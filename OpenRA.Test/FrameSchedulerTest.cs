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
using System.Linq;
using NUnit.Framework;
using OpenRA.Server;

namespace OpenRA.Test
{
	[TestFixture]
	public class FrameSchedulerTest
	{
		static readonly byte[] Order = { 1, 2, 3, 4, 5 };

		static List<(int Frame, List<(int Client, byte[] Data, int Count)> Contents)> Drain(FrameScheduler s, long now)
		{
			var closed = new List<(int, List<(int, byte[], int)>)>();
			while (s.TryCloseFrame(now, out var frame, out var contents))
				closed.Add((frame, contents));

			return closed;
		}

		[Test]
		public void FramesAreConsecutiveAndComplete()
		{
			const int First = 7;
			var s = new FrameScheduler(40, 3, First, new[] { 0, 1 });

			s.ReceivePacket(0, 1, Order, 0);
			Assert.That(Drain(s, 1000), Is.Empty, "No frames should close before every client has reported.");
			s.ReceivePacket(1, 1, Order, 0);

			var all = new List<(int, List<(int, byte[], int)>)>();
			for (var t = 0; t <= 2000; t += 40)
				all.AddRange(Drain(s, t));

			Assert.That(all, Is.Not.Empty);
			Assert.That(all[0].Item1, Is.EqualTo(First), "First closed frame should be firstFrame.");
			Assert.That(all.Select(f => f.Item1), Is.EqualTo(Enumerable.Range(First, all.Count)), "Frame numbers must be strictly consecutive.");
			Assert.That(all.All(f => f.Item2.Select(c => c.Item1).OrderBy(x => x).SequenceEqual(new[] { 0, 1 })), Is.True, "Every client must appear in every frame.");
		}

		[Test]
		public void AckCountsMatchPacketsSent()
		{
			var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
			s.ReceivePacket(0, 1, Order, 0);
			s.ReceivePacket(1, 1, Order, 0);
			Drain(s, 0);

			s.ReceivePacket(0, 2, Order, 130);
			s.ReceivePacket(0, 3, Order, 131);
			s.ReceivePacket(0, 4, Order, 132);
			var closed = Drain(s, 140);

			Assert.That(closed, Has.Count.EqualTo(1));
			var byClient = closed[0].Item2.ToDictionary(c => c.Item1, c => c.Item3);
			Assert.That(byClient[0], Is.EqualTo(3), "Ack count should equal packets sent since last close.");
			Assert.That(byClient[1], Is.EqualTo(0), "A silent client should get an empty frame (ack 0).");

			var data0 = closed[0].Item2.First(c => c.Item1 == 0).Item2;
			Assert.That(data0.Length, Is.EqualTo(3 * Order.Length), "Merged packet data should be concatenated.");
		}

		[Test]
		public void SilentPlayerBlocksThenResumes()
		{
			var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
			s.ReceivePacket(0, 1, Order, 0);
			s.ReceivePacket(1, 1, Order, 0);

			var lastFrame = 0;
			for (long t = 0; t <= 10000; t += 40)
			{
				foreach (var (frame, _) in Drain(s, t))
					lastFrame = frame;

				s.ReceivePacket(0, lastFrame + 5, Order, t);
			}

			var stalledAt = lastFrame;
			Assert.That(stalledAt, Is.GreaterThan(0).And.LessThan(80), "A silent player should block everyone within a bounded lead.");

			s.ReceivePacket(1, stalledAt, Order, 10000);
			var resumed = false;
			for (long t = 10000; t <= 20000; t += 40)
				if (Drain(s, t).Count > 0)
				{
					resumed = true;
					break;
				}

			Assert.That(resumed, Is.True, "Closing should resume after the silent player catches up.");
		}

		[Test]
		public void DefeatedPlayerNoLongerBlocksTheGame()
		{
			var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
			s.ReceivePacket(0, 1, Order, 0);
			s.ReceivePacket(1, 1, Order, 0);
			s.SetDefeated(1);

			// Client 1 (defeated) goes silent; client 0 keeps up
			var lastFrame = 0;
			for (long t = 0; t <= 15000; t += 40)
			{
				foreach (var (frame, _) in Drain(s, t))
					lastFrame = frame;

				s.ReceivePacket(0, lastFrame + 5, Order, t);
			}

			Assert.That(lastFrame, Is.GreaterThan(100), "A silent defeated player must not block the game.");
		}

