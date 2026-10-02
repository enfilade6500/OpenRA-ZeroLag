// Standalone tests for OpenRA.Server.FrameScheduler.
// Runs here against the built OpenRA.Game.dll (NUnit isn't available offline).
// The same assertions are mirrored as NUnit tests in OpenRA.Test/FrameSchedulerTest.cs for CI.
using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Server;

static class Tests
{
	static int failures;
	static readonly byte[] Order = { 1, 2, 3, 4, 5 }; // a non-empty, non-sync, non-disconnect payload

	static void Check(bool cond, string name)
	{
		Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
		if (!cond)
			failures++;
	}

	// Advance the scheduler to time `now`, collecting every frame it closes.
	static List<(int Frame, List<(int Client, byte[] Data, int Count)> Contents)> Drain(FrameScheduler s, long now)
	{
		var closed = new List<(int, List<(int, byte[], int)>)>();
		while (s.TryCloseFrame(now, out var frame, out var contents))
			closed.Add((frame, contents));

		return closed;
	}

	static void FramesAreConsecutiveAndComplete()
	{
		const int First = 7;
		var s = new FrameScheduler(40, 3, First, new[] { 0, 1 });

		// Not started until both clients have sent their first packet
		s.ReceivePacket(0, 1, Order, 0);
		Check(Drain(s, 1000).Count == 0, "no frames close before every client has reported");
		s.ReceivePacket(1, 1, Order, 0);

		var all = new List<(int, List<(int, byte[], int)>)>();
		for (var t = 0; t <= 2000; t += 40)
			all.AddRange(Drain(s, t));

		Check(all.Count > 0, "frames close once started");
		Check(all[0].Item1 == First, "first closed frame == firstFrame");
		Check(all.Select(f => f.Item1).SequenceEqual(Enumerable.Range(First, all.Count)), "frame numbers are strictly consecutive, no gaps");
		Check(all.All(f => f.Item2.Select(c => c.Item1).OrderBy(x => x).SequenceEqual(new[] { 0, 1 })), "every client appears in every frame");
	}

	static void AckCountsMatchPacketsSent()
	{
		var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
		s.ReceivePacket(0, 1, Order, 0);
		s.ReceivePacket(1, 1, Order, 0);
		Drain(s, 0); // start + close frame 1 (consumes the initial packets)

		// Between closes: client 0 sends 3, client 1 sends none
		s.ReceivePacket(0, 2, Order, 130);
		s.ReceivePacket(0, 3, Order, 131);
		s.ReceivePacket(0, 4, Order, 132);
		var closed = Drain(s, 140);
		Check(closed.Count == 1, "one frame closes per period");
		var byClient = closed[0].Item2.ToDictionary(c => c.Item1, c => c.Item3);
		Check(byClient[0] == 3, "ack count == packets sent since last close (3)");
		Check(byClient[1] == 0, "empty frame (ack 0) for a silent client");

		// Merged data for client 0 is the concatenation (length 3x)
		var data0 = closed[0].Item2.First(c => c.Item1 == 0).Item2;
		Check(data0.Length == 3 * Order.Length, "merged packet data is concatenated");
	}

	static void SilentPlayerBlocksThenResumes()
	{
		var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
		s.ReceivePacket(0, 1, Order, 0);
		s.ReceivePacket(1, 1, Order, 0);

		// Client 1 goes silent; client 0 keeps up. Advance well past the window.
		var lastFrame = 0;
		for (long t = 0; t <= 10000; t += 40)
		{
			foreach (var (frame, _) in Drain(s, t))
				lastFrame = frame;

			s.ReceivePacket(0, lastFrame + 5, Order, t); // client 0 stays current
		}

		var stalledAt = lastFrame;
		Check(s.MillisecondsUntilNextAction(10000) <= 5, "blocked scheduler asks to be polled soon");

		// Client 1 catches up: closing resumes
		s.ReceivePacket(1, stalledAt, Order, 10000);
		var resumed = false;
		for (long t = 10000; t <= 20000; t += 40)
			if (Drain(s, t).Count > 0)
			{
				resumed = true;
				break;
			}

		Check(stalledAt > 0 && stalledAt < 80, "a silent player eventually blocks everyone (bounded ahead)");
		Check(resumed, "closing resumes after the silent player catches up");
	}

	static void DefeatedPlayerNoLongerBlocks()
	{
		var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
		s.ReceivePacket(0, 1, Order, 0);
		s.ReceivePacket(1, 1, Order, 0);
		s.SetDefeated(1);
		var lastFrame = 0;
		for (long t = 0; t <= 15000; t += 40)
		{
			foreach (var (frame, _) in Drain(s, t))
				lastFrame = frame;

			s.ReceivePacket(0, lastFrame + 5, Order, t);
		}

		Check(lastFrame > 100, $"a silent defeated player does not block the game ({lastFrame} frames)");
	}

	static void TricklingPlayerNeverBlocks()
	{
		var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
		s.ReceivePacket(0, 1, Order, 0);
		s.ReceivePacket(1, 1, Order, 0);
		var lastFrame = 0; var slowFrame = 1;
		for (long t = 0; t <= 20000; t += 40)
		{
			foreach (var (frame, _) in Drain(s, t))
				lastFrame = frame;

			s.ReceivePacket(0, lastFrame, Order, t);
			if (t % 1000 == 0 && slowFrame < lastFrame)
				s.ReceivePacket(1, ++slowFrame, Order, t);
		}

		Check(lastFrame > 150, $"a trickling (alive but very slow) player never blocks the game ({lastFrame} frames)");
	}

	static void TooSlowComputerDoesNotSlowTheGame()
	{
		const int Timestep = 40;
		var logs = new List<string>();
		var s = new FrameScheduler(Timestep, 3, 1, new[] { 0, 1, 2 }, 0, null, logs.Add, null, 75);
		s.ReceivePing(0, new[] { 20 }); s.ReceivePing(1, new[] { 20 }); s.ReceivePing(2, new[] { 20 });
		s.ReceivePacket(0, 1, Order, 0); s.ReceivePacket(1, 1, Order, 0); s.ReceivePacket(2, 1, Order, 0);
		var lastFrame = 0; var slowFrame = 1; var scale0 = 1f;
		var closed = new Queue<(int Frame, long At)>();
		for (long t = 0; t <= 60000; t += 40)
		{
			foreach (var (frame, _) in Drain(s, t))
			{
				lastFrame = frame;
				closed.Enqueue((frame, t));
			}

			while (closed.Count > 0 && t >= closed.Peek().At + 170)
			{
				var f = closed.Dequeue().Frame;
				s.ReceivePacket(0, f, Order, t);
				s.ReceivePacket(2, f, Order, t);
			}

			if (t % 240 == 0 && slowFrame < lastFrame)
				s.ReceivePacket(1, ++slowFrame, Order, t);

			foreach (var (client, scale) in s.GetTickScales(t))
				if (client == 0)
					scale0 = scale;
		}

		Check(logs.Any(l => l.Contains("too slow")), "half-speed player is marked too slow");
		Check((int)(scale0 * Timestep) == Timestep, $"game not left slowed for a too-slow player (client 0 scale {scale0})");
	}

