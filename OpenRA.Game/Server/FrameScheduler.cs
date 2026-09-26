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
	/// Spectators, and players who have been defeated, are kept in lockstep and relayed like everyone else,
	/// but the game never waits for them and is never slowed down for them: if they cannot keep up they
	/// fall behind on their own.
	///
	/// Each client's playback is kept a small, steady distance behind the frames it receives by sending
	/// it a TickScale, so it neither runs dry nor builds up delay. A client that falls behind (a hitch,
	/// a burst of delayed packets) is told to run faster until it has caught up. If a client cannot keep
	/// up even when running faster (its computer is too slow), the whole game is slowed down smoothly for
	/// everyone instead, but never below a configurable floor (Server.MinGameSpeed): a player whose computer
	/// would need the game slower than that is left to fall behind on their own, like a spectator, rather than
	/// dragging everyone down to a pace that would not have kept them in the game anyway.
	///
	/// As a last resort, the server stops closing frames while a player has stopped responding and is far
	/// behind, which is the same "wait for everyone" behaviour as the original scheme and keeps the familiar
	/// connection-problems / vote-kick flow. A player who is still sending frames, however slowly, never
	/// holds the game.
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

		// Steps for slowing down the whole game for a client that cannot keep up (the limit is Server.MinGameSpeed).
		// With no configured floor the game can still not be slowed below this: a computer managing less than
		// 10% of normal speed is not meaningfully in the game, and longer ticks would be indistinguishable
		// from a frozen client to the players.
		const float PaceDownStep = 0.01f;
		const float PaceSanityLimit = 10f;
		const float PaceFastDownStep = 0.03f;
		const float PaceHeadroom = 0.97f;

		// A client counts as unable to keep up after falling further behind for this many intervals in a
		// row despite being told to run faster. A one-off hitch makes it fall behind once and then recover.
		const int FallingBehindIntervalsBeforePaceChange = 3;

		// ...or after staying well behind for this many intervals, even if it is not falling further behind
		const int BehindIntervalsBeforePaceChange = 6;

		// Stop closing frames while a client is further behind than its normal position plus this (ms),
		// and has stopped sending. Shorter outages (Wi-Fi dropouts, TCP retransmissions) only affect that
		// client; the client catches up afterwards by running faster. Longer outages make everyone wait,
		// like the original scheme.
		const int WindowSlack = 2000;

		// A client that has not reported a new frame for this long has stopped (a dead connection or a
		// frozen game) rather than merely being slow. Only stopped clients can make everyone wait.
		const int StalledThreshold = 2000;

		// Waits shorter than this are not logged individually; they are counted and reported in aggregate
		const int LoggedWaitThreshold = 500;

		// The client learns about merged packets through the Ack packet, whose count is a single byte
		const int MaxPacketsPerFrame = byte.MaxValue;

		// Close times are remembered for this many frames, to measure how long frames wait in client buffers.
		// Players cannot fall this far behind (the window above stops the game first), but spectators can.
		const int MaxTrackedFrames = 2048;

		// Players further behind than this are listed in the server log (ms), at most this often (ms)
		const int ReportBehindThreshold = 500;
		const int ReportInterval = 10000;

		sealed class ClientState
		{
			public readonly List<(int ClientFrame, byte[] Data)> Pending = new();
			public readonly List<long> SlackSamples = new();
			public int LastReportedFrame;
			public long LastProgressTime;
			public int Rtt;
			public bool HasRtt;
			public long LastBehind;
			public bool WasToldToSpeedUp;
			public int FallingBehindIntervals;
			public int BehindIntervals;
			public readonly Queue<(long Time, int Frame)> Progress = new();
			public bool IsSpectator;
			public bool IsDefeated;
			public bool IsTooSlow;

			// Nobody waits for, or is slowed down for, a client that is not (or no longer) playing, or whose
			// computer is too slow to be kept in the game by slowing everyone else down
			public bool ExemptFromPacing => IsSpectator || IsDefeated || IsTooSlow;

			// The speed (as a fraction of normal) the client was last told to run at
			public float RequestedSpeed = 1f;

			// What the client's computer has been observed to manage (as a fraction of normal speed). A client
			// in lockstep can never run ahead of the frames it has been sent, so its true capacity only shows
			// while it is working through a backlog: the fastest rate seen then is a lower bound (PeakSpeed),
			// and the rate it managed while it was the one slowing the game down is a measurement (SlowestSpeed).
			public double PeakSpeed;
			public double SlowestSpeed;

			// Accumulated for the end-of-game summary
			public long MaxBehind;
			public double SumBehind;
			public int BehindSamples;
			public int SlowdownsCaused;
			public bool Seen;
		}

		readonly Dictionary<int, ClientState> clients = new();
		readonly Dictionary<int, long> closeTimes = new();
		readonly int timestep;
		readonly int netFrameInterval;
		readonly int ticksPerInterval;
		readonly int minTickMs;
		readonly int maxTickMs;
		readonly int firstFrame;
		readonly int lagBudget;
		readonly int windowSlack;
		readonly float maxPace;
		readonly Func<int, string> describeClient;
		readonly Action<string> log;

		bool started;
		int nextFrame;
		double nextCloseTime;
		long nextControlUpdate;
		float pace = 1f;
		bool blocked;
		long blockedSince;
		long lastResumeTime = long.MinValue;
		string blockedBy;
		int shortWaits;
		long shortWaitMs;
		long nextBehindReport;
		int slowestClient = -1;
		double slowestClientSpeed;

		/// <summary>The current game speed as a percentage of normal (100 when the game is not slowed down).</summary>
		public int SpeedPercent => (int)Math.Round(100 / pace);

		/// <summary>
		/// The player whose computer the game is currently slowed down for, or null when the game runs at full
		/// speed or that player has left. The speed is the fraction of normal speed their computer managed.
		/// </summary>
		public (int Client, double Speed)? SlowestPlayer =>
			pace > 1f && slowestClient >= 0 && clients.TryGetValue(slowestClient, out var state) && !state.ExemptFromPacing
				? (slowestClient, slowestClientSpeed) : null;

		/// <summary>Players whose computers would need the game slower than the floor, and are falling behind on their own.</summary>
		public IEnumerable<int> TooSlowPlayers => clients.Where(c => c.Value.IsTooSlow).Select(c => c.Key);

		/// <summary>The player most recently found to need the game slower than the floor (-1 if none).</summary>
		public int LastTooSlowPlayer { get; private set; } = -1;

		/// <summary>The lowest game speed (percent) the scheduler will slow the game to; 100 / maxPace.</summary>
		public int MinSpeedPercent => (int)Math.Round(100 / maxPace);

		/// <param name="timestep">World tick length for the selected game speed (ms).</param>
		/// <param name="netFrameInterval">World ticks per network frame.</param>
		/// <param name="firstFrame">The first frame that this scheduler will close (the frames before it were sent at game start).</param>
		/// <param name="clientIndices">The clients taking part in the game.</param>
		/// <param name="maxPlayerLag">How far behind (ms) a player whose computer can't keep up may fall before the
		/// game is slowed down for everyone. 0 slows the game down as soon as a player can't keep up.</param>
		/// <param name="describeClient">Returns a player's name, for the log.</param>
		/// <param name="log">Writes a line to the server log (not to the players).</param>
		/// <param name="spectatorIndices">Clients watching rather than playing. Their orders are still relayed and kept
		/// in lockstep, but the game never waits for them and is never slowed down for them: a spectator who cannot keep
		/// up simply falls behind on their own.</param>
		/// <param name="minGameSpeed">The game is never slowed down below this percentage of normal speed for a slow
		/// computer; a player who would need less is left to fall behind on their own. 0 (the default) means no
		/// floor: like the original scheme, the game follows the slowest player's computer however slow it is,
		/// and it is up to the players to vote-kick if they do not want to wait.</param>
		public FrameScheduler(int timestep, int netFrameInterval, int firstFrame, IEnumerable<int> clientIndices,
			int maxPlayerLag = 0, Func<int, string> describeClient = null, Action<string> log = null,
			IEnumerable<int> spectatorIndices = null, int minGameSpeed = 0)
		{
			maxPace = minGameSpeed <= 0 ? PaceSanityLimit : Math.Min(PaceSanityLimit, 100f / minGameSpeed.Clamp(10, 100));
			this.timestep = timestep;
			this.netFrameInterval = netFrameInterval;
			this.firstFrame = firstFrame;
			this.describeClient = describeClient ?? (c => $"client {c}");
			this.log = log ?? (_ => { });

			// A player may always fall two frames behind before the game is slowed down for them
			lagBudget = Math.Max(2 * netFrameInterval * timestep, maxPlayerLag);

			// Don't make everyone wait for a player who is still within the lag budget
			windowSlack = Math.Max(WindowSlack, lagBudget + 1000);
			nextFrame = firstFrame;
			ticksPerInterval = Math.Max(1, Interval / timestep);
			minTickMs = Math.Max(1, (int)Math.Ceiling(MinTickScale * timestep));
			maxTickMs = Math.Max(timestep, (int)Math.Floor(MaxTickScale * timestep));

			foreach (var c in clientIndices)
				clients.TryAdd(c, new ClientState());

			if (spectatorIndices != null)
				foreach (var c in spectatorIndices)
					if (clients.TryGetValue(c, out var state))
						state.IsSpectator = true;
		}

		double FramePeriod => netFrameInterval * timestep * pace;

		/// <summary>A client sent the orders for its local frame <paramref name="clientFrame"/>.</summary>
		public void ReceivePacket(int client, int clientFrame, byte[] data, long now)
		{
			if (!clients.TryGetValue(client, out var state))
				return;

			state.Pending.Add((clientFrame, data));
			if (clientFrame > state.LastReportedFrame)
			{
				state.LastReportedFrame = clientFrame;
				state.LastProgressTime = now;
			}

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

		/// <summary>A player has been defeated: keep relaying their orders, but stop waiting for them.</summary>
		public void SetDefeated(int client)
		{
			if (!clients.TryGetValue(client, out var state) || state.IsDefeated || state.IsSpectator)
				return;

			state.IsDefeated = true;
			log($"{describeClient(client)} has been defeated; the game will no longer wait for them.");
		}

		public void RemoveClient(int client)
		{
			if (clients.TryGetValue(client, out var state) && state.Seen && !state.IsSpectator)
			{
				var avg = state.BehindSamples > 0 ? state.SumBehind / state.BehindSamples : 0;
				var computer = state.SlowestSpeed > 0 ? $" Their computer managed {state.SlowestSpeed * 100:F0}% while it was slowing the game" : "";
				if (state.PeakSpeed > 0)
					computer += (computer.Length > 0 ? ", and" : " Their computer managed") + $" at least {state.PeakSpeed * 100:F0}% at best";

				log($"Summary for {describeClient(client)}: worst {state.MaxBehind / 1000f:F1}s behind, " +
					$"average {avg / 1000f:F2}s; caused {state.SlowdownsCaused} slowdown(s)." + (computer.Length > 0 ? computer + "." : ""));
			}

			if (client == slowestClient)
				slowestClient = -1;

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
			return (int)Math.Ceiling((double)(state.Rtt + TargetSlack + windowSlack) / nominalPeriod) + 1;
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

			// Last resort: don't run further ahead of a player who has stopped responding than they could
			// possibly be buffering. A player who is still sending frames, however slowly, never holds the game.
			var waitingFor = clients.Where(c => !c.Value.ExemptFromPacing
				&& nextFrame - c.Value.LastReportedFrame > WindowFrames(c.Value)
				&& now - c.Value.LastProgressTime > StalledThreshold).Select(c => c.Key).ToList();
			var wasBlocked = blocked;
			blocked = waitingFor.Count > 0;
			if (blocked)
			{
				if (!wasBlocked)
				{
					blockedSince = now;
					blockedBy = string.Join(", ", waitingFor.Select(describeClient));

					// Don't log the start of every wait in a rapid sequence; they are reported in aggregate
					if (now - lastResumeTime > 1000)
						log($"Everyone is waiting for {blockedBy}, who has stopped responding.");
				}

				return false;
			}

			if (wasBlocked)
			{
				lastResumeTime = now;
				var waited = now - blockedSince;
				if (waited >= LoggedWaitThreshold)
					log($"Game resumed after waiting {waited / 1000f:F1}s for {blockedBy}.");
				else
				{
					shortWaits++;
					shortWaitMs += waited;
				}
			}

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

				var b = Median(state.SlackSamples) - TargetSlack;
				behind[index] = b;
				state.SlackSamples.Clear();

				// Summary stats (only count real lateness, not being ahead of schedule)
				state.Seen = true;
				if (b > 0)
				{
					state.MaxBehind = Math.Max(state.MaxBehind, b);
					state.SumBehind += b;
				}

				state.BehindSamples++;
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

				// A client that was told to run faster and spent the whole interval with a backlog (behind at the start
				// and still behind at the end, so it never ran out of frames) was limited only by its computer, or by the
				// speed it was asked for. Either way the rate it managed is a lower bound on what its computer can do.
				if (state.WasToldToSpeedUp && state.LastBehind > nominalPeriod && b > SlackDeadband && state.Progress.Count >= 2)
				{
					var snapshots = state.Progress.ToArray();
					var (startTime, startFrame) = snapshots[^2];
					var (endTime, endFrame) = snapshots[^1];
					var achieved = (endFrame - startFrame) * (double)nominalPeriod / Math.Max(1, endTime - startTime);
					state.PeakSpeed = Math.Max(state.PeakSpeed, Math.Min(achieved, state.RequestedSpeed));
				}
			}

			// A player whose computer was too slow has caught up again (it may have recovered): pace them normally
			foreach (var (index, b) in behind)
			{
				var state = clients[index];
				if (state.IsTooSlow && b <= lagBudget)
				{
					state.IsTooSlow = false;
					state.FallingBehindIntervals = 0;
					state.BehindIntervals = 0;
					log($"{describeClient(index)} has caught up; the game will be slowed down for them again if needed.");
				}
			}

			var cannotKeepUp = false;
			var oldPace = pace;
			foreach (var (index, b) in behind)
			{
				var state = clients[index];
				if (state.ExemptFromPacing || b <= lagBudget)
					continue;

				if (state.FallingBehindIntervals < FallingBehindIntervalsBeforePaceChange && state.BehindIntervals < BehindIntervalsBeforePaceChange)
					continue;

				// Match the game's pace to what this client has managed while running as fast as it can,
				// with a little headroom so it can work off its backlog
				var (startTime, startFrame) = state.Progress.Peek();
				var framesPerMs = (double)(state.LastReportedFrame - startFrame) / Math.Max(1, now - startTime);
				if (framesPerMs > 0)
				{
					var needed = (float)(1 / (nominalPeriod * PaceHeadroom * framesPerMs));
					if (needed > maxPace)
					{
						// Slowing everyone down to the floor would not keep this player in the game anyway,
						// so don't make the others pay for it: this player falls behind on their own instead
						state.IsTooSlow = true;
						state.FallingBehindIntervals = 0;
						state.BehindIntervals = 0;
						LastTooSlowPlayer = index;
						log($"{describeClient(index)}'s computer is too slow to keep up (managing {framesPerMs * nominalPeriod * 100:F0}%, " +
							$"{b / 1000f:F1}s behind); the game will not be slowed down below {100 / maxPace:F0}% for them, so they will fall behind on their own.");
						continue;
					}

					var managed = framesPerMs * nominalPeriod;
					if (needed > pace + 0.005f)
					{
						log($"Slowing the game to {100 / needed:F0}% of normal speed so that {describeClient(index)} can keep up " +
							$"(their computer is managing {managed * 100:F0}% and is {b / 1000f:F1}s behind).");
						state.SlowdownsCaused++;
					}

					// This player needs the game at least as slow as it is, so they are the one it is slowed down for
					if (needed > 1.005f && needed >= pace - 0.005f)
					{
						slowestClient = index;
						slowestClientSpeed = managed;
						state.SlowestSpeed = state.SlowestSpeed > 0 ? Math.Min(state.SlowestSpeed, managed) : managed;
					}

					pace = Math.Max(pace, needed);
				}

				state.FallingBehindIntervals = 0;
				state.BehindIntervals = 0;
				cannotKeepUp = true;
			}

			// Probe back towards full speed once every client is keeping up (a client that sent nothing
			// this interval may be stuck, so wait until it reports again)
			// Only start speeding back up once everyone is comfortably inside the lag budget, not right at its
			// edge, so that the game does not alternate between slowing down and speeding up
			var playerBehind = behind.Where(b => !clients[b.Key].ExemptFromPacing).Select(b => b.Value).ToList();
			var recoveryThreshold = Math.Max(nominalPeriod, lagBudget / 2);
			if (!cannotKeepUp && pace > 1f && playerBehind.Count == clients.Values.Count(c => !c.ExemptFromPacing))
			{
				if (playerBehind.All(b => b <= nominalPeriod))
					pace = Math.Max(1f, pace - PaceFastDownStep);
				else if (playerBehind.All(b => b <= recoveryThreshold))
					pace = Math.Max(1f, pace - PaceDownStep);

				if (pace == 1f && oldPace > 1f)
				{
					slowestClient = -1;
					log("The game is back to full speed.");
				}
			}

			// Periodically list players who are falling behind, so problems can be traced to a player
			if (now >= nextBehindReport)
			{
				if (shortWaits > 0)
				{
					log($"The game paused {shortWaits} time(s) briefly ({shortWaitMs / 1000f:F1}s in total) waiting for {blockedBy}.");
					shortWaits = 0;
					shortWaitMs = 0;
				}

				var lagging = behind.Where(b => b.Value > ReportBehindThreshold).OrderByDescending(b => b.Value).ToList();
				if (lagging.Count > 0)
				{
					nextBehindReport = now + ReportInterval;
					var speed = pace > 1f ? $" Game speed {100 / pace:F0}%." : "";
					log("Players behind: " + string.Join(", ", lagging.Select(b =>
						$"{describeClient(b.Key)}{(clients[b.Key].IsSpectator ? " (spectator)" : clients[b.Key].IsDefeated ? " (defeated)" : clients[b.Key].IsTooSlow ? " (too slow)" : "")} {b.Value / 1000f:F1}s")) + "." + speed);
				}
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
				else if (state.ExemptFromPacing && nextFrame - state.LastReportedFrame > WindowFrames(state))
				{
					// A spectator or defeated player so far behind that its frames are no longer tracked (or that
					// sent nothing this interval) gets no lateness samples. Keep telling it to run flat out so that
					// it can catch up if its computer recovers, instead of being stuck behind for the rest of the game.
					tickMs = minTickMs;
				}

				var roundedBaseTickMs = (int)Math.Round(baseTickMs);
				tickMs = tickMs.Clamp(minTickMs, Math.Max(maxTickMs, (int)Math.Round(baseTickMs * 1.25f)));
				state.WasToldToSpeedUp = tickMs < roundedBaseTickMs;
				state.RequestedSpeed = (float)timestep / tickMs;

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
