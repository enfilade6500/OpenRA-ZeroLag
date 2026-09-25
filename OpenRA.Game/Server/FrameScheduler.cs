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
using System.IO;
using System.Linq;

namespace OpenRA.Server
{
	/// <summary>
	/// Decides which orders go into which frame, on the server's own clock.
	///
	/// With the original scheme every client's packet for its local frame N was scheduled for frame
	/// N + OrderLatency, so every client needed every other client's packet for a frame before it could
	/// continue: one player's late packet froze the whole game.
	///
	/// Here the server "closes" one frame every net frame interval. A frame contains, for each client,
	/// everything that client has sent since the previous frame was closed: nothing if its packet has not
	/// arrived yet, or several packets merged together if it is catching up. A client with a slow or
	/// unreliable connection therefore only delays its own orders and its own view of the game; everyone
	/// else keeps playing.
	///
	/// Unmodified release clients support this without changes: frames are still sent to each client
	/// strictly in order with no gaps, and the sender learns which frame its packets were applied on from
	/// the existing Ack packet, whose count may be 0 (an empty frame) or more than 1 (merged packets).
	///
	/// Each client's playback is kept a small, steady distance behind the frames it receives by sending
	/// it a TickScale, so it neither runs dry nor builds up delay. A client that falls behind (a hitch,
	/// a burst of delayed packets) is told to run faster until it has caught up. If a client cannot keep
	/// up even when running faster (its computer is too slow), the whole game is slowed down smoothly for
	/// everyone instead. As a last resort, the server stops closing frames while any client is far behind,
	/// which is the same "wait for everyone" behaviour as the original scheme.
	/// </summary>
	public sealed class FrameScheduler
	{
		// How long a frame should wait in a client's buffer before the client needs it (ms).
		// This absorbs jitter on the connection from the server to the client.
		const int TargetSlack = 150;

		// Ignore slack errors smaller than this (ms)
		const int SlackDeadband = 40;

		// Corrections are recalculated this often (ms)
		const int Interval = 1000;

		// Correct only part of the measured error per interval, because the measurement lags behind
		const float Gain = 0.6f;

		// Limits on how much faster / slower a single client can be told to run
		const float MinTickScale = 0.7f;
		const float MaxTickScale = 1.6f;

		// Limits and steps for slowing down the whole game for a client that cannot keep up
		const float MaxPace = 1.5f;
		const float PaceDownStep = 0.01f;
		const float PaceFastDownStep = 0.03f;
		const float PaceHeadroom = 0.97f;

		// A client counts as unable to keep up after falling further behind for this many intervals in a
		// row despite being told to run faster. A one-off hitch makes it fall behind once and then recover.
		const int FallingBehindIntervalsBeforePaceChange = 3;

		// ...or after staying well behind for this many intervals, even if it is not falling further behind
		const int BehindIntervalsBeforePaceChange = 6;

		// Stop closing frames while a client is further behind than its normal position plus this (ms).
		// Shorter outages (Wi-Fi dropouts, TCP retransmissions) only affect that client; the client catches
		// up afterwards by running faster. Longer outages make everyone wait, like the original scheme.
		const int WindowSlack = 2000;

		// The client learns about merged packets through the Ack packet, whose count is a single byte
		const int MaxPacketsPerFrame = byte.MaxValue;

		// Close times are remembered for this many frames, to measure how long frames wait in client buffers
		const int MaxTrackedFrames = 512;

		sealed class ClientState
		{
			public readonly List<(int ClientFrame, byte[] Data)> Pending = new();
			public readonly List<long> SlackSamples = new();
			public int LastReportedFrame;
			public int Rtt;
			public bool HasRtt;
			public long LastBehind;
			public bool WasToldToSpeedUp;
			public int FallingBehindIntervals;
			public int BehindIntervals;
			public readonly Queue<(long Time, int Frame)> Progress = new();
		}

		readonly Dictionary<int, ClientState> clients = new();
		readonly Dictionary<int, long> closeTimes = new();
		readonly int timestep;
		readonly int netFrameInterval;
		readonly int ticksPerInterval;
		readonly int minTickMs;
		readonly int maxTickMs;
		readonly int firstFrame;

		bool started;
		int nextFrame;
		double nextCloseTime;
		long nextControlUpdate;
		float pace = 1f;
		bool blocked;