		[Test]
		public void TricklingPlayerNeverBlocksTheGame()
		{
			var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 });
			s.ReceivePacket(0, 1, Order, 0);
			s.ReceivePacket(1, 1, Order, 0);

			// Client 1 keeps sending, but only advances one frame per second (far slower than the game)
			var lastFrame = 0;
			var slowFrame = 1;
			for (long t = 0; t <= 20000; t += 40)
			{
				foreach (var (frame, _) in Drain(s, t))
					lastFrame = frame;

				s.ReceivePacket(0, lastFrame, Order, t);
				if (t % 1000 == 0 && slowFrame < lastFrame)
					s.ReceivePacket(1, ++slowFrame, Order, t);
			}

			Assert.That(lastFrame, Is.GreaterThan(150), "A player who is still sending frames, however slowly, must not hold the game.");
		}

		[Test]
		public void TooSlowComputerDoesNotSlowTheGameDown()
		{
			const int Timestep = 40;
			var logs = new List<string>();
			var s = new FrameScheduler(Timestep, 3, 1, new[] { 0, 1 }, 0, null, logs.Add, null, 75);
			s.ReceivePing(0, new[] { 20 });
			s.ReceivePing(1, new[] { 20 });
			s.ReceivePacket(0, 1, Order, 0);
			s.ReceivePacket(1, 1, Order, 0);

			// Client 1 runs at half speed: it reports one frame every 240ms while frames close every 120ms
			var lastFrame = 0;
			var slowFrame = 1;
			var scale0 = 1f;
			var closed = new Queue<(int Frame, long At)>();
			for (long t = 0; t <= 60000; t += 40)
			{
				foreach (var (frame, _) in Drain(s, t))
				{
					lastFrame = frame;
					closed.Enqueue((frame, t));
				}

				// Client 0 behaves like a healthy real client: it uses each frame ~170ms after it closed
				// (the 150ms target buffer plus its 20ms round trip), so it needs no per-client correction
				while (closed.Count > 0 && t >= closed.Peek().At + 170)
					s.ReceivePacket(0, closed.Dequeue().Frame, Order, t);

				if (t % 240 == 0 && slowFrame < lastFrame)
					s.ReceivePacket(1, ++slowFrame, Order, t);

				foreach (var (client, scale) in s.GetTickScales(t))
					if (client == 0)
						scale0 = scale;
			}

			Assert.That(logs.Any(l => l.Contains("too slow")), Is.True, "The half-speed player should be marked too slow.");
			Assert.That((int)(scale0 * Timestep), Is.EqualTo(Timestep), "The game must not stay slowed down for a player it cannot keep in anyway.");
		}

		[Test]
		public void SilentSpectatorNeverBlocks()
		{
			var s = new FrameScheduler(40, 3, 1, new[] { 0, 1 }, spectatorIndices: new[] { 1 });
			s.ReceivePacket(0, 1, Order, 0);
			s.ReceivePacket(1, 1, Order, 0);

			var lastFrame = 0;
			for (long t = 0; t <= 15000; t += 40)
			{
				foreach (var (frame, _) in Drain(s, t))
					lastFrame = frame;

				s.ReceivePacket(0, lastFrame + 5, Order, t);
			}

			Assert.That(lastFrame, Is.GreaterThan(100), "A hopelessly-behind spectator must not block the game.");
		}

		[Test]
		public void FarBehindSpectatorIsStillToldToCatchUp()
		{
			const int Timestep = 40;
			var s = new FrameScheduler(Timestep, 3, 1, new[] { 0, 1 }, spectatorIndices: new[] { 1 });
			s.ReceivePacket(0, 1, Order, 0);
			s.ReceivePacket(1, 1, Order, 0);

			// Spectator goes silent for a long time (well past the tracked-frame history); the player keeps up
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

			Assert.That(lastFrame, Is.GreaterThan(2048), "Precondition: spectator should be behind by more than the tracked history.");
			Assert.That((int)(lastSpectatorScale * Timestep), Is.LessThan(Timestep), "A far-behind spectator must still be told to run faster than normal.");
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
			readonly Queue<int> outbox = new();
			public float MinScale = 1f;

			// Ticks the client wanted to take but had no frame for
			public int Stalls;

			public SimClient(int index, double capacity, int timestep, int firstFrame)
			{
				Index = index;
				Capacity = capacity;
				TickMs = timestep;
				NextFrame = firstFrame;
			}

			public bool LinkDead(long now) => now < DownDeadUntil || now < UpDeadUntil;

			public void Run(FrameScheduler s, long now, int timestep, int netFrameInterval)
			{
				if (now >= UpDeadUntil)
					while (outbox.Count > 0)
						s.ReceivePacket(Index, outbox.Dequeue(), Order, now);

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

		static (List<string> Logs, FrameScheduler Scheduler, List<SimClient> Clients) RunWithCapacities(
			double[] capacities, long duration, int minGameSpeed, Action<long, List<SimClient>, FrameScheduler> onSecond = null,
			int maxPlayerLag = 0, int maxPlayerBuffer = 1500, int maxWait = 3000)
		{
			const int Timestep = 40, Interval = 3, First = 4, Delay = 20;
			var logs = new List<string>();
			var indices = Enumerable.Range(0, capacities.Length).ToArray();
			var s = new FrameScheduler(Timestep, Interval, First, indices, maxPlayerLag, i => $"P{i}", logs.Add, null, minGameSpeed, maxPlayerBuffer, 400, maxWait);
			var clients = indices.Select(i => new SimClient(i, capacities[i], Timestep, First)).ToList();
			foreach (var c in clients)
			{
				s.ReceivePing(c.Index, new[] { 2 * Delay }, 0);
				for (var f = 1; f < First; f++)
					s.ReceivePacket(c.Index, f, Order, 0);
			}

			for (long t = 0; t <= duration; t += 10)
			{
				while (s.TryCloseFrame(t, out var frame, out _))
					foreach (var c in clients)
						c.Inbox.Enqueue((frame, Math.Max(t, c.DownDeadUntil) + Delay));

				foreach (var c in clients)
					c.Run(s, t, Timestep, Interval);

				// Ping replies every 250ms, answered by the client's network thread: they continue while its game is
				// frozen, and stop while its connection is dead
				if (t % 250 == 0)
					foreach (var c in clients)
						if (!c.LinkDead(t))
							s.ReceivePing(c.Index, new[] { 2 * Delay }, t);

				foreach (var (client, scale) in s.GetTickScales(t))
				{
					clients[client].TickMs = Math.Max((int)(scale * Timestep), 1);
					clients[client].MinScale = Math.Min(clients[client].MinScale, scale);
				}

				if (t % 1000 == 0)
					onSecond?.Invoke(t, clients, s);
			}

			return (logs, s, clients);
		}

		[Test]
		public void SlowestPlayerIsIdentifiedAndCleared()
		{
			// P1's computer manages 80%; P0 and P2 are fast. The game should settle near 80% and know P1 is the reason.
			var seenSlowest = new HashSet<int>();
			var minSpeed = 100;
			var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 0.8, 2.0 }, 120000, 0, (t, cs, sched) =>
			{
				if (t == 60000)
					cs[1].Capacity = 2.0; // P1 recovers

				if (t < 60000)
				{
					if (sched.SlowestPlayer.HasValue)
						seenSlowest.Add(sched.SlowestPlayer.Value.Client);
					minSpeed = Math.Min(minSpeed, sched.SpeedPercent);
				}
			});

			Assert.That(seenSlowest, Is.EquivalentTo(new[] { 1 }), "Only the slow computer should be reported as the slowest player.");
			Assert.That(minSpeed, Is.InRange(70, 85), "The game should slow to about the slow computer's speed.");
			Assert.That(logs.Any(l => l.StartsWith("Slowing the game", StringComparison.Ordinal) && l.Contains("P1")), Is.True);
			Assert.That(s.SpeedPercent, Is.EqualTo(100), "Speed should be back to normal once the slow computer recovered.");
			Assert.That(s.SlowestPlayer, Is.Null);
			Assert.That(clients.Min(c => c.NextFrame), Is.GreaterThan(700), "The game should keep progressing.");

			s.RemoveClient(1);
			Assert.That(logs.Last(l => l.StartsWith("Summary for P1", StringComparison.Ordinal)), Does.Contain("while it was slowing the game"));
		}

		[Test]
		public void KickedSlowestPlayerRestoresFullSpeedAtOnce()
		{
			// P1 (80%) slows the game; at 30s P1 is kicked. The game must be back at 100% within a second,
			// not ramp up over half a minute, and P0 must be told its normal tick length straight away.
			var speedAfterKick = new List<(long T, int Speed, int Tick0)>();
			var (logs, _, _) = RunWithCapacities(new[] { 2.0, 0.8, 2.0 }, 45000, 0, (t, cs, sched) =>
			{
				if (t == 30000)
					sched.RemoveClient(1);
				if (t > 30000)
					speedAfterKick.Add((t, sched.SpeedPercent, cs[0].TickMs));
			});

			Assert.That(speedAfterKick.Select(x => x.Speed), Is.All.EqualTo(100), "Speed should be 100% from the first second after the kick.");
			Assert.That(speedAfterKick.Skip(1).Select(x => x.Tick0), Is.All.LessThanOrEqualTo(40),
				"The remaining players should run at normal tick length right after the kick.");
			Assert.That(logs.Any(l => l.Contains("P1 has left; the game is back to full speed")), Is.True);
		}

		[Test]
		public void CapacityIsMeasuredWhileCatchingUp()
		{
			// P1 freezes for 3s and then catches up: while it works through the backlog the scheduler learns a
			// lower bound on what its computer can do, even though the game itself never slows down.
			var (logs, s, _) = RunWithCapacities(new[] { 2.0, 3.0 }, 40000, 0, (t, cs, _) =>
			{
				if (t == 10000)
					cs[1].FrozenUntil = 13000;
			});

			s.RemoveClient(1);
			var summary = logs.Last(l => l.StartsWith("Summary for P1", StringComparison.Ordinal));
			Assert.That(summary, Does.Contain("at least").And.Not.Contain("while it was slowing the game"));
			Assert.That(logs.Any(l => l.StartsWith("Slowing the game", StringComparison.Ordinal)), Is.False, "A hitch must not slow the game.");
		}

		[Test]
		public void DownloadHoleBecomesBuffer()
		{
			// P1's download path dies for 1s at 10s and again at 30s. The first hole stops P1's game and is turned into
			// buffer; the second hole of the same length is covered by that buffer, so P1's game does not stop again.
			var stallsBefore = 0;
			var stallsAfter = 0;
			var bufferAfterFirstHole = 0;
			var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 2.0 }, 45000, 0, (t, cs, sched) =>
			{
				if (t == 10000 || t == 30000)
					cs[1].DownDeadUntil = t + 1000;
				if (t == 12000)
				{
					stallsBefore = cs[1].Stalls;
					bufferAfterFirstHole = sched.GetBuffer(1).Value.Ms;
				}

				if (t == 28000)
					stallsAfter = cs[1].Stalls;
			}, maxPlayerLag: 3000);

			Assert.That(bufferAfterFirstHole, Is.InRange(900, 1500), "A 1s download hole should become about 1s of buffer.");
			Assert.That(s.GetBuffer(1).Value.Ms, Is.LessThan(bufferAfterFirstHole).And.GreaterThan(150), "The buffer should shrink slowly while the connection is quiet.");
			Assert.That(stallsBefore, Is.GreaterThan(50), "The first hole should have stopped P1's game.");
			Assert.That(clients[1].Stalls - stallsAfter, Is.LessThan(10), "The second hole should not stop P1's game.");
			Assert.That(logs.Any(l => l.Contains("P1's connection dropped out for") && l.Contains("buffering")), Is.True, "The buffer growth should be logged.");
			Assert.That(logs.Any(l => l.StartsWith("Slowing the game", StringComparison.Ordinal)), Is.False, "A dropout must never slow the game.");
			Assert.That(clients[0].Stalls, Is.LessThan(5), "The other player must not notice.");
		}

		[Test]
		public void UploadStallAndFreezeDoNotGrowTheBuffer()
		{
			// An upload stall (the client kept playing; its packets arrived in a burst) and a game freeze (its ping
			// replies kept coming) are not what a buffer is for
			var (logs, s, _) = RunWithCapacities(new[] { 2.0, 2.0, 2.0 }, 30000, 0, (t, cs, sched) =>
			{
				if (t == 10000)
				{
					cs[1].UpDeadUntil = t + 1000;
					cs[2].FrozenUntil = t + 1000;
				}
			});

			Assert.That(s.GetBuffer(1).Value.Ms, Is.EqualTo(150), "An upload stall should leave the buffer alone.");
			Assert.That(s.GetBuffer(2).Value.Ms, Is.EqualTo(150), "A frozen game should leave the buffer alone.");
			s.RemoveClient(2);
			Assert.That(logs.Last(l => l.StartsWith("Summary for P2", StringComparison.Ordinal)), Does.Contain("froze 1 time"));
		}

		[Test]
		public void StallsDoNotSetThePace()
		{
			// P1's game freezes for 0.6s of every second (its ping replies keep coming, so these are freezes, not
			// dropouts) and its computer manages 120% in between: it falls behind 0.12s per second. v1.0 would have
			// averaged that into "managing 48%" and slowed everyone to 48%. Now the freezes are taken out of the
			// rate, the game is never slowed, the log says why, and P1 catches up at turbo speed once it stops freezing.
			var (logs, _, clients) = RunWithCapacities(new[] { 2.0, 1.2 }, 90000, 0, (t, cs, sched) =>
			{
				if (t >= 10000 && t < 60000)
					cs[1].FrozenUntil = t + 600;
				if (t == 60000)
					cs[1].Capacity = 3.0;
			}, maxPlayerLag: 3000);

			Assert.That(logs.Any(l => l.StartsWith("Slowing the game", StringComparison.Ordinal)), Is.False, "A player who keeps up between freezes must never slow the game.");
			Assert.That(logs.Any(l => l.Contains("P1 is") && l.Contains("because of freezes") && l.Contains("not their computer")), Is.True, "The log should attribute the lateness to the freezes.");
			Assert.That(clients[1].MinScale, Is.LessThan(0.5f), "P1 should be asked for turbo speed.");
			Assert.That(clients[0].NextFrame - clients[1].NextFrame, Is.LessThan(5), "P1 should be caught up again at the end.");
			Assert.That(clients[0].Stalls, Is.LessThan(5), "The other player must not notice.");
		}

		[Test]
		public void OutagesLongerThanTheBufferAreAbsorbedByTurbo()
		{
			// P1's connection is dead for 3.2s out of every 3.5s for half a minute: more than the buffer covers. The
			// game is never slowed for it, nobody else notices, and P1 is back in step once the connection settles.
			var (logs, s, clients) = RunWithCapacities(new[] { 2.0, 3.0 }, 60000, 0, (t, cs, sched) =>
			{
				if (t >= 10000 && t < 40000 && t % 3500 == 0)
					cs[1].DownDeadUntil = cs[1].UpDeadUntil = t + 3200;
			}, maxPlayerLag: 3000);

			Assert.That(logs.Any(l => l.StartsWith("Slowing the game", StringComparison.Ordinal)), Is.False, "Repeated outages must never slow the game.");
			Assert.That(s.GetBuffer(1).Value.Ms, Is.GreaterThanOrEqualTo(1000), "The buffer should have grown to its cap.");
			Assert.That(clients[1].MinScale, Is.LessThan(0.5f), "P1 should be asked for turbo speed.");
			Assert.That(clients[0].NextFrame - clients[1].NextFrame, Is.LessThan(s.GetBuffer(1).Value.Ms / 120 + 5), "P1 should be caught up again, allowing for its buffer.");
			Assert.That(clients[0].Stalls, Is.LessThan(5), "The other player must not notice.");
		}

		[Test]
		public void WaitForAStoppedPlayerIsBounded()
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

			// 30s at 8.33 frames/s = 250 frames if nothing paused; a 3s pause costs 25
			Assert.That(framesAtOutageEnd, Is.GreaterThan(215), "The others should lose at most a few seconds during a 20s outage.");
			Assert.That(logs.Any(l => l.Contains("Everyone waited") && l.Contains("continues without them")), Is.True, "The bounded wait should be logged.");
			Assert.That(s.TakeContinuedWithout(), Is.EqualTo(new[] { 1 }), "The event should be handed to the announcer once.");
			Assert.That(s.TakeContinuedWithout(), Is.Null);
			Assert.That(clients[0].NextFrame - clients[1].NextFrame, Is.LessThan(10), "P1 should have caught up after the outage.");
			Assert.That(logs.Any(l => l.Contains("P1 has caught up again")), Is.True);
		}

		[Test]
		public void SawtoothIsDamped()
		{
			// P1's computer manages 60% for two minutes. After the first slowdown the game should settle near 60%
			// and only probe upwards occasionally, instead of ramping up and slowing down every twenty seconds.
			var speeds = new List<int>();
			var (logs, _, _) = RunWithCapacities(new[] { 2.0, 0.6 }, 150000, 0, (t, cs, sched) =>
			{
				if (t >= 60000)
					speeds.Add(sched.SpeedPercent);
			});

			Assert.That(logs.Count(l => l.StartsWith("Slowing the game", StringComparison.Ordinal)), Is.LessThanOrEqualTo(4), "A steadily slow computer should cause few slowdowns.");
			Assert.That(speeds.Min(), Is.GreaterThanOrEqualTo(50));
			Assert.That(speeds.Max(), Is.LessThanOrEqualTo(70), "After settling the speed should stay close to the computer's capacity.");
			Assert.That(logs.Any(l => l.Contains("will not be sped up again for")), Is.True, "A failed probe should be held.");
		}

		[Test]
		public void RecoveryAcceleratesWhenTheLoadPasses()
		{
			// P1 manages 45% for 20s (a big battle), then is fast again. The game should be back to full speed well
			// within a minute of the load passing, not creep up a point at a time.
			long backAt = -1;
			var (logs, _, _) = RunWithCapacities(new[] { 2.0, 0.45 }, 100000, 0, (t, cs, sched) =>
			{
				if (t == 25000)
					cs[1].Capacity = 2.0;
				if (t > 25000 && backAt < 0 && sched.SpeedPercent == 100)
					backAt = t;
			});

			Assert.That(logs.Any(l => l.StartsWith("Slowing the game", StringComparison.Ordinal)), Is.True, "Precondition: the game was slowed.");
			Assert.That(backAt, Is.GreaterThan(0));
			Assert.That(backAt - 25000, Is.LessThanOrEqualTo(50000), "The game should be back to full speed within 50s of the load passing.");
		}

		[Test]
		public void FloorLeavesBehindAndTurboBringsBack()
		{
			// With a 50% floor, P1 at 35% is left behind and the others play at full speed; when P1's computer
			// recovers it catches up at turbo speed and is back in the game.
			var othersSpeedWhileBehind = new List<int>();
			var (logs, _, clients) = RunWithCapacities(new[] { 2.0, 0.35 }, 90000, 50, (t, cs, sched) =>
			{
				if (t == 40000)
					cs[1].Capacity = 3.0;
				if (t > 20000 && t < 40000)
					othersSpeedWhileBehind.Add(sched.SpeedPercent);
			});

			Assert.That(logs.Any(l => l.Contains("P1's computer is too slow")), Is.True, "P1 should be left behind by the floor.");
			Assert.That(othersSpeedWhileBehind, Is.Not.Empty.And.All.EqualTo(100), "The game should run at full speed for the others meanwhile.");
			Assert.That(clients[1].MinScale, Is.LessThan(0.3f), "P1 should be asked for turbo speed.");
			Assert.That(clients[0].NextFrame - clients[1].NextFrame, Is.LessThan(10), "P1 should catch up once its computer recovered.");
			Assert.That(logs.Any(l => l.Contains("P1 has caught up")), Is.True);
		}

		[Test]
		public void AnnouncerIsNotChatty()
		{
			var a = new GameSpeedAnnouncer(false, "Type !kickslow to vote.", i => $"P{i}");
			var messages = new List<(long T, string M)>();
			void Step(long t, int speed, int? slowest = null)
			{
				var m = a.Tick(t, speed, slowest, 0, -1, 10);
				if (m != null)
					messages.Add((t, m.Value.Message));
			}

			for (long t = 0; t < 60000; t += 1000)
				Step(t, 100);
			Assert.That(messages, Is.Empty, "Nothing should be said while the game runs at full speed.");

			Step(60000, 85, 1);
			Assert.That(messages, Has.Count.EqualTo(1), "The first slowdown should be announced immediately.");
			Assert.That(messages[0].M, Does.StartWith("Slowing the game to 85%").And.Not.Contain("P1").And.Contain("!kickslow"));

			var drift = new[] { 84, 82, 80, 79, 78, 78, 80, 82, 82, 81, 80, 79, 78, 78, 78, 79, 80, 81, 82, 82, 82, 82, 82, 82 };
			for (var i = 0; i < drift.Length; i++)
				Step(61000 + i * 1000, drift[i], 1);
			Assert.That(messages, Has.Count.EqualTo(1), "Small drifts should not be announced.");

			Step(90000, 70, 1);
			Assert.That(messages, Has.Count.EqualTo(2), "A large further slowdown should be announced once the interval has passed.");
			Assert.That(messages[1].M, Does.StartWith("Slowing the game to 70%"));

			var before = messages.Count;
			for (long t = 91000; t < 151000; t += 1000)
				Step(t, (t / 5000) % 2 == 0 ? 100 : 90, (t / 5000) % 2 == 0 ? null : 1);
			Assert.That(messages.Count - before, Is.LessThanOrEqualTo(2), "Flapping should produce at most one message per 30s.");
			Assert.That(messages.Skip(before).Any(m => m.M.Contains("full speed")), Is.False, "Brief recoveries must not be announced as full speed.");

			var lastBefore = messages.Count;
			for (long t = 151000; t < 300000; t += 1000)
				Step(t, 100);
			Assert.That(messages.Count - lastBefore, Is.EqualTo(1), "Sustained full speed should be announced exactly once.");
			Assert.That(messages[^1].M, Is.EqualTo("The game is back to full speed."));

			var kick = new GameSpeedAnnouncer(false, null, i => $"P{i}");
			kick.Tick(0, 80, 1, 0, -1, 10);
			Assert.That(kick.Tick(5000, 80, 1, 0, -1, 10), Is.Null, "Precondition: within the rate limit nothing is said.");
			kick.SlowestPlayerGone();
			var afterKick = kick.Tick(6000, 100, null, 0, -1, 10);
			Assert.That(afterKick, Is.Not.Null, "Full speed should be announced immediately after the slowest player is kicked.");
			Assert.That(afterKick.Value.Message, Is.EqualTo("The game is back to full speed."));
			Assert.That(kick.Tick(7000, 100, null, 0, -1, 10), Is.Null, "...and only once.");

			var named = new GameSpeedAnnouncer(true, null, i => $"P{i}");
			var m1 = named.Tick(0, 80, 1, 0, -1, 10);
			Assert.That(m1, Is.Not.Null);
			Assert.That(m1.Value.Message, Does.Contain("P1's computer"));
			Assert.That(m1.Value.PrivateClient, Is.EqualTo(1));
			Assert.That(m1.Value.PrivateMessage, Does.Contain("your computer"));

			var floor = new GameSpeedAnnouncer(false, null, i => $"P{i}");
			var m2 = floor.Tick(0, 100, null, 1, 2, 75);
			Assert.That(m2, Is.Not.Null);
			Assert.That(m2.Value.Message, Does.Contain("75%").And.Not.Contain("P2"));
			Assert.That(m2.Value.PrivateClient, Is.EqualTo(2));
		}

		[Test]
		public void AnnouncerSkipsPartialRecoveries()
		{
			var a = new GameSpeedAnnouncer(false, null, i => $"P{i}");
			Assert.That(a.Tick(0, 100, null, 0, -1, 10), Is.Null);
			Assert.That(a.Tick(1000, 60, 1, 0, -1, 10)?.Message, Does.StartWith("Slowing the game to 60%"));
			Assert.That(a.Tick(40000, 90, 1, 0, -1, 10), Is.Null, "A partial recovery should not be announced.");
			Assert.That(a.Tick(80000, 78, 1, 0, -1, 10)?.Message, Does.StartWith("Slowing the game to 78%"), "A slowdown from the quiet peak should be announced.");
			Assert.That(a.Tick(120000, 100, null, 0, -1, 10), Is.Null, "Full speed should wait for the settle time.");
			Assert.That(a.Tick(131000, 100, null, 0, -1, 10)?.Message, Is.EqualTo("The game is back to full speed."));
			a.ContinuedWithout(2);
			Assert.That(a.Tick(132000, 100, null, 0, -1, 10)?.Message, Does.StartWith("P2 has stopped responding; the game continues without them"), "Continuing without a stopped player should be announced at once.");
			Assert.That(a.Tick(133000, 100, null, 0, -1, 10), Is.Null);
		}

		[Test]
		public void TickScalesApplyToValidTickLengths()
		{
			const int Timestep = 40;
			var s = new FrameScheduler(Timestep, 3, 1, new[] { 0, 1 });

			for (long t = 0; t <= 3000; t += 40)
			{
				foreach (var (frame, _) in Drain(s, t))
				{
					s.ReceivePacket(0, frame, Order, t);
					s.ReceivePacket(1, frame, Order, t);
				}

				foreach (var (_, scale) in s.GetTickScales(t))
				{
					Assert.That((int)(scale * Timestep), Is.GreaterThanOrEqualTo(1), "Applied tick length must stay >= 1ms.");
					Assert.That(scale, Is.InRange(0.5f, 2.0f));
				}
			}
		}
	}
}
