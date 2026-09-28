using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace NetHarness
{
	/// <summary>One direction of a simulated network path.</summary>
	public sealed class LinkSpec
	{
		/// <summary>Fixed one-way delay in ms.</summary>
		public double BaseMs = 10;

		/// <summary>Extra uniformly distributed delay in [0, JitterMs] per chunk (in-order delivery is preserved, like TCP).</summary>
		public double JitterMs = 2;

		/// <summary>Average number of "freeze" events per second (Poisson). Models TCP retransmits after loss, Wi-Fi hiccups, bufferbloat bursts.</summary>
		public double SpikeRatePerSec = 0;

		/// <summary>How long each freeze holds back all traffic on this direction.</summary>
		public double SpikeMs = 0;

		/// <summary>
		/// Chance (percent) that a freeze doubles in length, applied repeatedly (up to 8x): a lost TCP retransmission
		/// doubles the retransmission timeout, so real holes are mostly RTO-sized with a tail of 2x and 4x.
		/// </summary>
		public double SpikeDoublePct = 0;

		/// <summary>Bursty loss: every BurstEverySec seconds of game time, for BurstForSec seconds, freezes happen at BurstSpikeRatePerSec instead.</summary>
		public double BurstEverySec = 0;
		public double BurstForSec = 0;
		public double BurstSpikeRatePerSec = 0;

		/// <summary>Complete outage: nothing passes from DeadAtSec (game time) for DeadForSec seconds.</summary>
		public double DeadAtSec = double.NaN;
		public double DeadForSec = 0;

		public LinkSpec Clone() => (LinkSpec)MemberwiseClone();

		public override string ToString() =>
			$"{BaseMs}ms+{JitterMs}j" + (SpikeRatePerSec > 0 ? $" spikes {SpikeMs}ms@{SpikeRatePerSec}/s" : "")
			+ (SpikeDoublePct > 0 ? $" x2@{SpikeDoublePct}%" : "")
			+ (BurstEverySec > 0 ? $" bursts {BurstSpikeRatePerSec}/s for {BurstForSec}s every {BurstEverySec}s" : "")
			+ (DeadForSec > 0 ? $" dead {DeadForSec}s@{DeadAtSec}s" : "");
	}

	/// <summary>
	/// Userspace TCP proxy that sits between one fake client and the server and applies
	/// independent delay/jitter/freeze models to each direction. Byte order is always preserved.
	/// </summary>
	public sealed class DelayProxy : IDisposable
	{
		readonly TcpListener listener;
		readonly IPEndPoint server;
		readonly LinkSpec up, down;
		readonly int seed;
		Socket clientSide, serverSide;
		Pipe upPipe, downPipe;
		volatile bool disposed;

		public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

		/// <summary>What the link actually did: freezes injected in each direction (count, total seconds).</summary>
		public string Injected =>
			$"up {upPipe?.Freezes ?? 0} freezes/{(upPipe?.FrozenMs ?? 0) / 1000:F1}s, down {downPipe?.Freezes ?? 0} freezes/{(downPipe?.FrozenMs ?? 0) / 1000:F1}s";

		public DelayProxy(IPEndPoint server, LinkSpec up, LinkSpec down, int seed)
		{
			this.server = server;
			this.up = up;
			this.down = down;
			this.seed = seed;
			listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			new Thread(Accept) { IsBackground = true, Name = "proxy-accept" }.Start();
		}

		void Accept()
		{
			try
			{
				clientSide = listener.AcceptSocket();
				clientSide.NoDelay = true;
				serverSide = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
				serverSide.Connect(server);
				upPipe = new Pipe(clientSide, serverSide, up, seed * 2 + 1, this);
				downPipe = new Pipe(serverSide, clientSide, down, seed * 2 + 2, this);
				upPipe.Start("up");
				downPipe.Start("down");
			}
			catch (Exception) when (disposed) { }
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			try { listener.Stop(); } catch { }
			try { clientSide?.Dispose(); } catch { }
			try { serverSide?.Dispose(); } catch { }
		}

		sealed class Pipe
		{
			readonly Socket from, to;
			readonly LinkSpec spec;
			readonly Random rng;
			readonly DelayProxy owner;
			readonly BlockingCollection<(double Due, byte[] Data)> queue = new();
			double lastDue;
			double spikeStart = double.NaN, spikeEnd = double.NaN;
			double burstSpikeStart = double.NaN, burstSpikeEnd = double.NaN;
			public int Freezes;
			public double FrozenMs;

			public Pipe(Socket from, Socket to, LinkSpec spec, int seed, DelayProxy owner)
			{
				this.from = from;
				this.to = to;
				this.spec = spec;
				this.owner = owner;
				rng = new Random(seed);
				spikeStart = ScheduleSpike(Clock.Now, spec.SpikeRatePerSec, out spikeEnd);
			}

			public void Start(string name)
			{
				new Thread(Read) { IsBackground = true, Name = "proxy-read-" + name }.Start();
				new Thread(Write) { IsBackground = true, Name = "proxy-write-" + name }.Start();
			}

			// A freeze is a lost segment: nothing gets through until TCP retransmits it after the timeout,
			// and a lost retransmission doubles the timeout
			double FreezeLength()
			{
				var len = spec.SpikeMs;
				for (var k = 0; k < 3 && spec.SpikeDoublePct > 0 && rng.NextDouble() * 100 < spec.SpikeDoublePct; k++)
					len *= 2;

				return len;
			}

			double ScheduleSpike(double after, double ratePerSec, out double end)
			{
				end = double.NaN;
				if (ratePerSec <= 0 || spec.SpikeMs <= 0)
					return double.NaN;

				var wait = -Math.Log(1 - rng.NextDouble()) / ratePerSec * 1000.0;
				var start = after + wait;
				end = start + FreezeLength();
				return start;
			}

			bool InBurst(double now)
			{
				if (spec.BurstEverySec <= 0 || double.IsNaN(Clock.GameStart))
					return false;

				var t = (now - Clock.GameStart) / 1000.0;
				return t >= 0 && t % spec.BurstEverySec >= spec.BurstEverySec - spec.BurstForSec;
			}

			double DueTime(double now)
			{
				var due = now + spec.BaseMs + rng.NextDouble() * spec.JitterMs;
				var heldUntil = double.NaN;

				if (!double.IsNaN(spikeStart))
				{
					// Advance past freeze windows that ended before this chunk arrived
					while (now >= spikeEnd)
						spikeStart = ScheduleSpike(spikeEnd, spec.SpikeRatePerSec, out spikeEnd);

					if (now >= spikeStart)
						heldUntil = spikeEnd;
				}

				// The burst stream only runs (and is only scheduled) inside burst windows, so a burst starts with a fresh draw
				if (InBurst(now))
				{
					if (double.IsNaN(burstSpikeStart) || burstSpikeStart < now - spec.BurstForSec * 1000)
						burstSpikeStart = ScheduleSpike(now, spec.BurstSpikeRatePerSec, out burstSpikeEnd);

					while (now >= burstSpikeEnd)
						burstSpikeStart = ScheduleSpike(burstSpikeEnd, spec.BurstSpikeRatePerSec, out burstSpikeEnd);

					if (now >= burstSpikeStart)
						heldUntil = double.IsNaN(heldUntil) ? burstSpikeEnd : Math.Max(heldUntil, burstSpikeEnd);
				}
				else
					burstSpikeStart = double.NaN;

				// A complete outage at a fixed point in the game
				if (spec.DeadForSec > 0 && !double.IsNaN(Clock.GameStart))
				{
					var t = (now - Clock.GameStart) / 1000.0;
					if (t >= spec.DeadAtSec && t < spec.DeadAtSec + spec.DeadForSec)
					{
						var deadEnd = Clock.GameStart + (spec.DeadAtSec + spec.DeadForSec) * 1000.0;
						heldUntil = double.IsNaN(heldUntil) ? deadEnd : Math.Max(heldUntil, deadEnd);
					}
				}

				if (!double.IsNaN(heldUntil))
				{
					if (heldUntil + spec.BaseMs > lastDue)
					{
						Freezes++;
						FrozenMs += heldUntil - now;
					}

					due = Math.Max(due, heldUntil + spec.BaseMs);
				}

				// TCP never reorders
				due = Math.Max(due, lastDue);
				lastDue = due;
				return due;
			}

			void Read()
			{
				var buf = new byte[65536];
				try
				{
					while (!owner.disposed)
					{
						var n = from.Receive(buf);
						if (n <= 0)
							break;

						var data = new byte[n];
						Buffer.BlockCopy(buf, 0, data, 0, n);
						queue.Add((DueTime(Clock.Now), data));
					}
				}
				catch (Exception) { }
				finally
				{
					queue.CompleteAdding();
				}
			}

			void Write()
			{
				try
				{
					foreach (var (due, data) in queue.GetConsumingEnumerable())
					{
						Clock.SleepUntil(due);
						var sent = 0;
						while (sent < data.Length)
							sent += to.Send(data, sent, data.Length - sent, SocketFlags.None);
					}

					to.Shutdown(SocketShutdown.Send);
				}
				catch (Exception) { }
			}
		}
	}
}