		/// <param name="timestep">World tick length for the selected game speed (ms).</param>
		/// <param name="netFrameInterval">World ticks per network frame.</param>
		/// <param name="firstFrame">The first frame that this scheduler will close (the frames before it were sent at game start).</param>
		/// <param name="clientIndices">The clients taking part in the game.</param>
		public FrameScheduler(int timestep, int netFrameInterval, int firstFrame, IEnumerable<int> clientIndices)
		{
			this.timestep = timestep;
			this.netFrameInterval = netFrameInterval;
			this.firstFrame = firstFrame;
			nextFrame = firstFrame;
			ticksPerInterval = Math.Max(1, Interval / timestep);
			minTickMs = Math.Max(1, (int)Math.Ceiling(MinTickScale * timestep));
			maxTickMs = Math.Max(timestep, (int)Math.Floor(MaxTickScale * timestep));

			foreach (var c in clientIndices)
				clients.TryAdd(c, new ClientState());
		}

		double FramePeriod => netFrameInterval * timestep * pace;

		/// <summary>A client sent the orders for its local frame <paramref name="clientFrame"/>.</summary>
		public void ReceivePacket(int client, int clientFrame, byte[] data, long now)
		{
			if (!clients.TryGetValue(client, out var state))
				return;

			state.Pending.Add((clientFrame, data));
			state.LastReportedFrame = Math.Max(state.LastReportedFrame, clientFrame);

			// The client sends the packet for frame N just before it simulates frame N, so the time frame N
			// spent in its buffer is the time since we closed it, minus the round trip
			if (state.HasRtt && clientFrame >= firstFrame && closeTimes.TryGetValue(clientFrame, out var closedAt))
				state.SlackSamples.Add(now - closedAt - state.Rtt);
		}

		/// <summary>Round-trip times from the connection's ping history (ms).</summary>
		public void ReceivePing(int client, int[] pingHistory)
		{
			if (!clients.TryGetValue(client, out var state) || pingHistory.Length == 0)
				return;

			var sorted = pingHistory.OrderBy(p => p).ToArray();
			state.Rtt = sorted[sorted.Length / 2];
			state.HasRtt = true;
		}

		public void RemoveClient(int client)
		{
			clients.Remove(client);
		}

		/// <summary>How long the server loop may sleep before this needs attention again (ms).</summary>
		public int MillisecondsUntilNextAction(long now)
		{
			// Nothing to schedule once everyone has left
			if (clients.Count == 0)
				return 1000;

			// Waiting for clients to load, or for a client that is far behind: check again shortly
			if (!started || blocked)
				return 5;

			var untilClose = (int)Math.Ceiling(nextCloseTime - now);
			var untilControl = (int)(nextControlUpdate - now);
			return Math.Max(0, Math.Min(untilClose, untilControl));
		}

		int WindowFrames(ClientState state)
		{
			var nominalPeriod = netFrameInterval * timestep;
			return (int)Math.Ceiling((double)(state.Rtt + TargetSlack + WindowSlack) / nominalPeriod) + 1;
		}

		/// <summary>
		/// If a frame is due, returns its number and, for every client, the order data to forward
		/// (the concatenation of all packets received since the last frame) and the number of packets
		/// it contains, for the Ack sent back to that client.
		/// </summary>
		public bool TryCloseFrame(long now, out int frame, out List<(int Client, byte[] Data, int PacketCount)> contents)
		{
			frame = 0;
			contents = null;

			if (clients.Count == 0)
				return false;

			if (!started)
			{
				// Wait until every client has finished loading and sent its first orders
				if (clients.Values.Any(c => c.LastReportedFrame < 1))
					return false;

				started = true;
				nextCloseTime = now;
				nextControlUpdate = now + Interval;
			}

			if (now < nextCloseTime)
				return false;

			// Last resort: don't run further ahead of a client than it can possibly be buffering
			blocked = clients.Values.Any(c => nextFrame - c.LastReportedFrame > WindowFrames(c));
			if (blocked)
				return false;

			frame = nextFrame++;
			contents = new List<(int, byte[], int)>(clients.Count);
			foreach (var (index, state) in clients)
			{
				var count = Math.Min(state.Pending.Count, MaxPacketsPerFrame);
				byte[] data;
				if (count == 0)
					data = Array.Empty<byte>();
				else if (count == 1)
					data = state.Pending[0].Data;
				else
				{
					var ms = new MemoryStream();
					for (var i = 0; i < count; i++)
						ms.Write(state.Pending[i].Data);

					data = ms.ToArray();
				}

				state.Pending.RemoveRange(0, count);
				contents.Add((index, data, count));
			}

			closeTimes[frame] = now;
			closeTimes.Remove(frame - MaxTrackedFrames);

			// Keep to the schedule, unless we have fallen more than a frame behind (e.g. after being blocked)
			var period = FramePeriod;
			nextCloseTime = now - nextCloseTime > period ? now + period : nextCloseTime + period;

			return true;
		}

