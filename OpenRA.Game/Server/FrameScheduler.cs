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
	/// <para>
	/// Decides which orders go into which frame, on the server's own clock.
	/// </para>
	/// <para>
	/// With the original scheme every client's packet for its local frame N was scheduled for frame
	/// N + OrderLatency, so every client needed every other client's packet for a frame before it could
	/// continue: one player's late packet froze the whole game.
	/// </para>
	/// <para>
	/// Here the server "closes" one frame every net frame interval. A frame contains, for each client,
	/// everything that client has sent since the previous frame was closed: nothing if its packet has not
	/// arrived yet, or several packets merged together if it is catching up. A client with a slow or
	/// unreliable connection therefore only delays its own orders and its own view of the game; everyone
	/// else keeps playing.
	/// </para>
	/// <para>
	/// Unmodified release clients support this without changes: frames are still sent to each client
	/// strictly in order with no gaps, and the sender learns which frame its packets were applied on from
	/// the existing Ack packet, whose count may be 0 (an empty frame) or more than 1 (merged packets).
	/// </para>
	/// <para>
	/// Spectators, and players who have been defeated, are kept in lockstep and relayed like everyone else,
	/// but the game never waits for them and is never slowed down for them: if they cannot keep up they
	/// fall behind on their own.
	/// </para>
	/// <para>
	/// Each client's playback is kept a steady distance behind the frames it receives by sending it a
	/// TickScale, so it neither runs dry nor builds up delay. That distance is small (150 ms) for a good
	/// connection and grows, up to Server.MaxPlayerBuffer, for a connection whose dropouts have stopped the
	/// client's game: the time a dropout cost is turned into buffer, so that the next dropout of that length
	/// is played through instead. A client that has fallen behind (a hitch, a dropout longer than its buffer)
	/// is told to run faster in proportion to how far behind it is, up to Server.MaxCatchUpSpeed.
	/// </para>
	/// <para>
	/// A client's shortfall is attributed before anything acts on it. Gaps in its packet arrivals are holes
	/// (its connection dropped out, or its game froze); the rest is the smooth shortfall of a computer that
	/// cannot keep up. Only the smooth shortfall can slow the whole game, which is then slowed smoothly for
	/// everyone to the rate that computer sustains between holes, but never below Server.MinGameSpeed: a
	/// player whose computer would need the game slower than that is left to fall behind on their own, like
	/// a spectator, and catches back up if its load drops. The game speeds back up by probing: gently at
	/// first, faster while it succeeds, and after a failed probe it waits before trying again.
	/// </para>
	/// <para>
	/// As a last resort, the server stops closing frames while a player who was keeping up has stopped
	/// responding and is far behind, for at most Server.MaxWaitForStalledPlayer, and then continues without
	/// them. A player who is still sending frames, however slowly, never holds the game.
	/// </para>
	/// </summary>
	public sealed class FrameScheduler
	{
		// How long a frame should wait in a client's buffer before the client needs it (ms), unless the client's
		// connection has shown that it needs more (see the adaptive buffer below). This absorbs ordinary jitter
		// on the connection from the server to the client.
		const int BaseSlack = 150;

		// Ignore slack errors smaller than this (ms)
		const int SlackDeadband = 40;

		// Corrections are recalculated this often (ms)
		const int Interval = 1000;

		// Correct only part of the measured error per interval, because the measurement lags behind
		const float Gain = 0.6f;

		// Limits on how much faster / slower a single client can be told to run for ordinary corrections. A client
		// well behind is asked for more, up to Server.MaxCatchUpSpeed (see CatchUpPerSecondBehind below).
		const float MinTickScale = 0.7f;
		const float MaxTickScale = 1.6f;

		// A client far behind is asked to run about (1 + this * seconds behind) times normal speed: half a second behind
		// is a gentle 1.3x, five seconds is flat out. Because the client is told once per interval, and the measured
		// lateness is already half an interval old, the same fraction as Gain means it closes only part of its deficit
		// per interval and lands on its target without overshooting into running dry.
		const float CatchUpPerSecondBehind = Gain;

		// A gap between two packets from a client longer than this many frame periods, and than this multiple of the
		// client's own typical gap, is a hole: the client sent nothing for that long. Holes are how connection dropouts
		// (and frozen game windows) show up, as opposed to a client that is merely slow, whose packets keep coming at
		// a steady but slower cadence (which is why the threshold follows its cadence).
		const float HoleThresholdPeriods = 2.5f;
		const float HoleThresholdCadence = 3f;
		const int CadenceSamples = 24;

		// A second packet within this many ms of the first one after a hole means a burst: everything the client queued
		// while its upload path was stalled arrives together. Its simulation did not stop, only its packets were held
		// up. A client that had genuinely stopped resumes one packet per net frame (30 ms even at 4x), never two at once.
		const int BurstWindowMs = 20;

		// A client whose game froze (a hitch, a dragged window) looks the same from here as one whose download path
		// died: nothing arrives, then it resumes at its normal cadence (the release client answers pings on its game
		// thread, so those stop too). The one thing that tells them apart is the buffer: a download dropout shorter
		// than the client's buffer is played through and leaves no hole at all, so a hole that the buffer should have
		// covered can only be a freeze. Until a client has a buffer, holes are taken to be dropouts. A freeze that
		// shows a buffer to be pointless takes it away again, and stops it growing for this long (ms).
		const int FreezeMemory = 300000;

		// Adaptive buffer: a download-side hole that stopped a client's game is turned into buffer, so that the next
		// hole of that length does not stop it, once such holes have happened twice within this window (ms): a single
		// hiccup costs nobody any delay. The buffer shrinks back with this half-life (ms) when the connection has been
		// quiet, by running the client imperceptibly fast.
		const int RecurrenceWindow = 120000;
		const int BufferHalfLife = 120000;

		// A player is told once, privately, when their buffer first grows past this (ms); the log notes growth in these steps (ms)
		const int BufferNoticeThreshold = 400;
		const int BufferLogStep = 250;

		// Holes are remembered this long (ms) for attribution and the per-player summary
		const int HoleMemory = 300000;

		// A client behind because of holes is logged as such at most this often (ms)
		const int HoleReportInterval = 60000;

		// Slowing down the whole game for a client that cannot keep up (the limit is Server.MinGameSpeed).
		// With no configured floor the game can still not be slowed below this: a computer managing less than
		// 10% of normal speed is not meaningfully in the game, and longer ticks would be indistinguishable
		// from a frozen client to the players. The pace is set a little below what the client managed, so
		// that it can work off its backlog.
		const float PaceSanityLimit = 10f;
		const float PaceHeadroom = 0.97f;

		// After a slowdown the game is held near the ceiling that was measured, and the hold reads the player's
		// backlog in net frames: how many frames the server has closed beyond the last one they reported, less the
		// fewest ever seen for them (the frames always in flight on their connection), so that a constant in the
		// lateness measure, such as an underestimated round trip, cannot pass for a backlog. The speed creeps up by
		// CreepRate points per second while the backlog is at most CreepBacklog frames and the player's deficit (the
		// backlog's growth as a fraction of the frames the server closes) is within CreepDeficitDeadband, or the growth
		// within CreepGrowthFloor frames per second (the lag is sampled to the frame, so a frame of jitter is not
		// growth, whatever the pace); a larger deficit pulls the speed back by CreepDeficitGain times the deficit
		// beyond that, as a share of the speed, per second, and a backlog beyond CreepPullBacklog frames by
		// CreepLevelGain points per second per frame, moving at most CreepRampLimit points per second either way and
		// never more than CreepFloor below the measured ceiling. (With a lag budget smaller than the usual three
		// seconds the backlog thresholds shrink with it.) The creep is a continuous, imperceptible probe and the pull-back is
		// its answer: a computer at a steady ceiling settles a point or two under it with no further speed changes.
		// Once that player has kept up (backlog small, deficit within the deadband) for CreepFreeIntervals control intervals with
		// the speed CreepAboveCeiling percent above the measured ceiling (a measurement is a few percent off either
		// way; the creep settles that on its own), their ceiling has moved up and a real probe starts. A growing or
		// large backlog resets the count; intervals around a connection hole say nothing about the computer and
		// count neither way.
		const float CreepRate = 0.3f;
		const float CreepRampLimit = 1f;
		const float CreepFloor = 0.06f;
		const int CreepFreeIntervals = 15;
		const int CreepAboveCeiling = 3;
		const int CreepBacklog = 2;
		const int CreepPullBacklog = 4;
		const float CreepDeficitDeadband = 0.05f;
		const float CreepGrowthFloor = 0.5f;
		const float CreepDeficitGain = 1f;
		const float CreepLevelGain = 0.3f;

		// Probing back towards full speed: the speed is raised by ProbeRate points per second at first, doubling
		// every ProbeDoublingTime (ms) while everyone keeps up, up to MaxProbeRate. When the player the game was
		// slowed down for leaves, the probe starts at LeaveProbeRate instead (the others were never tested above
		// the old speed, so the game does not jump).
		const float ProbeRate = 0.5f;
		const float MaxProbeRate = 4f;
		const int ProbeDoublingTime = 10000;
		const float LeaveProbeRate = 2f;

		// A probe has failed when a player who was told to run faster has a backlog of at least ProbeFailureBacklog
		// frames that has grown by at least ProbeFailureGrowth frames over the last FallingBehindIntervalsBeforePaceChange
		// intervals: enough to tell a computer at its ceiling from the small, corrected lag every client picks up while
		// the frame period shrinks. The game then goes back to the speed that player was measured managing (never below
		// where the probe started), and the creeping hold resumes.
		const int ProbeFailureBacklog = 3;
		const int ProbeFailureGrowth = 2;

		// A player is slowed down for once they are more than the lag budget behind, or once they are more than
		// ReportBehindThreshold behind and falling behind fast enough to pass the budget within this long (ms):
		// waiting out the whole budget when the outcome is already clear only costs everyone more catching up.
		const int TriggerHorizon = 10000;

		// The floor (Server.MinGameSpeed) leaves a player behind to protect the others; with fewer players than this
		// there is no majority to protect, and leaving one behind ends the game for everyone. A two-player game
		// follows the slower computer however slow it is.
		const int MinPlayersForFloor = 3;

		// A client counts as unable to keep up after falling further behind for this many intervals in a
		// row despite being told to run faster. A one-off hitch makes it fall behind once and then recover.
		const int FallingBehindIntervalsBeforePaceChange = 3;

		// ...or after staying well behind for this many intervals, even if it is not falling further behind
		const int BehindIntervalsBeforePaceChange = 6;

		// Stop closing frames while a client is further behind than its normal position plus this (ms),
		// and has stopped sending. Shorter outages (Wi-Fi dropouts, TCP retransmissions) only affect that
		// client; the client catches up afterwards by running faster. Longer outages make everyone wait,
		// like the original scheme, but only for up to Server.MaxWaitForStalledPlayer.
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
			public readonly Queue<long> RecentBehind = new();

			// The backlog in net frames (see CreepRate): frames closed beyond the client's last report, sampled once
			// per control interval, the fewest ever seen (the frames in flight on its connection), and recent samples
			public int LagFrames;
			public int MinLagFrames = int.MaxValue;
			public readonly Queue<int> RecentLagFrames = new();
			public int Backlog => MinLagFrames == int.MaxValue ? 0 : Math.Max(0, LagFrames - MinLagFrames);

			/// <summary>Growth of the frame lag over the recent samples, in frames per interval.</summary>
			public float LagGrowth => RecentLagFrames.Count < 2 ? 0 : (float)(LagFrames - RecentLagFrames.Peek()) / (RecentLagFrames.Count - 1);

			/// <summary>Growth of the frame lag over the whole window of recent samples, in frames.</summary>
			public int LagGrowthOverWindow => RecentLagFrames.Count < FallingBehindIntervalsBeforePaceChange + 1 ? 0 : LagFrames - RecentLagFrames.Peek();
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

			// The adaptive buffer: how long frames should wait in this client's buffer (ms)
			public int TargetSlack = BaseSlack;
			public BufferMode BufferMode = BufferMode.Auto;
			public int BufferPeak = BaseSlack;
			public bool BufferNoticeSent;

			// Hole detection: packet arrival times, the client's usual gap between packets, and the hole being classified
			public long LastArrivalTime;
			public readonly Queue<long> RecentGaps = new();
			public long LastFreezeTime = -1;
			public long OpenHoleStart = -1;
			public long OpenHoleEnd;
			public int PacketsSinceHole;
			public readonly Queue<(long End, long Length, HoleKind Kind)> Holes = new();
			public long LastHoleReport = -1;

			// Summary of the connection: dropouts stopped the client's game, delays only held up its packets
			public int Dropouts;
			public long DropoutMs;
			public long LongestDropout;
			public int Delays;
			public long DelayMs;
			public int Freezes;
			public long FreezeMs;

			// The game gave up waiting for this client (it stopped responding for longer than the host allows);
			// it is not waited for again until it has caught up
			public bool GaveUpWaiting;
			public bool WasWithinBudget = true;
		}

		public enum BufferMode
		{
			/// <summary>Grows with the connection's dropouts, shrinks while it is quiet (the default).</summary>
			Auto,

			/// <summary>The base 150 ms only.</summary>
			Off,

			/// <summary>Set by the player with !buffer.</summary>
			Fixed
		}

		public enum HoleKind
		{
			/// <summary>The download path, or the whole connection, stalled: the client had nothing to simulate.</summary>
			Connection,

			/// <summary>The upload path stalled: the client kept simulating and its queued packets arrived in a burst.</summary>
			Upload,

			/// <summary>The client's game stopped (a hitch, a dragged window): a hole its buffer would otherwise have covered.</summary>
			Freeze
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
		readonly int maxPlayerBuffer;
		readonly float maxCatchUpSpeed;
		readonly int maxWaitForStalledPlayer;
		readonly int startDelay;

		// The frame-backlog thresholds (see CreepRate), shrunk for a lag budget below the usual three seconds
		readonly int creepBacklog;
		readonly int creepPullBacklog;
		readonly int probeFailureBacklog;
		readonly Func<int, string> describeClient;
		readonly Action<string> log;
		readonly List<(int Client, string Message)> notices = new();
		readonly List<int> continuedWithout = new();

		bool started;
		long startedAt;
		long loadedAt = -1;
		int nextFrame;
		double nextCloseTime;
		long nextControlUpdate;
		float pace = 1f;
		bool blocked;
		bool gaveUp;
		long blockedSince;
		readonly HashSet<int> waitedFor = new();
		long lastResumeTime = -1;
		bool waitLogged;
		string blockedBy;
		int shortWaits;
		long shortWaitMs;
		long nextBehindReport;
		int slowestClient = -1;
		double slowestClientSpeed;
		float probeRate;
		long probeSince;
		float probeStartPace = 1f;

		// The creeping hold: the player the game is held for, the speed their computer was measured managing
		// (fraction of normal), and since when they have shown no resistance at that speed (-1: they have)
		int holdFor = -1;
		float ceilingSpeed = 1f;
		int freeIntervals;

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

		/// <summary>Creates a scheduler for one game.</summary>
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
		/// computer; a player who would need less is left to fall behind on their own. 0 means no floor: like the
		/// original scheme, the game follows the slowest player's computer however slow it is, and it is up to the
		/// players to vote-kick if they do not want to wait.</param>
		/// <param name="maxPlayerBuffer">The largest buffer (ms) built for a player whose connection drops out, at the
		/// cost of that player's own input delay. 0 disables the adaptive buffer (every client keeps the base 150 ms).</param>
		/// <param name="maxCatchUpSpeed">The fastest speed (percent of normal) a client far behind is asked to run at.</param>
		/// <param name="maxWaitForStalledPlayer">The longest the game pauses (ms) for a player who was keeping up and has
		/// stopped responding, before continuing without them. 0 never pauses.</param>
		/// <param name="startDelay">How long (ms) the game waits after the last client has finished loading before the
		/// first frame is closed, so that client can warm up its rendering like the others did while they waited.</param>
		public FrameScheduler(int timestep, int netFrameInterval, int firstFrame, IEnumerable<int> clientIndices,
			int maxPlayerLag = 0, Func<int, string> describeClient = null, Action<string> log = null,
			IEnumerable<int> spectatorIndices = null, int minGameSpeed = 0, int maxPlayerBuffer = 1500,
			int maxCatchUpSpeed = 400, int maxWaitForStalledPlayer = 3000, int startDelay = 0)
		{
			maxPace = minGameSpeed <= 0 ? PaceSanityLimit : Math.Min(PaceSanityLimit, 100f / minGameSpeed.Clamp(10, 100));
			this.timestep = timestep;
			this.netFrameInterval = netFrameInterval;
			this.firstFrame = firstFrame;
			this.maxPlayerBuffer = Math.Max(0, maxPlayerBuffer);
			this.maxCatchUpSpeed = Math.Max(1 / MinTickScale, maxCatchUpSpeed.Clamp(100, 2000) / 100f);
			this.maxWaitForStalledPlayer = Math.Max(0, maxWaitForStalledPlayer);
			this.startDelay = Math.Max(0, startDelay);
			this.describeClient = describeClient ?? (c => $"client {c}");
			this.log = log ?? (_ => { });

			// A player may always fall two frames behind before the game is slowed down for them
			lagBudget = Math.Max(2 * netFrameInterval * timestep, maxPlayerLag);

			var budgetFrames = lagBudget / (netFrameInterval * timestep);
			creepBacklog = Math.Min(CreepBacklog, budgetFrames / 10);
			creepPullBacklog = Math.Max(1, Math.Min(CreepPullBacklog, budgetFrames / 5));
			probeFailureBacklog = Math.Max(1, Math.Min(ProbeFailureBacklog, budgetFrames * 3 / 20));

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

			NoteArrival(client, state, now);

			// The client sends the packet for frame N just before it simulates frame N, so the time frame N
			// spent in its buffer is the time since we closed it, minus the round trip
			if (state.HasRtt && clientFrame >= firstFrame && closeTimes.TryGetValue(clientFrame, out var closedAt))
				state.SlackSamples.Add(now - closedAt - state.Rtt);
		}

		// Hole detection. A packet that arrives long after the previous one ends a hole; the packets right after it
		// tell whether the client had kept simulating meanwhile (a burst of queued packets: its upload path stalled)
		// or not (its download path stalled, or its game froze; see FreezeMemory for how those two are told apart).
		// The hole is classified once the burst window has passed, so that the buffer and the pace logic act on what
		// actually happened.
		void NoteArrival(int index, ClientState state, long now)
		{
			var period = FramePeriod;
			if (state.OpenHoleStart >= 0)
			{
				if (now <= state.OpenHoleEnd + BurstWindowMs)
					state.PacketsSinceHole++;
				else
					CloseHole(index, state, now);
			}

			// (Not during the first second: the first packets after loading arrive whenever each client is ready)
			if (state.OpenHoleStart < 0 && started && state.LastArrivalTime > startedAt + Interval)
			{
				var gap = now - state.LastArrivalTime;
				var typical = state.RecentGaps.Count > 0 ? Median(state.RecentGaps) : (long)period;
				if (gap > HoleThresholdPeriods * period && gap > HoleThresholdCadence * typical)
				{
					state.OpenHoleStart = state.LastArrivalTime;
					state.OpenHoleEnd = now;
					state.PacketsSinceHole = 1;
				}
				else
				{
					state.RecentGaps.Enqueue(gap);
					while (state.RecentGaps.Count > CadenceSamples)
						state.RecentGaps.Dequeue();
				}
			}

			state.LastArrivalTime = now;
		}

		void CloseHole(int index, ClientState state, long now)
		{
			var length = state.OpenHoleEnd - state.OpenHoleStart;

			// The packets that arrived together at the end of the hole were produced while the client was still
			// playing (from its buffer, or because only its upload path was stalled); the rest of the hole it was
			// stopped. That stopped part is what a bigger buffer would have covered.
			var typical = state.RecentGaps.Count > 0 ? Median(state.RecentGaps) : (long)FramePeriod;
			var stopped = Math.Max(0, length - (state.PacketsSinceHole - 1) * typical);
			var kind = HoleKind.Connection;
			if (state.PacketsSinceHole >= 2 && stopped < HoleThresholdPeriods * FramePeriod)
				kind = HoleKind.Upload;
			else if (state.PacketsSinceHole < 2 && state.TargetSlack > BaseSlack && stopped + FramePeriod < state.TargetSlack)
			{
				// The buffer would have covered a dropout this short without a hole: the client's game itself stopped
				kind = HoleKind.Freeze;
				state.LastFreezeTime = now;
				if (state.BufferMode == BufferMode.Auto)
				{
					// The recent dropouts of about that buffer's length were most likely freezes too (the buffer was built
					// from them, and it did not help); a much longer one, such as a dead connection, stays what it was
					var reclassified = 0;
					var remaining = new Queue<(long End, long Length, HoleKind Kind)>();
					foreach (var h in state.Holes)
					{
						if (h.Kind == HoleKind.Connection && h.Length <= 2 * state.TargetSlack)
						{
							reclassified++;
							state.Dropouts--;
							state.DropoutMs -= h.Length;
							state.Freezes++;
							state.FreezeMs += h.Length;
							remaining.Enqueue((h.End, h.Length, HoleKind.Freeze));
						}
						else
							remaining.Enqueue(h);
					}

					state.Holes.Clear();
					foreach (var h in remaining)
						state.Holes.Enqueue(h);
					state.LongestDropout = state.Holes.Where(h => h.Kind == HoleKind.Connection).Select(h => h.Length).DefaultIfEmpty(0).Max();

					log($"{describeClient(index)}'s game froze for {stopped / 1000f:F1}s with {state.TargetSlack / 1000f:F1}s buffered: " +
						$"the buffer is not helping them ({reclassified} earlier dropout(s) were freezes too); back to the normal buffer.");
					state.TargetSlack = BaseSlack;
				}
			}

			state.Holes.Enqueue((state.OpenHoleEnd, length, kind));
			while (state.Holes.Count > 0 && state.Holes.Peek().End < now - HoleMemory)
				state.Holes.Dequeue();

			switch (kind)
			{
				case HoleKind.Freeze:
					state.Freezes++;
					state.FreezeMs += length;
					break;
				case HoleKind.Upload:
					state.Delays++;
					state.DelayMs += length;
					break;
				default:
					state.Dropouts++;
					state.DropoutMs += length;
					state.LongestDropout = Math.Max(state.LongestDropout, length);
					break;
			}

			// The lateness samples taken across the hole do not describe a steady state: a burst makes the client look
			// late when its game never stopped, and a stopped client's samples straddle the change of buffer below
			state.SlackSamples.Clear();

			// A download-side hole stopped the client's game for its whole length (the buffer it had was used up first,
			// so the gap we saw is exactly what was missing). Turn the time it lost into buffer instead of catching it
			// up: its delay grows by that much, and the next hole of that length does not stop it.
			var recentlyFroze = state.LastFreezeTime >= 0 && now - state.LastFreezeTime < FreezeMemory;
			var recurring = state.Holes.Count(h => h.Kind == HoleKind.Connection && h.End > now - RecurrenceWindow) >= 2;
			if (kind == HoleKind.Connection && recurring && state.BufferMode == BufferMode.Auto && maxPlayerBuffer > 0 && !state.ExemptFromPacing && !recentlyFroze)
			{
				var target = (int)Math.Min(maxPlayerBuffer, state.TargetSlack + stopped);
				if (target > state.TargetSlack)
				{
					state.TargetSlack = target;
					if (!state.BufferNoticeSent && target >= BufferNoticeThreshold)
					{
						state.BufferNoticeSent = true;
						notices.Add((index, $"Your connection dropped out for {length / 1000f:F1}s. The server now buffers {target / 1000f:F1}s of the game " +
							"for you so that it keeps running through dropouts; your own commands take that much longer to happen. Type !buffer off to turn this off."));
					}

					// Log the growth in steps, not every hole
					if (target >= state.BufferPeak + BufferLogStep || (target >= BufferNoticeThreshold && state.BufferPeak < BufferNoticeThreshold))
						log($"{describeClient(index)}'s connection dropped out for {length / 1000f:F1}s; buffering {target / 1000f:F1}s for them from now on.");

					state.BufferPeak = Math.Max(state.BufferPeak, target);
				}
			}

			state.OpenHoleStart = -1;
		}

		/// <summary>Round-trip times from the connection's ping history (ms), on receiving a ping reply.</summary>
		public void ReceivePing(int client, int[] pingHistory, long now = 0)
		{
			if (!clients.TryGetValue(client, out var state) || pingHistory.Length == 0)
				return;

			var sorted = pingHistory.OrderBy(p => p).ToArray();
			state.Rtt = sorted[sorted.Length / 2];
			state.HasRtt = true;
		}

		/// <summary>Private messages for players (about their buffer), to be sent by the server.</summary>
		public List<(int Client, string Message)> TakeNotices()
		{
			if (notices.Count == 0)
				return null;

			var result = new List<(int, string)>(notices);
			notices.Clear();
			return result;
		}

		/// <summary>Players the game has stopped waiting for since the last call (for the announcer).</summary>
		public List<int> TakeContinuedWithout()
		{
			if (continuedWithout.Count == 0)
				return null;

			var result = new List<int>(continuedWithout);
			continuedWithout.Clear();
			return result;
		}

		/// <summary>The player's buffer setting, for the !buffer command.</summary>
		public (BufferMode Mode, int Ms)? GetBuffer(int client) =>
			clients.TryGetValue(client, out var state) ? (state.BufferMode, state.TargetSlack) : null;

		/// <summary>Sets a player's buffer by hand (<paramref name="ms"/> is used for <see cref="BufferMode.Fixed"/>).</summary>
		public void SetBuffer(int client, BufferMode mode, int ms = 0)
		{
			if (!clients.TryGetValue(client, out var state))
				return;

			state.BufferMode = mode;
			state.TargetSlack = mode switch
			{
				BufferMode.Off => BaseSlack,
				BufferMode.Fixed => Math.Max(BaseSlack, ms),
				_ => state.TargetSlack,
			};
			state.BufferPeak = Math.Max(state.BufferPeak, state.TargetSlack);
			state.SlackSamples.Clear();
			log($"{describeClient(client)} set their buffer to {(mode == BufferMode.Auto ? "auto" : $"{state.TargetSlack / 1000f:F1}s")}.");
		}

		/// <summary>A player has been defeated: keep relaying their orders, but stop waiting for them.</summary>
		public void SetDefeated(int client)
		{
			if (!clients.TryGetValue(client, out var state) || state.IsDefeated || state.IsSpectator)
				return;

			state.IsDefeated = true;
			log($"{describeClient(client)} has been defeated; the game will no longer wait for them.");
			if (client == slowestClient || client == holdFor)
				SpeedBackUp($"{describeClient(client)} has been defeated");
		}

		// The player the game was slowed down for is no longer playing. Nobody else has been tested above the
		// current speed since the slowdown began (when one computer struggles with a big battle, others are usually
		// close behind), so rather than jumping to full speed and finding out the hard way, probe up from here,
		// briskly: players expect to see the effect of a kick. Whoever cannot keep up is found on the way.
		void SpeedBackUp(string reason)
		{
			slowestClient = -1;
			holdFor = -1;
			freeIntervals = 0;
			if (pace <= 1f)
				return;

			StartProbe(LeaveProbeRate);
			foreach (var state in clients.Values)
			{
				state.FallingBehindIntervals = 0;
				state.BehindIntervals = 0;
			}

			// Take the first step straight away rather than at the next interval
			nextControlUpdate = 0;
			log($"{reason}; speeding the game back up.");
		}

		// Begin raising the speed from where it is, at the given rate (points per second), doubling from there.
		// The doubling clock is anchored at the next control update (see probeSince).
		void StartProbe(float initialRate)
		{
			probeRate = initialRate;
			probeSince = -1;
			probeStartPace = pace;
			freeIntervals = 0;
		}

		// Points per second the running probe should add now
		float CurrentProbeRate(long now)
		{
			if (probeSince < 0)
				probeSince = now - (long)(ProbeDoublingTime * Math.Log2(Math.Max(1f, probeRate / ProbeRate)));

			return Math.Min(MaxProbeRate, ProbeRate * (float)Math.Pow(2, (now - probeSince) / (double)ProbeDoublingTime));
		}

		public void RemoveClient(int client)
		{
			if (clients.TryGetValue(client, out var state) && state.Seen && !state.IsSpectator)
			{
				var avg = state.BehindSamples > 0 ? state.SumBehind / state.BehindSamples : 0;
				var computer = state.SlowestSpeed > 0 ? $" Their computer managed {state.SlowestSpeed * 100:F0}% while it was slowing the game" : "";

				// The best rate seen is a lower bound that says nothing unless it is high, or unless this player slowed the game
				if (state.PeakSpeed > 0 && (state.PeakSpeed >= 1 || state.SlowestSpeed > 0))
					computer += (computer.Length > 0 ? ", and" : " Their computer managed") + $" at least {state.PeakSpeed * 100:F0}% at best";

				var connection = "";
				if (state.Dropouts > 0)
					connection += $" Their connection dropped out {state.Dropouts} time(s), {state.DropoutMs / 1000f:F1}s in total, " +
						$"longest {state.LongestDropout / 1000f:F1}s" + (state.BufferPeak > BaseSlack ? $"; buffered up to {state.BufferPeak / 1000f:F1}s for them" : "") + ".";
				else if (state.BufferPeak > BaseSlack)
					connection += $" Buffered up to {state.BufferPeak / 1000f:F1}s for them.";
				if (state.Delays > 0)
					connection += $" Their packets were delayed {state.Delays} time(s), {state.DelayMs / 1000f:F1}s in total (their game kept running).";
				if (state.Freezes > 0)
					connection += $" Their game froze {state.Freezes} time(s), {state.FreezeMs / 1000f:F1}s in total.";

				log($"Summary for {describeClient(client)}: worst {state.MaxBehind / 1000f:F1}s behind, " +
					$"average {avg / 1000f:F2}s; caused {state.SlowdownsCaused} slowdown(s)." + (computer.Length > 0 ? computer + "." : "") + connection);
			}

			if (client == slowestClient || client == holdFor)
				SpeedBackUp($"{describeClient(client)} has left");

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
			return (int)Math.Ceiling((double)(state.Rtt + state.TargetSlack + windowSlack) / nominalPeriod) + 1;
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
				// Wait until every client has finished loading and sent its first orders, and then a little longer
				// (see startDelay) so that the last of them is as ready as the ones that waited for it
				if (clients.Values.Any(c => c.LastReportedFrame < 1))
					return false;

				if (loadedAt < 0)
					loadedAt = now;

				if (now < loadedAt + startDelay)
					return false;

				started = true;
				startedAt = now;
				nextCloseTime = now;
				nextControlUpdate = now + Interval;
			}

			if (now < nextCloseTime)
				return false;

			// Last resort: don't run further ahead of a player who was keeping up and has stopped responding than
			// they could possibly be buffering, for up to the host's limit; then continue without them (they catch
			// up at turbo speed if they come back). A player who is still sending frames, however slowly, or who was
			// already behind when they stopped, never holds the game.
			var waitingFor = maxWaitForStalledPlayer <= 0 ? new List<int>() : clients.Where(c => !c.Value.ExemptFromPacing
				&& !c.Value.GaveUpWaiting && c.Value.WasWithinBudget
				&& nextFrame - c.Value.LastReportedFrame > WindowFrames(c.Value)
				&& now - c.Value.LastProgressTime > StalledThreshold).Select(c => c.Key).ToList();
			var wasBlocked = blocked;
			blocked = waitingFor.Count > 0;
			if (blocked && wasBlocked && now - blockedSince >= maxWaitForStalledPlayer)
			{
				foreach (var index in waitingFor)
				{
					clients[index].GaveUpWaiting = true;
					continuedWithout.Add(index);
				}

				log($"Everyone waited {(now - blockedSince) / 1000f:F1}s for {blockedBy}, who has stopped responding; the game continues without them.");
				blocked = false;
				gaveUp = true;
			}

			if (blocked)
			{
				if (!wasBlocked)
				{
					blockedSince = now;
					blockedBy = string.Join(", ", waitingFor.Select(describeClient));
					waitedFor.UnionWith(waitingFor);
					waitLogged = false;
				}

				// A wait is logged once it has lasted long enough to be worth a line (shorter ones are counted and
				// reported in aggregate), and not for every wait in a rapid sequence
				if (!waitLogged && now - blockedSince >= LoggedWaitThreshold && (lastResumeTime < 0 || blockedSince - lastResumeTime > 1000))
				{
					waitLogged = true;
					log($"Everyone is waiting for {blockedBy}, who has stopped responding.");
				}

				return false;
			}

			if (wasBlocked)
			{
				lastResumeTime = now;
				var waited = now - blockedSince;
				if (gaveUp)
					gaveUp = false;
				else if (waited >= LoggedWaitThreshold)
					log($"Game resumed after waiting {waited / 1000f:F1}s for {blockedBy}.");
				else
				{
					shortWaits++;
					shortWaitMs += waited;
				}

				// The other clients had nothing to send while the game was paused; that gap is not a hole in their connections
				foreach (var (index, state) in clients)
					if (state.OpenHoleStart < 0 && !waitedFor.Contains(index))
						state.LastArrivalTime = now;

				waitedFor.Clear();
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

			// Classify holes whose burst window has passed without another packet, and let quiet buffers shrink
			foreach (var (index, state) in clients)
			{
				if (state.OpenHoleStart >= 0 && now > state.OpenHoleEnd + BurstWindowMs)
					CloseHole(index, state, now);

				// Shrink towards the base by the half-life: the controller then runs the client a little fast
				if (state.BufferMode == BufferMode.Auto && state.TargetSlack > BaseSlack)
					state.TargetSlack = BaseSlack + (int)((state.TargetSlack - BaseSlack) * Math.Pow(0.5, (double)Interval / BufferHalfLife));
			}

			// Each client's lag in net frames, and the fewest frames it has ever been behind by (its in-flight frames)
			foreach (var state in clients.Values)
			{
				state.LagFrames = nextFrame - 1 - state.LastReportedFrame;
				state.MinLagFrames = Math.Min(state.MinLagFrames, state.LagFrames);
				state.RecentLagFrames.Enqueue(state.LagFrames);
				while (state.RecentLagFrames.Count > FallingBehindIntervalsBeforePaceChange + 1)
					state.RecentLagFrames.Dequeue();
			}

			// How far behind its target position each client is (ms). Positive: frames are waiting in its
			// buffer for longer than needed, so it should run faster. Negative: it is close to running dry.
			var behind = new Dictionary<int, long>();
			foreach (var (index, state) in clients)
			{
				if (state.SlackSamples.Count == 0)
					continue;

				var b = Median(state.SlackSamples) - state.TargetSlack;
				behind[index] = b;
				state.SlackSamples.Clear();

				// Summary stats (only count real lateness, not being ahead of schedule, and only while the client is
				// still playing: a defeated player or spectator who falls behind is not held against them)
				state.Seen = true;
				if (!state.ExemptFromPacing)
				{
					if (b > 0)
					{
						state.MaxBehind = Math.Max(state.MaxBehind, b);
						state.SumBehind += b;
					}

					state.BehindSamples++;
				}

				state.RecentBehind.Enqueue(b);
				while (state.RecentBehind.Count > FallingBehindIntervalsBeforePaceChange + 1)
					state.RecentBehind.Dequeue();

				// A client the game stopped waiting for is waited for again once it has caught up
				state.WasWithinBudget = b <= lagBudget;
				if (state.GaveUpWaiting && state.WasWithinBudget)
				{
					state.GaveUpWaiting = false;
					log($"{describeClient(index)} has caught up again.");
				}
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

			// A probe has failed as soon as a player starts falling behind while it runs, without waiting for them to
			// use up their whole lag budget (which would take the game ten points past their ceiling and them back to
			// a three-second backlog). The game goes back to the speed that player was measured managing while they
			// fell behind, which is their ceiling as of now (never below where the probe started, where everyone was
			// keeping up), and is held there.
			if (probeRate > 0)
			{
				var failing = behind.Where(b => !clients[b.Key].ExemptFromPacing && clients[b.Key].WasToldToSpeedUp
					&& clients[b.Key].Backlog >= probeFailureBacklog && clients[b.Key].LagGrowthOverWindow >= ProbeFailureGrowth).Select(b => b.Key).ToList();
				if (failing.Count > 0)
				{
					var index = failing[0];
					var state = clients[index];
					var from = 100 / pace;
					var revert = probeStartPace;
					var rate = MeasuredRate(state, now, out _);
					if (rate > 0)
					{
						var needed = (float)(1 / (nominalPeriod * PaceHeadroom * rate));
						revert = Math.Min(probeStartPace, Math.Max(pace, needed));
					}

					pace = revert;
					HoldFor(index, rate > 0 ? (float)(rate * nominalPeriod) : 1 / (pace * PaceHeadroom));
					state.SlowdownsCaused++;
					foreach (var f in failing)
						clients[f].FallingBehindIntervals = clients[f].BehindIntervals = 0;

					log($"Speeding the game up to {from:F0}% was too much for {describeClient(index)} ({behind[index] / 1000f:F1}s behind and falling further); " +
						$"back to {100 / pace:F0}%.");
					cannotKeepUp = true;
				}
			}

			foreach (var (index, b) in behind)
			{
				var state = clients[index];
				if (state.ExemptFromPacing || !OverBudget(state, b))
					continue;

				if (state.FallingBehindIntervals < FallingBehindIntervalsBeforePaceChange && state.BehindIntervals < BehindIntervalsBeforePaceChange)
					continue;

				// Match the game's pace to what this client has managed while running as fast as it can,
				// with a little headroom so it can work off its backlog. Time the client spent in holes (its
				// connection dropped out, or its game froze) is taken out first: a stall is not a rate, and
				// slowing everyone to the average of "nothing" and "flat out" helps nobody. Only the rate the
				// client sustains between holes can slow the game.
				var framesPerMs = MeasuredRate(state, now, out var holeMs);
				if (holeMs > 0 && framesPerMs * nominalPeriod * PaceHeadroom >= 1 / pace)
				{
					// Between the holes this client keeps up with the game as it is: the deficit is theirs to catch up
					state.FallingBehindIntervals = 0;
					state.BehindIntervals = 0;
					if (state.LastHoleReport < 0 || now - state.LastHoleReport > HoleReportInterval)
					{
						state.LastHoleReport = now;
						var startTime = state.Progress.Peek().Time;
						var recent = state.Holes.Where(h => h.End > startTime).ToList();
						var cause = recent.Where(h => h.Kind == HoleKind.Freeze).Sum(h => h.Length) > recent.Where(h => h.Kind != HoleKind.Freeze).Sum(h => h.Length)
							? "freezes" : "connection dropouts";
						log($"{describeClient(index)} is {b / 1000f:F1}s behind because of {cause} ({holeMs / 1000f:F1}s in the last {(now - startTime) / 1000f:F0}s), " +
							"not their computer; the game is not slowed down for them.");
					}

					continue;
				}

				if (framesPerMs > 0)
				{
					var needed = (float)(1 / (nominalPeriod * PaceHeadroom * framesPerMs));
					if (needed > maxPace && clients.Values.Count(c => !c.ExemptFromPacing) >= MinPlayersForFloor)
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

					// This player needs the game at least as slow as it is, so they are the one it is slowed down for,
					// and held for from now on
					if (needed > 1.005f && needed >= pace - 0.005f)
					{
						state.SlowestSpeed = state.SlowestSpeed > 0 ? Math.Min(state.SlowestSpeed, managed) : managed;
						HoldFor(index, (float)managed);
					}

					pace = Math.Max(pace, needed);
				}

				state.FallingBehindIntervals = 0;
				state.BehindIntervals = 0;
				cannotKeepUp = true;
			}

			// Below full speed and nobody newly behind: either probing back up, or holding near the measured ceiling
			var playerBehind = behind.Where(b => !clients[b.Key].ExemptFromPacing).Select(b => b.Value).ToList();
			var recoveryThreshold = Math.Max(nominalPeriod, lagBudget / 2);
			if (!cannotKeepUp && pace > 1f)
			{
				if (probeRate > 0)
				{
					// Raise the speed while everyone is comfortably inside the lag budget, not right at its edge (a client
					// that sent nothing this interval may be stuck, so wait until it reports again). The probe starts
					// gently and accelerates while it succeeds: a computer at a hard ceiling is found out within a few
					// points of it, while a computer whose load has passed gets the game back to full speed in well
					// under a minute.
					if (playerBehind.Count == clients.Values.Count(c => !c.ExemptFromPacing) && playerBehind.All(b => b <= recoveryThreshold))
					{
						probeRate = CurrentProbeRate(now);
						pace = Math.Max(1f, 1 / (1 / pace + probeRate / 100f));
					}
				}
				else if (holdFor >= 0 && clients.TryGetValue(holdFor, out var held) && !held.ExemptFromPacing)
				{
					// A held player that sent nothing this interval may be stuck; their backlog says nothing until they report
					if (behind.ContainsKey(holdFor))
					{
						// The creeping hold (see CreepRate): up a little while the held player's backlog is small and steady,
						// back in proportion to how fast it grows and how large it is, never far below the measured ceiling
						var speed = 100 / pace;
						var backlog = held.Backlog;

						// The deficit: how fast the backlog grows (frames per second, LagGrowth being per interval) beyond a
						// deadband of CreepDeficitDeadband of the frames closed per second at this pace, or CreepGrowthFloor.
						// A frame per second of growth is nominalPeriod/10 speed points short of the pace.
						var growth = held.LagGrowth * 1000f / Interval;
						var framesPerSecond = 1000f / (nominalPeriod * pace);
						var deadband = Math.Max(CreepDeficitDeadband * framesPerSecond, CreepGrowthFloor);
						var excess = Math.Max(0, growth - deadband);
						var delta = (growth <= deadband && backlog <= creepBacklog ? CreepRate : 0)
							- CreepDeficitGain * excess * nominalPeriod / 10f
							- CreepLevelGain * Math.Max(0, backlog - creepPullBacklog);
						delta = delta.Clamp(-CreepRampLimit, CreepRampLimit);
						var floor = Math.Min(speed, 100 * ceilingSpeed * (1 - CreepFloor));
						speed = Math.Min(100f, Math.Max(speed + delta, floor));
						pace = 100 / speed;

						// Keeping up at the measured ceiling for long enough means the ceiling has moved up: probe
						var atCeiling = speed >= (100 + CreepAboveCeiling) * ceilingSpeed;
						var recentHole = held.OpenHoleStart >= 0 || held.Holes.Any(h => h.End > now - FallingBehindIntervalsBeforePaceChange * Interval);
						if (recentHole)
						{
							// Says nothing about the computer
						}
						else if (growth > 2 * deadband || backlog > creepPullBacklog)
							freeIntervals = 0;
						else if (growth <= deadband && backlog <= creepBacklog && atCeiling)
							freeIntervals++;

						if (pace > 1f && freeIntervals >= CreepFreeIntervals)
						{
							log($"{describeClient(holdFor)} has kept up at {speed:F0}% for {freeIntervals}s; probing for more speed.");
							StartProbe(ProbeRate);
						}
					}
				}
				else
				{
					// Slowed down with nobody left to hold for: find out what the others can do
					holdFor = -1;
					StartProbe(ProbeRate);
				}
			}

			if (pace == 1f && oldPace > 1f)
			{
				slowestClient = -1;
				holdFor = -1;
				probeRate = 0;
				freeIntervals = 0;
				log("The game is back to full speed.");
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
					log("Players behind: " + string.Join(", ", lagging.Select(b => $"{describeClient(b.Key)}{Role(clients[b.Key])} {b.Value / 1000f:F1}s")) + "." + speed);
				}
			}

			foreach (var (index, state) in clients)
			{
				var baseTickMs = timestep * pace;
				var tickMs = (int)Math.Round(baseTickMs);
				var fastestTickMs = minTickMs;
				if (behind.TryGetValue(index, out var b))
				{
					if (Math.Abs(b) >= SlackDeadband)
						tickMs = (int)Math.Round(baseTickMs - Gain * b / ticksPerInterval);

					// The further behind, the faster it may be asked to run (see CatchUpPerSecondBehind)
					if (b > 0)
						fastestTickMs = CatchUpTickMs(1 + CatchUpPerSecondBehind * b / 1000f);

					state.LastBehind = b;
				}
				else if (nextFrame - state.LastReportedFrame > WindowFrames(state))
				{
					// A client so far behind that its frames are no longer tracked (or that sent nothing this
					// interval) gets no lateness samples: a spectator or defeated player who fell behind, a player
					// left behind by the floor, or one the game stopped waiting for. Tell it to run flat out so
					// that it catches up if its computer or connection recovers, instead of staying behind for
					// the rest of the game.
					tickMs = fastestTickMs = CatchUpTickMs(maxCatchUpSpeed);
				}

				var roundedBaseTickMs = (int)Math.Round(baseTickMs);
				tickMs = tickMs.Clamp(fastestTickMs, Math.Max(maxTickMs, (int)Math.Round(baseTickMs * 1.25f)));
				state.WasToldToSpeedUp = tickMs < roundedBaseTickMs;
				state.RequestedSpeed = (float)timestep / tickMs;

				// Clients compute (int)(scale * timestep); the half millisecond keeps float rounding
				// from landing just below the intended value
				result.Add((index, tickMs == timestep ? 1f : (tickMs + 0.5f) / timestep));
			}

			return result;
		}

		// The rate (frames per ms) a client has managed over its progress window, with the time it spent in holes
		// taken out (returned in holeMs). 0 if nothing is known yet.
		static double MeasuredRate(ClientState state, long now, out long holeMs)
		{
			var (startTime, startFrame) = state.Progress.Peek();
			var elapsed = Math.Max(1, now - startTime);
			holeMs = state.Holes.Where(h => h.End > startTime).Sum(h => Math.Min(h.Length, h.End - startTime));
			if (state.OpenHoleStart >= 0)
				holeMs += Math.Min(state.OpenHoleEnd - state.OpenHoleStart, state.OpenHoleEnd - startTime);

			return (double)(state.LastReportedFrame - startFrame) / Math.Max(1, elapsed - holeMs);
		}

		// Whether a client's lateness (ms) calls for slowing the game: past the budget, or past ReportBehindThreshold
		// and on course to pass the budget within TriggerHorizon at the rate it has been growing
		bool OverBudget(ClientState state, long b)
		{
			if (b > lagBudget)
				return true;

			if (b <= ReportBehindThreshold || state.FallingBehindIntervals < FallingBehindIntervalsBeforePaceChange || state.RecentBehind.Count < 2)
				return false;

			var slopePerInterval = (double)(b - state.RecentBehind.Peek()) / (state.RecentBehind.Count - 1);
			return b + slopePerInterval * TriggerHorizon / Interval > lagBudget;
		}

		// Hold the game for this client, whose computer was just measured managing the given fraction of normal speed
		void HoldFor(int client, float measuredSpeed)
		{
			holdFor = client;
			slowestClient = client;
			slowestClientSpeed = measuredSpeed;
			ceilingSpeed = measuredSpeed;
			probeRate = 0;
			freeIntervals = 0;
		}

		// The shortest tick (ms) for a requested speed-up, never faster than the host's cap nor slower than the
		// ordinary correction limit
		int CatchUpTickMs(float speed) =>
			Math.Max(1, (int)Math.Round(timestep / Math.Min(maxCatchUpSpeed, Math.Max(1 / MinTickScale, speed))));

		static string Role(ClientState state) =>
			state.IsSpectator ? " (spectator)" : state.IsDefeated ? " (defeated)" : state.IsTooSlow ? " (too slow)" : "";

		static long Median(IEnumerable<long> values)
		{
			var a = values.ToArray();
			Array.Sort(a);
			return a[a.Length / 2];
		}
	}
}
