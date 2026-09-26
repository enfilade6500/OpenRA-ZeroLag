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