		/// <summary>TickScale updates to send, recalculated once per interval.</summary>
		public List<(int Client, float Scale)> GetTickScales(long now)
		{
			var result = new List<(int, float)>();
			if (!started || now < nextControlUpdate)
				return result;

			nextControlUpdate = now + Interval;

			// How far behind its target position each client is (ms). Positive: frames are waiting in its
			// buffer for longer than needed, so it should run faster. Negative: it is close to running dry.
			var behind = new Dictionary<int, long>();
			foreach (var (index, state) in clients)
			{
				if (state.SlackSamples.Count == 0)
					continue;

				behind[index] = Median(state.SlackSamples) - TargetSlack;
				state.SlackSamples.Clear();
			}

			// A client that is well behind and keeps falling further behind even though it was told to run
			// faster cannot keep up: slow the whole game down a little. Speed back up once everyone is close.
			foreach (var state in clients.Values)
			{
				state.Progress.Enqueue((now, state.LastReportedFrame));
				while (state.Progress.Count > FallingBehindIntervalsBeforePaceChange + 1)
					state.Progress.Dequeue();
			}

			var nominalPeriod = netFrameInterval * timestep;
			foreach (var (index, b) in behind)
			{
				var state = clients[index];
				if (state.WasToldToSpeedUp && b > state.LastBehind)
					state.FallingBehindIntervals++;
				else
					state.FallingBehindIntervals = 0;

				if (state.WasToldToSpeedUp && b > 2 * nominalPeriod)
					state.BehindIntervals++;
				else
					state.BehindIntervals = 0;
			}

			var cannotKeepUp = false;
			foreach (var (index, b) in behind)
			{
				var state = clients[index];
				if (b <= 2 * nominalPeriod)
					continue;

				if (state.FallingBehindIntervals < FallingBehindIntervalsBeforePaceChange && state.BehindIntervals < BehindIntervalsBeforePaceChange)
					continue;

				// Match the game's pace to what this client has managed while running as fast as it can,
				// with a little headroom so it can work off its backlog
				var (startTime, startFrame) = state.Progress.Peek();
				var framesPerMs = (double)(state.LastReportedFrame - startFrame) / Math.Max(1, now - startTime);
				if (framesPerMs > 0)
					pace = Math.Max(pace, (float)Math.Min(MaxPace, 1 / (nominalPeriod * PaceHeadroom * framesPerMs)));

				state.FallingBehindIntervals = 0;
				state.BehindIntervals = 0;
				cannotKeepUp = true;
			}

			// Probe back towards full speed once every client is keeping up (a client that sent nothing
			// this interval may be stuck, so wait until it reports again)
			if (!cannotKeepUp && pace > 1f && behind.Count == clients.Count)
			{
				if (behind.Values.All(b => b <= nominalPeriod))
					pace = Math.Max(1f, pace - PaceFastDownStep);
				else if (behind.Values.All(b => b <= 2 * nominalPeriod))
					pace = Math.Max(1f, pace - PaceDownStep);
			}

			foreach (var (index, state) in clients)
			{
				var baseTickMs = timestep * pace;
				var tickMs = (int)Math.Round(baseTickMs);
				if (behind.TryGetValue(index, out var b))
				{
					if (Math.Abs(b) >= SlackDeadband)
						tickMs = (int)Math.Round(baseTickMs - Gain * b / ticksPerInterval);

					state.LastBehind = b;
				}

				var roundedBaseTickMs = (int)Math.Round(baseTickMs);
				tickMs = tickMs.Clamp(minTickMs, Math.Max(maxTickMs, (int)Math.Round(baseTickMs * 1.25f)));
				state.WasToldToSpeedUp = tickMs < roundedBaseTickMs;

				// Clients compute (int)(scale * timestep); the half millisecond keeps float rounding
				// from landing just below the intended value
				result.Add((index, tickMs == timestep ? 1f : (tickMs + 0.5f) / timestep));
			}

			return result;
		}

		static long Median(List<long> values)
		{
			var a = values.ToArray();
			Array.Sort(a);
			return a[a.Length / 2];
		}
	}
}
