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
		var s = new FrameScheduler(Timestep, 3, 1, new[] { 0, 1 }, 0, null, logs.Add, null, 75);
		s.ReceivePing(0, new[] { 20 }); s.ReceivePing(1, new[] { 20 });
		s.ReceivePacket(0, 1, Order, 0); s.ReceivePacket(1, 1, Order, 0);
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
				s.ReceivePacket(0, closed.Dequeue().Frame, Order, t);

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

		public SimClient(int index, double capacity, int timestep, int firstFrame)
		{
			Index = index; Capacity = capacity; TickMs = timestep; NextFrame = firstFrame;
		}

		public void Run(FrameScheduler s, long now, int timestep, int netFrameInterval)
		{
			// A frozen client does nothing, and starts again from where it was when it unfreezes
			if (now < FrozenUntil)
				return;

			if (FrozenUntil > 0)
			{
				FrozenUntil = 0;
				NextFrameTime = now;
			}

			while (now >= NextFrameTime && Inbox.Count > 0 && Inbox.Peek().Frame == NextFrame && Inbox.Peek().Available <= now)
			{
				Inbox.Dequeue();
				s.ReceivePacket(Index, NextFrame++, Order, now);
				var tick = Math.Max(TickMs, timestep / Capacity);
				NextFrameTime = Math.Max(NextFrameTime, now) + (tick * netFrameInterval);
			}
		}
	}

	static (List<string> Logs, FrameScheduler S, List<SimClient> Clients) RunWithCapacities(double[] capacities, long duration, int minGameSpeed,
		Action<long, List<SimClient>, FrameScheduler> onSecond = null)
	{
		const int Timestep = 40, Interval = 3, First = 4, Delay = 20;
		var logs = new List<string>();
		var indices = Enumerable.Range(0, capacities.Length).ToArray();
		var s = new FrameScheduler(Timestep, Interval, First, indices, 0, i => $"P{i}", logs.Add, null, minGameSpeed);
		var clients = indices.Select(i => new SimClient(i, capacities[i], Timestep, First)).ToList();
		foreach (var c in clients)
		{
			s.ReceivePing(c.Index, new[] { 2 * Delay });
			for (var f = 1; f < First; f++)
				s.ReceivePacket(c.Index, f, Order, 0); // the frames a client sends while the game is starting
		}

		for (long t = 0; t <= duration; t += 10)
		{
			while (s.TryCloseFrame(t, out var frame, out _))
				foreach (var c in clients)
					c.Inbox.Enqueue((frame, t + Delay));

			foreach (var c in clients)
				c.Run(s, t, Timestep, Interval);

			foreach (var (client, scale) in s.GetTickScales(t))
				clients[client].TickMs = Math.Max((int)(scale * Timestep), 1);

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

	static void KickedSlowestPlayerRestoresFullSpeedAtOnce()
	{
		// P1 (80%) slows the game; at 30s P1 is kicked. The game must be back at 100% within a second or two,
		// not ramp up over half a minute, and P0 must be told its normal tick length straight away.
		var speedAfterKick = new List<(long T, int Speed, int Tick0)>();
		var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.8, 2.0 }, 45000, 0, (t, cs, sched) =>
		{
			if (t == 30000)
				sched.RemoveClient(1);
			if (t > 30000)
				speedAfterKick.Add((t, sched.SpeedPercent, cs[0].TickMs));
		});

		Check(speedAfterKick.Count > 0 && speedAfterKick[0].Speed == 100 && speedAfterKick.All(x => x.Speed == 100),
			$"speed is 100% from the first second after the kick ({string.Join(",", speedAfterKick.Take(5).Select(x => x.Speed))})");
		Check(speedAfterKick.Skip(1).All(x => x.Tick0 <= 40), $"the remaining players run at normal tick length right after the kick ({string.Join(",", speedAfterKick.Take(5).Select(x => x.Tick0))})");
		Check(logs.Any(l => l.Contains("P1 has left; the game is back to full speed")), "the snap back is logged");
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

	static void AnnouncerIsNotChatty()
	{
		var a = new GameSpeedAnnouncer(false, "Type !kickslow to vote.", i => $"P{i}");
		var messages = new List<(long T, string M)>();
		void Step(long t, int speed, int? slowest = null)
		{
			var m = a.Tick(t, speed, slowest, 0, -1, 10);
			if (m != null)
				messages.Add((t, m.Value.Message));
		}

		// Full speed for a minute: silence
		for (long t = 0; t < 60000; t += 1000)
			Step(t, 100);
		Check(messages.Count == 0, "nothing is said while the game runs at full speed");

		// Slowed to 85% for P1: one message, at once; the private message names nobody publicly
		Step(60000, 85, 1);
		Check(messages.Count == 1 && messages[0].M.StartsWith("Slowing the game to 85%") && !messages[0].M.Contains("P1") && messages[0].M.Contains("!kickslow"),
			"the first slowdown is announced immediately, without naming the player: " + (messages.Count > 0 ? messages[0].M : ""));

		// Drifting 85 -> 80 -> 78 -> 82 over the next 25s: nothing new (small changes, too soon)
		var drift = new[] { 84, 82, 80, 79, 78, 78, 80, 82, 82, 81, 80, 79, 78, 78, 78, 79, 80, 81, 82, 82, 82, 82, 82, 82 };
		for (var i = 0; i < drift.Length; i++)
			Step(61000 + i * 1000, drift[i], 1);
		Check(messages.Count == 1, $"small drifts are not announced ({messages.Count} messages)");

		// Down to 70% at t=90s (30s after the first message, 15 points lower): announced
		Step(90000, 70, 1);
		Check(messages.Count == 2 && messages[1].M.StartsWith("Slowing the game to 70%"), "a large further slowdown is announced once the interval has passed");

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

		// When the slowest player is kicked, the return to full speed is announced at once, rate limit or not
		var kick = new GameSpeedAnnouncer(false, null, i => $"P{i}");
		kick.Tick(0, 80, 1, 0, -1, 10);
		Check(kick.Tick(5000, 80, 1, 0, -1, 10) == null, "precondition: within the rate limit nothing is said");
		kick.SlowestPlayerGone();
		var afterKick = kick.Tick(6000, 100, null, 0, -1, 10);
		Check(afterKick != null && afterKick.Value.Message == "The game is back to full speed.", "full speed is announced immediately after the slowest player is kicked");
		Check(kick.Tick(7000, 100, null, 0, -1, 10) == null, "...and only once");

		// With naming on, the message names the player
		var named = new GameSpeedAnnouncer(true, null, i => $"P{i}");
		var m1 = named.Tick(0, 80, 1, 0, -1, 10);
		Check(m1 != null && m1.Value.Message.Contains("P1's computer") && m1.Value.PrivateClient == 1 && m1.Value.PrivateMessage.Contains("your computer"),
			"naming option names the player publicly and tells them privately");

		// A player left behind by the speed floor is announced
		var floor = new GameSpeedAnnouncer(false, null, i => $"P{i}");
		var m2 = floor.Tick(0, 100, null, 1, 2, 75);
		Check(m2 != null && m2.Value.Message.Contains("75%") && !m2.Value.Message.Contains("P2") && m2.Value.PrivateClient == 2,
			"a player falling behind on their own is announced without a name, and told privately");
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
		KickedSlowestPlayerRestoresFullSpeedAtOnce();
		CapacityIsMeasuredWhileCatchingUp();
		AnnouncerIsNotChatty();
		TickScalesAreValid();
		Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
		return failures == 0 ? 0 : 1;
	}
}