	static void SilentSpectatorNeverBlocks()
	{
		var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 }, spectatorIndices: new[] { 1 });
		s.ReceivePacket(0, 1, Order, 0);
		s.ReceivePacket(1, 1, Order, 0);

		// Spectator (1) never sends again; player 0 keeps up
		var lastFrame = 0;
		for (long t = 0; t <= 15000; t += 40)
		{
			foreach (var (frame, _) in Drain(s, t))
				lastFrame = frame;

			s.ReceivePacket(0, lastFrame + 5, Order, t);
		}

		// ~15s at 120ms/frame ~= 125 frames; must be far past the ~19-frame window
		Check(lastFrame > 100, "game keeps closing frames despite a hopelessly-behind spectator");
	}

	static void FarBehindSpectatorIsStillToldToCatchUp()
	{
		const int Timestep = 40;
		var s = new FrameScheduler(Timestep, 3, 1, new[] { 0, 1 }, spectatorIndices: new[] { 1 });
		s.ReceivePacket(0, 1, Order, 0);
		s.ReceivePacket(1, 1, Order, 0);
		var lastFrame = 0;
		float lastSpectatorScale = 1f;
		for (long t = 0; t <= 400000; t += 40)
		{
			foreach (var (frame, _) in Drain(s, t))
				lastFrame = frame;

			s.ReceivePacket(0, lastFrame, Order, t);
			foreach (var (client, scale) in s.GetTickScales(t))
				if (client == 1)
					lastSpectatorScale = scale;
		}

		Check(lastFrame > 2048, $"precondition: spectator behind by more than tracked history ({lastFrame} frames)");
		Check((int)(lastSpectatorScale * Timestep) < Timestep, $"far-behind spectator still told to speed up (scale {lastSpectatorScale})");
	}

	// A client whose computer manages `capacity` (fraction of normal speed): it uses one frame per net frame
	// interval, at the tick length it was told or as fast as its computer allows, whichever is slower, and
	// only once the frame has reached it. Reports each frame to the scheduler as a real client would.
	sealed class SimClient
	{
		public readonly int Index;
		public double Capacity;
		public int TickMs;
		public double NextFrameTime;
		public long FrozenUntil;
		public int NextFrame;
		public readonly Queue<(int Frame, long Available)> Inbox = new();

		// Connection trouble: nothing reaches the client before DownDeadUntil; nothing it sends reaches the server
		// before UpDeadUntil (and then everything queued arrives at once, like TCP after a retransmission)
		public long DownDeadUntil;
		public long UpDeadUntil;

		// Extra one-way delay on the download side that the client's ping does not show (ms): the server then reads
		// this client's lateness that much too high, like a client whose round trip is underestimated
		public long ExtraDelay;
		readonly Queue<int> outbox = new();
		public float MinScale = 1f;
		public int Stalls; // ticks the client wanted to take but had no frame for

		public SimClient(int index, double capacity, int timestep, int firstFrame)
		{
			Index = index; Capacity = capacity; TickMs = timestep; NextFrame = firstFrame;
		}

		public bool LinkDead(long now) => now < DownDeadUntil || now < UpDeadUntil;

		public void Run(FrameScheduler s, long now, int timestep, int netFrameInterval)
		{
			if (now >= UpDeadUntil)
				while (outbox.Count > 0)
					s.ReceivePacket(Index, outbox.Dequeue(), Order, now);

			// A frozen client does nothing, and starts again from where it was when it unfreezes
			if (now < FrozenUntil)
				return;

			if (FrozenUntil > 0)
			{
				FrozenUntil = 0;
				NextFrameTime = now;
			}

			if (now >= NextFrameTime && (Inbox.Count == 0 || Inbox.Peek().Frame != NextFrame || Inbox.Peek().Available > now))
				Stalls++;

			while (now >= NextFrameTime && Inbox.Count > 0 && Inbox.Peek().Frame == NextFrame && Inbox.Peek().Available <= now)
			{
				Inbox.Dequeue();
				if (now < UpDeadUntil)
					outbox.Enqueue(NextFrame++);
				else
					s.ReceivePacket(Index, NextFrame++, Order, now);

				var tick = Math.Max(TickMs, timestep / Capacity);
				NextFrameTime = Math.Max(NextFrameTime, now) + (tick * netFrameInterval);
			}
		}
	}

	static (List<string> Logs, FrameScheduler S, List<SimClient> Clients) RunWithCapacities(double[] capacities, long duration, int minGameSpeed,
		Action<long, List<SimClient>, FrameScheduler> onSecond = null, int maxPlayerBuffer = 1500, int maxWait = 3000,
		Action<FrameScheduler, long> onTick = null, int maxPlayerLag = 3000, long[] extraDelays = null)
	{
		const int Timestep = 40, Interval = 3, First = 4, Delay = 20;
		var logs = new List<string>();
		var indices = Enumerable.Range(0, capacities.Length).ToArray();
		var s = new FrameScheduler(Timestep, Interval, First, indices, maxPlayerLag, i => $"P{i}", logs.Add, null, minGameSpeed, maxPlayerBuffer, 400, maxWait);
		var clients = indices.Select(i => new SimClient(i, capacities[i], Timestep, First)).ToList();
		foreach (var c in clients)
		{
			c.ExtraDelay = extraDelays?[c.Index] ?? 0;
			s.ReceivePing(c.Index, new[] { 2 * Delay }, 0);
			for (var f = 1; f < First; f++)
				s.ReceivePacket(c.Index, f, Order, 0); // the frames a client sends while the game is starting
		}

		for (long t = 0; t <= duration; t += 10)
		{
			while (s.TryCloseFrame(t, out var frame, out _))
				foreach (var c in clients)
					c.Inbox.Enqueue((frame, Math.Max(t, c.DownDeadUntil) + Delay + c.ExtraDelay));

			foreach (var c in clients)
				c.Run(s, t, Timestep, Interval);

			// Ping replies every 250ms. The release client answers pings on its game thread, so they stop while its
			// game is frozen as well as while its connection is dead
			if (t % 250 == 0)
				foreach (var c in clients)
					if (!c.LinkDead(t) && t >= c.FrozenUntil)
						s.ReceivePing(c.Index, new[] { 2 * Delay }, t);

			foreach (var (client, scale) in s.GetTickScales(t))
			{
				clients[client].TickMs = Math.Max((int)(scale * Timestep), 1);
				clients[client].MinScale = Math.Min(clients[client].MinScale, scale);
			}

			onTick?.Invoke(s, t);
			if (t % 1000 == 0)
				onSecond?.Invoke(t, clients, s);
		}

		return (logs, s, clients);
	}

	static void SlowestPlayerIsIdentifiedAndCleared()
	{
		// P1's computer manages 80%; P0 and P2 are fast. The game should settle near 80% and know P1 is the reason.
		var seenSlowest = new HashSet<int>();
		var minSpeed = 100;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.8, 2.0 }, 120000, 0, (t, cs, sched) =>
		{
			if (t == 60000)
				cs[1].Capacity = 2.0; // P1 recovers (e.g. closed a background program)

			if (t < 60000)
			{
				if (sched.SlowestPlayer.HasValue)
					seenSlowest.Add(sched.SlowestPlayer.Value.Client);
				minSpeed = Math.Min(minSpeed, sched.SpeedPercent);
			}
		});

		Check(seenSlowest.SetEquals(new[] { 1 }), $"only the slow computer is reported as the slowest player ({string.Join(",", seenSlowest)})");
		Check(minSpeed >= 70 && minSpeed <= 85, $"game slowed to about the slow computer's speed ({minSpeed}%)");
		Check(logs.Any(l => l.StartsWith("Slowing the game") && l.Contains("P1")), "slowdown logged against P1");
		Check(s.SpeedPercent == 100 && !s.SlowestPlayer.HasValue, $"speed back to 100% and no slowest player once P1 recovered ({s.SpeedPercent}%)");
		Check(clients[1].NextFrame > 700 && clients[0].NextFrame > 700, $"game kept progressing ({clients[0].NextFrame}, {clients[1].NextFrame} frames)");

		s.RemoveClient(1);
		var summary = logs.Last(l => l.StartsWith("Summary for P1"));
		Check(summary.Contains("while it was slowing the game"), "summary reports what the slow computer managed: " + summary);
	}

	static void KickedSlowestPlayerSpeedsBackUp()
	{
		// P1 (80%) slows the game; at 30s P1 is kicked. Nobody else has been tested above the held speed (a few points
		// under 80%) since, so the game does not jump to 100%: it speeds back up briskly from where it is, and is at
		// full speed within 15s.
		var speedAfterKick = new List<(long T, int Speed)>();
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.8, 2.0 }, 50000, 0, (t, cs, sched) =>
		{
			if (t == 30000)
				sched.RemoveClient(1);
			if (t > 30000)
				speedAfterKick.Add((t, sched.SpeedPercent));
		});

		var speeds = speedAfterKick.Select(x => x.Speed).ToList();
		Check(speeds[0] < 100 && speeds[0] >= 74, $"the game does not jump to full speed the second after the kick ({speeds[0]}%)");
		Check(speeds.Zip(speeds.Skip(1), (a, b) => b >= a).All(x => x), $"the speed only rises after the kick ({string.Join(",", speeds.Take(12))})");
		Check(speedAfterKick.First(x => x.Speed == 100).T - 30000 <= 15000, $"full speed within 15s of the kick ({(speedAfterKick.First(x => x.Speed == 100).T - 30000) / 1000}s)");
		Check(logs.Any(l => l.Contains("P1 has left; speeding the game back up")), "the ramp is logged");
	}

	static void LeavingSlowestPlayerFindsTheNextCeiling()
	{
		// P1 (60%) slows the game; P2 (80%) is quietly fine at that speed. When P1 leaves, the game probes up and
		// P2's ceiling is found on the way: a brief overshoot, then a hold near 80%, never a jump to 100% that
		// leaves P2 three seconds behind.
		var maxP2Gap = 0;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.6, 0.8 }, 120000, 0, (t, cs, sched) =>
		{
			if (t == 40000)
				sched.RemoveClient(1);
			if (t > 40000)
				maxP2Gap = Math.Max(maxP2Gap, cs[0].NextFrame - cs[2].NextFrame);
		}, maxPlayerLag: 3000);

		Check(logs.Any(l => (l.StartsWith("Speeding the game up to") || l.StartsWith("Slowing the game")) && l.Contains("P2")), "P2's ceiling is found after P1 leaves");
		Check(maxP2Gap * 120 < 2500, $"P2 never falls far behind while the game speeds up ({maxP2Gap * 120}ms at worst)");
		Check(s.SpeedPercent >= 72 && s.SpeedPercent <= 92, $"the game settles near P2's ceiling ({s.SpeedPercent}%)");
	}

	static void FailedProbeRevertsToTheMeasuredSpeed()
	{
		// P1 manages 60% for 45s, then 85%. The creeping hold notices the ceiling has moved and probes; the probe
		// fails somewhere above 85%, and the game goes back to what P1 was just measured managing, not to 60%.
		var minSpeedAfterFailure = 100;
		var speedBefore = 100;
		var speedByTime = new List<(long T, int Speed)>();
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.6 }, 130000, 0, (t, cs, sched) =>
		{
			if (t == 45000)
				cs[1].Capacity = 0.85;
			speedByTime.Add((t, sched.SpeedPercent));
		}, maxPlayerLag: 3000);

		// The failure happens when the speed drops for the first time after 60s (the probe's overshoot is reverted)
		var failedAt = speedByTime.Where(x => x.T > 60000).Zip(speedByTime.Where(x => x.T > 60000).Skip(1), (a, b) => (b.T, Drop: b.Speed < a.Speed - 3)).FirstOrDefault(x => x.Drop).T;
		if (failedAt > 0)
			minSpeedAfterFailure = speedByTime.Where(x => x.T >= failedAt).Min(x => x.Speed);

		var failure = logs.FirstOrDefault(l => l.StartsWith("Speeding the game up to"));
		Check(failure != null && failedAt > 0, "precondition: a probe failed after the ceiling rose: " + string.Join(" | ", logs.Where(l => !l.StartsWith("Players behind")).TakeLast(4)));
		var backTo = failure == null ? 0 : int.Parse(failure.Split("back to ")[1].TrimEnd('.', '%'));
		Check(backTo >= 75, $"the revert is to the measured speed, not the probe's start ({failure})");
		Check(minSpeedAfterFailure >= 72, $"after the failure the game stays near P1's new ceiling ({minSpeedAfterFailure}% at lowest)");
	}

	static void CreepingHoldSettlesWithoutProbing()
	{
		// P1 manages a steady 75%. After the first slowdown the creeping hold should settle a point or two under
		// 75% and stay there: no timed probes, no failures, no further slowdowns.
		var speeds = new List<int>();
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.75, 2.0 }, 180000, 0, (t, cs, sched) =>
		{
			if (t >= 40000)
				speeds.Add(sched.SpeedPercent);
		}, maxPlayerLag: 3000);

		Check(logs.Count(l => l.StartsWith("Slowing the game")) <= 2, $"at most two slowdowns for a steady computer ({logs.Count(l => l.StartsWith("Slowing the game"))})");
		Check(logs.Count(l => l.StartsWith("Speeding the game up to")) <= 1, $"at most one failed probe in three minutes ({logs.Count(l => l.StartsWith("Speeding the game up to"))})");
		Check(speeds.Min() >= 66 && speeds.Max() <= 79, $"the speed stays within a few points of the ceiling ({speeds.Min()}-{speeds.Max()}%)");
		Check(clients[0].NextFrame - clients[1].NextFrame < 20, $"P1 stays close to the others ({(clients[0].NextFrame - clients[1].NextFrame) * 120}ms behind at the end)");
	}

	static void ProjectedLagTriggersBeforeTheBudget()
	{
		// P1 manages 50% at full speed: it falls behind at half a second per second. With a 3s budget the outcome
		// is clear long before 3s, so the slowdown comes early and P1 never carries the whole budget.
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.5 }, 30000, 0, null, maxPlayerLag: 3000);
		var first = logs.FirstOrDefault(l => l.StartsWith("Slowing the game"));
		Check(first != null, "precondition: the game was slowed");
		var behind = first == null ? 9f : float.Parse(first.Split(" and is ")[1].Split('s')[0], System.Globalization.CultureInfo.InvariantCulture);
		Check(behind < 2.8f, $"the first slowdown comes before the budget is used up ({behind}s behind)");
	}

	static void VoteCommandsAreRecognised()
	{
		Check(SlowestPlayerVote.IsCommand("!kickslow") && SlowestPlayerVote.IsCommand("!kicklag") && SlowestPlayerVote.IsCommand("!ks"), "aliases count as the vote");
		Check(SlowestPlayerVote.IsCommand("!kickslow lincox"), "a name after the command does not stop it counting");
		Check(!SlowestPlayerVote.IsCommand("!kick") && SlowestPlayerVote.LooksLikeKick("!kick kali") && SlowestPlayerVote.LooksLikeKick("!kicksllow"), "other kick attempts are recognised for a hint");
		Check(!SlowestPlayerVote.LooksLikeKick("!kickslow") && !SlowestPlayerVote.LooksLikeKick("!speed"), "the vote and other commands are not hints");
	}

	static void CapacityIsMeasuredWhileCatchingUp()
	{
		// P1 freezes for 3s (a hitch) and then catches up: while it works through the backlog the scheduler
		// learns a lower bound on what its computer can do, even though the game itself never slows down.
		var (logs, s, _) = RunWithCapacities(new[] { 2.0, 3.0 }, 40000, 0, (t, cs, _) =>
		{
			if (t == 10000)
				cs[1].FrozenUntil = 13000;
		});

		s.RemoveClient(1);
		var summary = logs.Last(l => l.StartsWith("Summary for P1"));
		Check(summary.Contains("at least") && !summary.Contains("while it was slowing the game"), "summary gives a lower bound for a fast computer after a hitch: " + summary);
		Check(!logs.Any(l => l.StartsWith("Slowing the game")), "a hitch does not slow the game");
	}

	static void DownloadHoleBecomesBuffer()
	{
		// P1's download path dies for 1s at 10s, 30s and 50s. One hiccup costs nothing; the second within two minutes
		// shows a pattern and is turned into buffer; the third hole of the same length is covered by that buffer, so
		// P1's game does not stop again.
		var bufferAfterFirst = 0; var bufferAfterSecond = 0; var stallsBefore = 0; var stallsAfter = 0;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 2.0 }, 65000, 0, (t, cs, sched) =>
		{
			if (t == 10000 || t == 30000 || t == 50000)
				cs[1].DownDeadUntil = t + 1000;
			if (t == 12000)
				bufferAfterFirst = sched.GetBuffer(1).Value.Ms;
			if (t == 32000)
			{
				bufferAfterSecond = sched.GetBuffer(1).Value.Ms;
				stallsBefore = cs[1].Stalls;
			}

			if (t == 48000)
				stallsAfter = cs[1].Stalls;
		}, maxPlayerLag: 3000);

		Check(bufferAfterFirst == 150, $"a single hiccup costs no delay ({bufferAfterFirst}ms)");
		Check(bufferAfterSecond >= 900 && bufferAfterSecond <= 1500, $"the second 1s download hole becomes about 1s of buffer ({bufferAfterSecond}ms)");
		Check(s.GetBuffer(1).Value.Ms < bufferAfterSecond && s.GetBuffer(1).Value.Ms > 150, $"the buffer shrinks slowly while the connection is quiet ({s.GetBuffer(1).Value.Ms}ms after 15s)");
		Check(stallsBefore > 100, $"the first two holes stopped P1's game ({stallsBefore} stalled ticks)");
		Check(clients[1].Stalls - stallsAfter < 10, $"the third hole did not ({clients[1].Stalls - stallsAfter} stalled ticks)");
		Check(logs.Any(l => l.Contains("P1's connection dropped out for") && System.Text.RegularExpressions.Regex.IsMatch(l, @"buffering (0\.[89]|1\.[0-2])s")), "the buffer growth is logged: " + (logs.FirstOrDefault(l => l.Contains("dropped out")) ?? "(nothing)"));
		Check(!logs.Any(l => l.StartsWith("Slowing the game")), "a dropout never slows the game");
		Check(clients[0].Stalls < 5, $"the other player never noticed ({clients[0].Stalls} stalled ticks)");
	}

	static void UploadStallAndFreezeDoNotKeepABuffer()
	{
		// An upload stall (the client kept playing; its packets arrived in a burst) is not what a buffer is for.
		// Game freezes cannot be told from dropouts at first, so two of them build a buffer; a third freeze, shorter
		// than that buffer, proves the buffer is not helping and takes it away again.
		var bufferAfterTwoFreezes = 0;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 2.0, 2.0 }, 50000, 0, (t, cs, sched) =>
		{
			if (t == 10000)
			{
				cs[1].UpDeadUntil = t + 1000;
				cs[2].FrozenUntil = t + 1000;
			}

			if (t == 20000)
				cs[2].FrozenUntil = t + 1000;
			if (t == 25000)
				bufferAfterTwoFreezes = sched.GetBuffer(2).Value.Ms;
			if (t == 35000)
				cs[2].FrozenUntil = t + 600;
		}, maxPlayerLag: 3000);

		Check(s.GetBuffer(1).Value.Ms == 150, $"an upload stall leaves the buffer alone ({s.GetBuffer(1).Value.Ms}ms)");
		Check(bufferAfterTwoFreezes > 800, $"two freezes are taken for dropouts and buffered ({bufferAfterTwoFreezes}ms)");
		Check(s.GetBuffer(2).Value.Ms == 150, $"a third, shorter freeze shows the buffer is pointless and removes it ({s.GetBuffer(2).Value.Ms}ms)");
		Check(logs.Any(l => l.Contains("P2's game froze") && l.Contains("back to the normal buffer")), "the correction is logged");
		s.RemoveClient(2);
		var summary = logs.Last(l => l.StartsWith("Summary for P2"));
		Check(summary.Contains("froze 3 time") && !summary.Contains("dropped out"), "all three freezes are reported as freezes: " + summary);
		s.RemoveClient(1);
		var summary1 = logs.Last(l => l.StartsWith("Summary for P1"));
		Check(summary1.Contains("packets were delayed 1 time") && !summary1.Contains("dropped out"), "an upload stall is reported as a delay, not a dropout: " + summary1);
	}

	static void StallsDoNotSetThePace()
	{
		// P1's game freezes for 0.6s of every second (its ping replies keep coming, so these are freezes, not
		// dropouts) and its computer manages 120% in between: it falls behind 0.12s per second. v1.0 would have
		// averaged that into "managing 48%" and slowed everyone to 48%. Now the freezes are taken out of the
		// rate, the game is never slowed, the log says why, and P1 catches up at turbo speed once it stops freezing.
		var worst = 0L;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 1.2 }, 90000, 0, (t, cs, sched) =>
		{
			if (t >= 10000 && t < 60000)
				cs[1].FrozenUntil = t + 600;
			if (t == 60000)
				cs[1].Capacity = 3.0; // the load that caused the freezes is gone
		}, maxPlayerLag: 3000);

		Check(!logs.Any(l => l.StartsWith("Slowing the game")), "a player who keeps up between freezes never slows the game");
		Check(logs.Any(l => l.Contains("P1 is") && l.Contains("not their computer")),
			"the log attributes the lateness to the holes: " + (logs.FirstOrDefault(l => l.Contains("not their computer")) ?? "(no such line)"));
		Check(logs.Any(l => l.StartsWith("Players behind: P1") && float.Parse(l.Split(' ')[3].TrimEnd('s', '.'), System.Globalization.CultureInfo.InvariantCulture) > 3), "P1 did fall more than 3s behind: " + string.Join(" / ", logs.Where(l => l.StartsWith("Players behind")).Take(4)));
		Check(clients[1].MinScale < 0.5f, $"P1 is asked for turbo speed (scale {clients[1].MinScale})");
		Check(clients[0].NextFrame - clients[1].NextFrame < 5, $"P1 is caught up again at the end ({clients[0].NextFrame - clients[1].NextFrame} frames apart)");
		Check(clients[0].Stalls < 5, $"the other player never noticed ({clients[0].Stalls} stalled ticks)");
	}

	static void OutagesLongerThanTheBufferAreAbsorbedByTurbo()
	{
		// P1's connection is dead for 3.2s out of every 3.5s for half a minute: more than the buffer covers. The
		// game is never slowed for it, nobody else notices, and P1 is back in step once the connection settles.
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 3.0 }, 60000, 0, (t, cs, sched) =>
		{
			if (t >= 10000 && t < 40000 && t % 3500 == 0)
				cs[1].DownDeadUntil = cs[1].UpDeadUntil = t + 3200;
		}, maxPlayerLag: 3000);

		Check(!logs.Any(l => l.StartsWith("Slowing the game")), "repeated outages never slow the game");
		Check(s.GetBuffer(1).Value.Ms >= 1000, $"the buffer grew to its cap ({s.GetBuffer(1).Value.Ms}ms)");
		Check(clients[1].MinScale < 0.6f, $"P1 is asked for more than the ordinary 143% (scale {clients[1].MinScale})");
		var apart = clients[0].NextFrame - clients[1].NextFrame;
		Check(apart < s.GetBuffer(1).Value.Ms / 120 + 5, $"P1 is caught up again at the end, allowing for its buffer ({apart} frames apart, buffer {s.GetBuffer(1).Value.Ms}ms)");
		Check(clients[0].Stalls < 5, $"the other player never noticed ({clients[0].Stalls} stalled ticks)");
	}

	static void WaitForAStoppedPlayerIsBounded()
	{
		// P1's connection dies completely for 20s. The game pauses for at most the configured 3s, then continues
		// without P1; when P1 comes back it catches up and is waited for again.
		var framesAtOutageEnd = 0;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 3.0 }, 60000, 0, (t, cs, sched) =>
		{
			if (t == 10000)
				cs[1].DownDeadUntil = cs[1].UpDeadUntil = t + 20000;
			if (t == 30000)
				framesAtOutageEnd = cs[0].NextFrame;
		}, maxPlayerLag: 3000);

		// 20s of outage at 8.33 frames/s = 167 frames if nothing paused; a 3s pause costs 25
		Check(framesAtOutageEnd > 140 * 1 + 83, $"the others lost at most a few seconds during a 20s outage ({framesAtOutageEnd} frames by 30s)");
		Check(logs.Any(l => l.Contains("Everyone waited") && l.Contains("continues without them")), "the bounded wait is logged");
		var continued = s.TakeContinuedWithout();
		Check(continued != null && continued.SequenceEqual(new[] { 1 }), "the event is handed to the announcer once");
		Check(s.TakeContinuedWithout() == null, "...and only once");
		Check(clients[0].NextFrame - clients[1].NextFrame < 10, $"P1 caught up after the outage ({clients[0].NextFrame - clients[1].NextFrame} frames apart)");
		Check(logs.Any(l => l.Contains("P1 has caught up again")), "the return is logged");
	}

	static void SawtoothIsDamped()
	{
		// P1's computer manages 60% for two minutes. After the first slowdown the game should settle near 60%
		// and only probe upwards occasionally, instead of ramping up and slowing down every twenty seconds.
		var speeds = new List<int>();
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.6 }, 150000, 0, (t, cs, sched) =>
		{
			if (t >= 60000)
				speeds.Add(sched.SpeedPercent);
		});

		var slowdowns = logs.Count(l => l.StartsWith("Slowing the game"));
		Check(slowdowns <= 4, $"few slowdowns for a steadily slow computer ({slowdowns} in 150s)");
		Check(speeds.Min() >= 50 && speeds.Max() <= 70, $"after settling the speed stays close to the computer's capacity ({speeds.Min()}-{speeds.Max()}%)");
		Check(logs.Count(l => l.StartsWith("Speeding the game up to")) <= 2, $"probes against a steady ceiling are rare ({logs.Count(l => l.StartsWith("Speeding the game up to"))} failed in 150s)");
	}

	static void RecoveryAcceleratesWhenTheLoadPasses()
	{
		// P1 manages 45% for 20s (a big battle), then is fast again. The game should be back to full speed well
		// within a minute of the load passing, not creep up a point at a time.
		long backAt = -1;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.45 }, 100000, 0, (t, cs, sched) =>
		{
			if (t == 25000)
				cs[1].Capacity = 2.0;
			if (t > 25000 && backAt < 0 && sched.SpeedPercent == 100)
				backAt = t;
		});

		Check(logs.Any(l => l.StartsWith("Slowing the game")), "precondition: the game was slowed");
		Check(backAt > 0 && backAt - 25000 <= 60000, $"back to full speed within 60s of the load passing ({(backAt - 25000) / 1000}s)");
	}

	static void FloorLeavesBehindAndTurboBringsBack()
	{
		// With a 50% floor, P1 at 35% is left behind and the others play at full speed; when P1's computer
		// recovers it catches up at turbo speed and is back in the game.
		var othersSpeedWhileBehind = new List<int>();
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.35, 2.0 }, 90000, 50, (t, cs, sched) =>
		{
			if (t == 40000)
				cs[1].Capacity = 3.0;
			if (t > 20000 && t < 40000)
				othersSpeedWhileBehind.Add(sched.SpeedPercent);
		});

		Check(logs.Any(l => l.Contains("P1's computer is too slow")), "P1 is left behind by the floor");
		Check(othersSpeedWhileBehind.Count > 0 && othersSpeedWhileBehind.All(x => x == 100), $"the game runs at full speed for the others meanwhile ({string.Join(",", othersSpeedWhileBehind.Distinct())}%)");
		Check(clients[1].MinScale < 0.3f, $"P1 is asked for turbo speed (scale {clients[1].MinScale})");
		Check(clients[0].NextFrame - clients[1].NextFrame < 10, $"P1 caught up once its computer recovered ({clients[0].NextFrame - clients[1].NextFrame} frames apart)");
		Check(logs.Any(l => l.Contains("P1 has caught up")), "the return is logged");
	}

	static void TwoPlayersFollowTheSlowerComputer()
	{
		// In a two-player game there is nobody to protect by leaving the slower player behind, so the floor does
		// not apply: the game follows P1's 35% computer.
		var minSpeed = 100;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.35 }, 60000, 50, (t, cs, sched) => minSpeed = Math.Min(minSpeed, sched.SpeedPercent));
		Check(!logs.Any(l => l.Contains("too slow")), "nobody is left behind in a two-player game");
		Check(minSpeed <= 40, $"the game follows the slower computer ({minSpeed}%)");
		Check(clients[0].NextFrame - clients[1].NextFrame < 40, $"the two players stay together ({clients[0].NextFrame - clients[1].NextFrame} frames apart)");
	}

	static void AnnouncerIsNotChatty()
	{
		var a = new GameSpeedAnnouncer(false, "Type !kickslow to vote.", i => $"P{i}");
		var messages = new List<(long T, string M)>();
		var privateLines = new List<(long T, int Client, string M)>();
		void Step(long t, int speed, int? slowest = null)
		{
			var m = a.Tick(t, speed, slowest, 0, -1, 10, slowest.HasValue ? speed + 2 : 0);
			if (m?.Message != null)
				messages.Add((t, m.Value.Message));
			if (m?.PrivateMessage != null)
				privateLines.Add((t, m.Value.PrivateClient, m.Value.PrivateMessage));
		}

		// Full speed for a minute: silence
		for (long t = 0; t < 60000; t += 1000)
			Step(t, 100);
		Check(messages.Count == 0, "nothing is said while the game runs at full speed");

		// Slowed to 85% for P1: one message, at once; the private message names nobody publicly
		Step(60000, 85, 1);
		Check(messages.Count == 1 && messages[0].M.StartsWith("Slowing the game to 85%") && !messages[0].M.Contains("P1") && messages[0].M.Contains("!kickslow"),
			"the first slowdown is announced immediately, without naming the player: " + (messages.Count > 0 ? messages[0].M : ""));
		Check(privateLines.Count == 0, "the private line does not go out in the same instant as the public one");

		// Drifting 85 -> 80 -> 78 -> 82 over the next 25s: nothing new (small changes, too soon)
		var drift = new[] { 84, 82, 80, 79, 78, 78, 80, 82, 82, 81, 80, 79, 78, 78, 78, 79, 80, 81, 82, 82, 82, 82, 82, 82 };
		for (var i = 0; i < drift.Length; i++)
			Step(61000 + i * 1000, drift[i], 1);
		Check(messages.Count == 1, $"small drifts are not announced ({messages.Count} messages)");
		Check(privateLines.Count == 1 && privateLines[0].Client == 1 && privateLines[0].T >= 62000 && privateLines[0].T <= 64000
			&& privateLines[0].M.StartsWith(">>> P1, THIS IS ABOUT YOU: the game is slowed to 85%") && privateLines[0].M.Contains("managing about 87%")
			&& privateLines[0].M.Contains("\"Enable VSync\"") && privateLines[0].M.Contains("\"Limit framerate to game tick rate\"") && !privateLines[0].M.Contains("60 Hz"),
			"the private line follows a couple of seconds later, flagged with the player's name in capitals, their measured speed and the settings to change: "
			+ (privateLines.Count > 0 ? privateLines[0].M : "(none)"));

		// Down to 70% at t=90s (30s after the first message, 15 points lower): announced
		Step(90000, 70, 1);
		Check(messages.Count == 2 && messages[1].M.StartsWith("Slowing the game to 70%"), "a large further slowdown is announced once the interval has passed");
		Step(93000, 70, 1);
		Check(privateLines.Count == 2 && privateLines[1].M.Contains("slowed to 70%") && !privateLines.Any(l => l.M.Contains("keeping up again")),
			"a further slowdown for the same player brings a new private line and no all-clear");

		// Flapping between 100% and 90% every 5s for a minute: at most one message per 30s, never "back to full speed"
		var before = messages.Count;
		for (long t = 91000; t < 151000; t += 1000)
			Step(t, (t / 5000) % 2 == 0 ? 100 : 90, (t / 5000) % 2 == 0 ? null : 1);
		Check(messages.Count - before <= 2, $"flapping produces at most one message per 30s ({messages.Count - before} in 60s)");
		Check(!messages.Skip(before).Any(m => m.M.Contains("full speed")), "brief recoveries are not announced as full speed");

		// Sustained full speed: announced once, then silence
		var lastBefore = messages.Count;
		for (long t = 151000; t < 300000; t += 1000)
			Step(t, 100);
		Check(messages.Count == lastBefore + 1 && messages.Last().M == "The game is back to full speed.", $"sustained full speed is announced exactly once ({messages.Count - lastBefore})");
		var allClear = privateLines.Where(l => l.T > 151000).ToList();
		Check(allClear.Count == 1 && allClear[0].Client == 1 && allClear[0].M == ">>> P1: your computer is keeping up again."
			&& allClear[0].T > messages.Last().T && allClear[0].T <= messages.Last().T + 4000,
			"the player told is given the all-clear once, shortly after full speed is announced: " + string.Join(" | ", allClear.Select(l => l.M)));

		// When the slowest player is kicked, the return to full speed is announced at once, rate limit or not
		var kick = new GameSpeedAnnouncer(false, null, i => $"P{i}");
		kick.Tick(0, 80, 1, 0, -1, 10);
		Check(kick.Tick(3000, 80, 1, 0, -1, 10)?.PrivateClient == 1, "precondition: the private line has gone out");
		Check(kick.Tick(5000, 80, 1, 0, -1, 10) == null, "precondition: within the rate limit nothing is said");
		kick.SlowestPlayerGone();
		var afterKick = kick.Tick(6000, 100, null, 0, -1, 10);
		Check(afterKick != null && afterKick.Value.Message == "The game is back to full speed.", "full speed is announced immediately after the slowest player is kicked");
		Check(kick.Tick(7000, 100, null, 0, -1, 10) == null, "...and only once");

		// With naming on, the message names the player; at a fast game speed the private advice mentions VSync and the monitor
		var named = new GameSpeedAnnouncer(true, null, i => $"P{i}", fastGameSpeed: true);
		var m1 = named.Tick(0, 80, 1, 0, -1, 10, 82);
		Check(m1 != null && m1.Value.Message.Contains("P1's computer") && m1.Value.PrivateMessage == null,
			"naming option names the player publicly; the private line follows on its own");
		var pm = named.Tick(3000, 80, 1, 0, -1, 10, 82);
		Check(pm != null && pm.Value.Message == null && pm.Value.PrivateClient == 1 && pm.Value.PrivateMessage.StartsWith(">>> P1, THIS IS ABOUT YOU:")
			&& pm.Value.PrivateMessage.Contains("At this game speed, VSync alone causes this on a 60 Hz monitor."),
			"at a fast game speed the private line says that VSync alone does it: " + (pm?.PrivateMessage ?? "(none)"));

		// The slowest player changes while the game is slow: the first gets the all-clear, the second the flag
		var m3 = named.Tick(40000, 70, 2, 0, -1, 10, 72);
		Check(m3 != null && m3.Value.Message.Contains("P2's computer"), "a slowdown for another player is announced");
		var l1 = named.Tick(43000, 70, 2, 0, -1, 10, 72);
		var l2 = named.Tick(44000, 70, 2, 0, -1, 10, 72);
		Check(l1?.PrivateClient == 1 && l1.Value.PrivateMessage == ">>> P1: your computer is keeping up again." && l2?.PrivateClient == 2 && l2.Value.PrivateMessage.StartsWith(">>> P2, THIS IS ABOUT YOU:"),
			"the player told before gets the all-clear and the new one the flag: " + (l1?.PrivateMessage ?? "(none)") + " / " + (l2?.PrivateMessage ?? "(none)"));
		Check(named.Tick(45000, 70, 2, 0, -1, 10, 72) == null, "...and nothing more");

		// A player left behind by the speed floor is announced
		var floor = new GameSpeedAnnouncer(false, null, i => $"P{i}");
		var m2 = floor.Tick(0, 100, null, 1, 2, 75);
		Check(m2 != null && m2.Value.Message.Contains("75%") && !m2.Value.Message.Contains("P2") && m2.Value.PrivateMessage == null,
			"a player falling behind on their own is announced without a name");
		var f2 = floor.Tick(3000, 100, null, 1, 2, 75);
		Check(f2?.PrivateClient == 2 && f2.Value.PrivateMessage.StartsWith(">>> P2, THIS IS ABOUT YOU: your computer can't keep up with the game even at 75%"),
			"...and told privately, flagged: " + (f2?.PrivateMessage ?? "(none)"));
	}

	static void AnnouncerSkipsPartialRecoveries()
	{
		var a = new GameSpeedAnnouncer(false, null, i => $"P{i}");
		Check(a.Tick(0, 100, null, 0, -1, 10) == null, "quiet at full speed");
		Check(a.Tick(1000, 60, 1, 0, -1, 10)?.Message.StartsWith("Slowing the game to 60%") == true, "the slowdown is announced");
		Check(a.Tick(4000, 60, 1, 0, -1, 10)?.PrivateClient == 1, "the private line follows");
		Check(a.Tick(40000, 90, 1, 0, -1, 10) == null, "a partial recovery (60% -> 90%) is not announced");
		var again = a.Tick(80000, 78, 1, 0, -1, 10);
		Check(again?.Message.StartsWith("Slowing the game to 78%") == true, "a slowdown from the quiet peak (90% -> 78%) is announced: " + (again?.Message ?? "(nothing)"));
		Check(a.Tick(83000, 78, 1, 0, -1, 10)?.PrivateMessage.Contains("slowed to 78%") == true, "the private line follows again");
		Check(a.Tick(120000, 100, null, 0, -1, 10) == null, "full speed waits for the settle time");
		Check(a.Tick(131000, 100, null, 0, -1, 10)?.Message == "The game is back to full speed.", "full speed is announced once settled");
		a.ContinuedWithout(2);
		var cont = a.Tick(132000, 100, null, 0, -1, 10);
		Check(cont?.Message.StartsWith("P2 has stopped responding; the game continues without them") == true, "continuing without a stopped player is announced at once: " + (cont?.Message ?? "(nothing)"));
		Check(a.Tick(133000, 100, null, 0, -1, 10) == null, "...and only once");
		Check(a.Tick(134000, 100, null, 0, -1, 10)?.PrivateMessage == ">>> P1: your computer is keeping up again.", "the all-clear follows full speed");
		Check(a.Tick(135000, 100, null, 0, -1, 10) == null, "...and that is all");
	}

	static void TickScalesAreValid()
	{
		const int Timestep = 40;
		var s = new FrameScheduler(Timestep, 3, 1, new[] { 0, 1 });

		// Drive a couple of control intervals with both clients on time
		for (long t = 0; t <= 3000; t += 40)
		{
			foreach (var (frame, _) in Drain(s, t))
			{
				s.ReceivePacket(0, frame, Order, t);
				s.ReceivePacket(1, frame, Order, t);
			}

			foreach (var (_, scale) in s.GetTickScales(t))
			{
				var tickMs = (int)(scale * Timestep); // exactly how the release client applies it
				Check(tickMs >= 1, $"applied tick length stays >= 1ms (scale {scale})");
				Check(scale >= 0.5f && scale <= 2.0f, $"scale in a sane range ({scale})");
			}
		}
	}

	static void OffsetClientIsNotHeldForever()
	{
		// P1 manages 75% and its lateness reads 600 ms high (its round trip is longer than its ping says). After the
		// slowdown the hold must still work, and when P1's computer recovers at 45 s the game must get back to full
		// speed within 90 s. In v1.2 such a client was held at the measured speed for the rest of the game.
		var fullAt = -1L;
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.75, 2.0 }, 150000, 0, (t, cs, sched) =>
		{
			if (t == 45000)
				cs[1].Capacity = 2.0;
			if (t > 45000 && fullAt < 0 && sched.SpeedPercent == 100)
				fullAt = t;
		}, maxPlayerLag: 3000, extraDelays: new long[] { 0, 600, 0 });

		Check(logs.Any(l => l.StartsWith("Slowing the game", StringComparison.Ordinal) && l.Contains("P1")), "offset: P1 was slowed down for");
		Check(fullAt > 0 && fullAt - 45000 <= 90000, $"offset: full speed within 90s of the recovery (at {fullAt})");
		Check(s.SpeedPercent == 100, "offset: at full speed at the end");
		Check(clients[0].NextFrame - clients[1].NextFrame < 20, "offset: P1 ended close to the others");
	}

	static void OffsetClientSettlesLikeAnyOther()
	{
		// P1 manages a steady 75% with a 600 ms lateness offset: the hold settles near 75% and probes only now and
		// then, as it would for a client with no offset.
		var speeds = new List<int>();
		var (logs, _, clients) = RunWithCapacities(new[] { 2.0, 0.75, 2.0 }, 180000, 0, (t, cs, sched) =>
		{
			if (t >= 40000)
				speeds.Add(sched.SpeedPercent);
		}, maxPlayerLag: 3000, extraDelays: new long[] { 0, 600, 0 });

		Check(logs.Count(l => l.StartsWith("Slowing the game", StringComparison.Ordinal)) <= 2, "offset steady: at most two slowdowns");
		Check(logs.Count(l => l.StartsWith("Speeding the game up to", StringComparison.Ordinal)) <= 3, "offset steady: at most three failed probes in three minutes");
		Check(speeds.Min() >= 66 && speeds.Max() <= 85, $"offset steady: speed stays within a few points of the ceiling ({speeds.Min()}-{speeds.Max()})");
		Check(clients[0].NextFrame - clients[1].NextFrame < 20, "offset steady: P1 stays close to the others");
	}

	static void StartIsDelayedForTheLastLoader()
	{
		// With a start delay, the first frame closes that long after the last client has finished loading
		var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 }, startDelay: 2000);
		s.ReceivePacket(0, 1, Order, 0);
		var before = 0;
		for (var t = 0L; t < 3500; t += 10)
		{
			if (t == 1500)
				s.ReceivePacket(1, 1, Order, t);
			before += Drain(s, t).Count;
		}

		Check(before == 0, "start delay: no frame closes within 2s of the last client loading");
		Check(Drain(s, 3500).Count == 1, "start delay: the first frame closes once the delay is over");
	}

	static int Main()
	{
		FramesAreConsecutiveAndComplete();
		AckCountsMatchPacketsSent();
		SilentPlayerBlocksThenResumes();
		SilentSpectatorNeverBlocks();
		TricklingPlayerNeverBlocks();
		TooSlowComputerDoesNotSlowTheGame();
		DefeatedPlayerNoLongerBlocks();
		FarBehindSpectatorIsStillToldToCatchUp();
		SlowestPlayerIsIdentifiedAndCleared();
		KickedSlowestPlayerSpeedsBackUp();
		LeavingSlowestPlayerFindsTheNextCeiling();
		FailedProbeRevertsToTheMeasuredSpeed();
		CreepingHoldSettlesWithoutProbing();
		ProjectedLagTriggersBeforeTheBudget();
		VoteCommandsAreRecognised();
		CapacityIsMeasuredWhileCatchingUp();
		DownloadHoleBecomesBuffer();
		UploadStallAndFreezeDoNotKeepABuffer();
		StallsDoNotSetThePace();
		OutagesLongerThanTheBufferAreAbsorbedByTurbo();
		WaitForAStoppedPlayerIsBounded();
		SawtoothIsDamped();
		RecoveryAcceleratesWhenTheLoadPasses();
		FloorLeavesBehindAndTurboBringsBack();
		TwoPlayersFollowTheSlowerComputer();
		AnnouncerIsNotChatty();
		AnnouncerSkipsPartialRecoveries();
		TickScalesAreValid();
		OffsetClientIsNotHeldForever();
		OffsetClientSettlesLikeAnyOther();
		StartIsDelayedForTheLastLoader();
		Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
		return failures == 0 ? 0 : 1;
	}
}
