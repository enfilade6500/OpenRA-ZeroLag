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

		// Outgoing packets wait here until the send thread runs, which then writes everything waiting in one
		// socket call. A frame close queues one packet per other client plus an ack for every connection, so
		// without this each of those N*N packets per frame cost a thread wake-up and a write of its own, and the
		// send threads were the largest single CPU cost of the server (see Server.CloseDueFrames, which holds
		// the sends for the duration of a close so that they go out as exactly one write per client).
		readonly object sendLock = new();
		readonly ManualResetEventSlim sendSignal = new(false);
		List<byte[]> pending = new();
		bool sendsHeld;
		bool sendCompleted;
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
				while (true)
				{
					if (!sendSignal.Wait(1000))
					{
						// The socket has been closed from the receive side; nothing more can be sent
						if (receiveLoopExited)
							return;

						continue;
					}

					// Take everything that is waiting, in order, and write it as one buffer
					List<byte[]> batch;
					bool completed;
					lock (sendLock)
					{
						sendSignal.Reset();
						batch = pending;
						pending = new List<byte[]>();
						completed = sendCompleted;
					}

					if (batch.Count > 0)
					{
						var data = batch.Count == 1 ? batch[0] : Concatenate(batch);
						var start = 0;
						while (start < data.Length)
							start += socket.Send(data, start, data.Length - start, SocketFlags.None);
					}

					if (completed)
						break;
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
			finally
			{
				sendLoopExited.Set();
			}
		}

		static byte[] Concatenate(List<byte[]> parts)
		{
			var total = 0;
			foreach (var part in parts)
				total += part.Length;

			var data = new byte[total];
			var offset = 0;
			foreach (var part in parts)
			{
				Buffer.BlockCopy(part, 0, data, offset, part.Length);
				offset += part.Length;
			}

			return data;
		}

		public bool TrySendData(byte[] data)
		{
			lock (sendLock)
			{
				if (sendCompleted)
					return false;

				pending.Add(data);
				if (!sendsHeld)
					sendSignal.Set();

				return true;
			}
		}

		/// <summary>
		/// Queue outgoing packets without waking the send thread until ReleaseSends, so that a burst of packets
		/// for this client (a frame close) goes out as one write. Always paired with ReleaseSends.
		/// </summary>
		public void HoldSends()
		{
			lock (sendLock)
				sendsHeld = true;
		}

		public void ReleaseSends()
		{
			lock (sendLock)
			{
				sendsHeld = false;
				if (pending.Count > 0)
					sendSignal.Set();
			}
		}

		public void Dispose()
		{
			// Tell the send thread to write what is left and close the stream
			lock (sendLock)
			{
				sendCompleted = true;
				sendsHeld = false;
				sendSignal.Set();
			}
		}
	}

	public enum ReceiveState { Header, Data }
}
