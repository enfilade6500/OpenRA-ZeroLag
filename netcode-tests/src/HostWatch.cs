using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace NetHarness
{
	/// <summary>
	/// Watches the machine the harness runs on. A hundred clients in one process can stall as one (a full garbage
	/// collection over everything they have recorded, the OS paging, the CPU taken by something else), and from the
	/// clients' side that looks exactly like the server stalling: every client sees the same gap at the same moment.
	/// A thread that only sleeps tells the two apart: if it overslept by as much as the gap, this machine stalled.
	/// </summary>
	public sealed class HostWatch : IDisposable
	{
		const int SleepMs = 10;
		public const int StallThresholdMs = 100;

		readonly Thread thread;
		readonly object sync = new();
		readonly List<(double T, double Ms)> stalls = new();
		volatile bool stop;
		int fullGCs;
		double longestFullGCPauseMs;

		public HostWatch()
		{
			thread = new Thread(Watch) { IsBackground = true, Name = "host watch" };
			thread.Start();
		}

		void Watch()
		{
			var gen2 = GC.CollectionCount(2);
			var last = Clock.Now;
			while (!stop)
			{
				Thread.Sleep(SleepMs);
				var now = Clock.Now;
				var overslept = now - last - SleepMs;
				if (overslept >= StallThresholdMs)
					lock (sync)
						stalls.Add((last, overslept));

				last = now;

				var g = GC.CollectionCount(2);
				if (g != gen2)
				{
					gen2 = g;
					var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
					var pause = 0.0;
					foreach (var p in info.PauseDurations)
						pause += p.TotalMilliseconds;

					lock (sync)
					{
						fullGCs++;
						longestFullGCPauseMs = Math.Max(longestFullGCPauseMs, pause);
					}
				}
			}
		}

		/// <summary>The stalls seen between t0 and t1 (Clock.Now units).</summary>
		public List<(double T, double Ms)> StallsBetween(double t0, double t1)
		{
			lock (sync)
				return stalls.Where(s => s.T >= t0 && s.T <= t1).ToList();
		}

		/// <summary>One line for the run's checks: whether this machine stalled during the measured window, and how the GC did.</summary>
		public string Describe(double t0, double t1)
		{
			var inWindow = StallsBetween(t0, t1);
			int gcs;
			double gcPause;
			lock (sync)
			{
				gcs = fullGCs;
				gcPause = longestFullGCPauseMs;
			}

			var heapMb = GC.GetTotalMemory(false) / 1048576.0;
			var gc = string.Format(CultureInfo.InvariantCulture, "{0} full GCs, longest pause {1:F0} ms, managed heap {2:F0} MB at the end", gcs, gcPause, heapMb);
			if (inWindow.Count == 0)
				return $"harness host: no stalls over {StallThresholdMs} ms in the measured window ({gc})";

			var worst = inWindow.OrderByDescending(s => s.Ms).First();
			return string.Format(CultureInfo.InvariantCulture,
				"harness host: STALLED {0} time(s) for over {1} ms in the measured window, longest {2:F0} ms at {3:F1} s ({4}). " +
				"A gap of that length on every client at that moment is this machine, not the server.",
				inWindow.Count, StallThresholdMs, worst.Ms, (worst.T - t0) / 1000, gc);
		}

		public void Dispose()
		{
			stop = true;
		}
	}
}
