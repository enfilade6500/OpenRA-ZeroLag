#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace OpenRA.Server
{
	public sealed class Connection : IDisposable
	{
		public const int MaxOrderLength = 131072;

		// Cap ping history at 15 seconds as a balance between expiring stale state and having enough data for decent statistics
		const int MaxPingSamples = 15;

		public readonly int PlayerIndex;
		public readonly string AuthToken;
		public readonly EndPoint EndPoint;
		public readonly Stopwatch ConnectionTimer = Stopwatch.StartNew();

		public long TimeSinceLastResponse => Game.RunTime - lastReceivedTime;

		public bool TimeoutMessageShown;
		public bool Validated;
		public int LastOrdersFrame;

		long lastReceivedTime = 0;

		readonly BlockingCollection<byte[]> sendQueue = new();
		readonly ManualResetEventSlim sendLoopExited = new(false);
		volatile bool receiveLoopExited;
		volatile bool sendFailed;
		readonly Queue<int> pingHistory = new();

		public Connection(Server server, Socket socket, string authToken)
		{
			PlayerIndex = server.ChooseFreePlayerIndex();
			AuthToken = authToken;
			EndPoint = socket.RemoteEndPoint;

			new Thread(SendReceiveLoop)
			{
				Name = $"Client communication ({EndPoint}",
				IsBackground = true
			}.Start((server, socket));
		}

		static byte[] CreatePingFrame()
		{
			var ms = new MemoryStream(21);
			ms.Write(13);
			ms.Write(0);
			ms.Write(0);
			ms.WriteByte((byte)OrderType.Ping);
			ms.Write(Game.RunTime);
			return ms.GetBuffer();
		}

		void SendReceiveLoop(object s)
		{
			var (server, socket) = ((Server, Socket))s;

			// Outgoing data is written by a dedicated thread (see SendLoop) as soon as it is queued.
			// This thread previously only flushed the send queue after Poll returned, which meant that
			// orders relayed to a client could sit in the queue for up to 100ms until that client sent
			// something itself. That delay was added to every order relay, and to the recovery from every
			// lockstep stall (when stalled clients stop sending and the Poll always runs to its timeout).
			socket.Blocking = true;
			socket.NoDelay = true;
			new Thread(SendLoop)
			{
				Name = $"Client send ({EndPoint})",
				IsBackground = true
			}.Start(socket);

			var receiveBuffer = new byte[1024];
			var readBuffer = new List<byte>();
			var state = ReceiveState.Header;
			var expectLength = 8;
			var frame = 0;
			var lastPingSent = Stopwatch.StartNew();

			try
			{
				while (true)
				{
					// Wait up to 100ms for data to arrive before checking whether the connection has been closed
					if (socket.Poll(100000, SelectMode.SelectRead))
					{
						var read = socket.Receive(receiveBuffer);
						if (read == 0)
						{
							// Empty packet signals that the client has been dropped
							return;
						}

						if (read > 0)
						{
							readBuffer.AddRange(receiveBuffer.Take(read));
							lastReceivedTime = Game.RunTime;
							TimeoutMessageShown = false;
						}

						while (readBuffer.Count >= expectLength)
						{
							var bytes = readBuffer.GetRange(0, expectLength).ToArray();
							readBuffer.RemoveRange(0, expectLength);

							switch (state)
							{
								case ReceiveState.Header:
								{
									expectLength = BitConverter.ToInt32(bytes, 0) - 4;
									frame = BitConverter.ToInt32(bytes, 4);
									state = ReceiveState.Data;

									if (expectLength < 0 || (server.IsMultiplayer && expectLength > MaxOrderLength))
									{
										Log.Write("server", $"Closing socket connection to {EndPoint} because of excessive order length: {expectLength}");
										return;
									}

									break;
								}

								case ReceiveState.Data:
								{
									// Ping packets are sent and processed internally within this thread to reduce
									// server-introduced latencies from polling loops
									if (expectLength == 10 && bytes[0] == (byte)OrderType.Ping)
									{
										if (pingHistory.Count == MaxPingSamples)
											pingHistory.Dequeue();

										pingHistory.Enqueue((int)(Game.RunTime - BitConverter.ToInt64(bytes, 1)));
										server.OnConnectionPing(this, pingHistory.ToArray(), bytes[9]);
									}
									else
										server.OnConnectionPacket(this, frame, bytes);

									expectLength = 8;
									state = ReceiveState.Header;

									break;
								}
							}
						}
					}

					// Client has been dropped by the server (or sending failed) and all queued data has been sent
					if (sendLoopExited.IsSet)
					{
						if (!sendFailed)
							WaitForClientToClose(socket, receiveBuffer);

						return;
					}

					// Regularly check player ping. During games the round trip time is also used to pace
					// the clients (see FrameScheduler), so it is measured more often.
					var pingInterval = server.State == ServerState.GameStarted ? 250 : 1000;
					if (lastPingSent.ElapsedMilliseconds > pingInterval && TrySendData(CreatePingFrame()))
						lastPingSent.Restart();
				}
			}
			catch (SocketException e)
			{
				Log.Write("server", $"Closing socket connection to {EndPoint} because of socket error: {e}");
			}
			finally
			{
				receiveLoopExited = true;
				server.OnConnectionDisconnect(this);
				socket.Dispose();
			}
		}

		/// <summary>
		/// The send loop has shut down our side of the connection after sending the final messages
		/// (e.g. the reason the client was kicked). Give the client a moment to read them and close its
		/// side: closing a socket that still has unread incoming data resets the connection, which can
		/// make the client discard the final messages before it has read them.
		/// </summary>
		static void WaitForClientToClose(Socket socket, byte[] buffer)
		{
			var timer = Stopwatch.StartNew();
			try
			{
				while (timer.ElapsedMilliseconds < 2000)
					if (socket.Poll(100000, SelectMode.SelectRead) && socket.Receive(buffer) == 0)
						return;
			}
			catch (SocketException) { }
			catch (ObjectDisposedException) { }
		}

		void SendLoop(object s)
		{
			var socket = (Socket)s;
			try
			{
				while (!sendQueue.IsCompleted)
				{
					if (!sendQueue.TryTake(out var data, 1000))
					{
						// The socket has been closed from the receive side; nothing more can be sent
						if (receiveLoopExited)
							return;

						continue;
					}

					var start = 0;
					while (start < data.Length)
						start += socket.Send(data, start, data.Length - start, SocketFlags.None);
				}

				// All queued data has been sent: signal the end of the stream to the client
				socket.Shutdown(SocketShutdown.Send);
			}
			catch (SocketException e)
			{
				sendFailed = true;
				if (!receiveLoopExited)
					Log.Write("server", $"Closing socket connection to {EndPoint} because of socket error: {e}");
			}
			catch (ObjectDisposedException)
			{
				// The receive loop closed the socket
				sendFailed = true;
			}
			catch (InvalidOperationException)
			{
				// The send queue was completed while we were waiting on it
			}
			finally
			{
				sendLoopExited.Set();
			}
		}

		public bool TrySendData(byte[] data)
		{
			if (sendQueue.IsAddingCompleted)
				return false;

			try
			{
				sendQueue.Add(data);
				return true;
			}
			catch (InvalidOperationException)
			{
				// Occurs if the collection is marked completed for adding by another thread.
				return false;
			}
		}

		public void Dispose()
		{
			// Tell the sendReceiveThread that the socket should be closed
			sendQueue.CompleteAdding();
		}
	}

	public enum ReceiveState { Header, Data }
}
