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

		public LinkSpec Clone() => (LinkSpec)MemberwiseClone();

		public override string ToString() =>
			$"{BaseMs}ms+{JitterMs}j" + (SpikeRatePerSec > 0 ? $" spikes {SpikeMs}ms@{SpikeRatePerSec}/s" : "");
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
		volatile bool disposed;

		public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

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
				new Pipe(clientSide, serverSide, up, seed * 2 + 1, this).Start("up");
				new Pipe(serverSide, clientSide, down, seed * 2 + 2, this).Start("down");
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

			public Pipe(Socket from, Socket to, LinkSpec spec, int seed, DelayProxy owner)
			{
				this.from = from;
				this.to = to;
				this.spec = spec;
				this.owner = owner;
				rng = new Random(seed);
				ScheduleNextSpike(Clock.Now);
			}

			public void Start(string name)
			{
				new Thread(Read) { IsBackground = true, Name = "proxy-read-" + name }.Start();
				new Thread(Write) { IsBackground = true, Name = "proxy-write-" + name }.Start();
			}

			void ScheduleNextSpike(double after)
			{
				if (spec.SpikeRatePerSec <= 0 || spec.SpikeMs <= 0)
					return;

				var wait = -Math.Log(1 - rng.NextDouble()) / spec.SpikeRatePerSec * 1000.0;
				spikeStart = after + wait;
				spikeEnd = spikeStart + spec.SpikeMs;
			}

			double DueTime(double now)
			{
				var due = now + spec.BaseMs + rng.NextDouble() * spec.JitterMs;

				if (!double.IsNaN(spikeStart))
				{
					// Advance past freeze windows that ended before this chunk arrived
					while (now >= spikeEnd)
						ScheduleNextSpike(spikeEnd);

					if (now >= spikeStart)
						due = Math.Max(due, spikeEnd + spec.BaseMs);
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
