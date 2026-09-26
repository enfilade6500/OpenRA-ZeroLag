using System.Diagnostics;
using System.Threading;

namespace NetHarness
{
	/// <summary>Shared process-wide clock so latencies can be measured across fake clients.</summary>
	public static class Clock
	{
		static readonly Stopwatch Watch = Stopwatch.StartNew();

		public static double Now => Watch.Elapsed.TotalMilliseconds;

		/// <summary>Integer ms clock, equivalent to Game.RunTime in the real client.</summary>
		public static long RunTime => Watch.ElapsedMilliseconds;

		public static void SleepUntil(double due)
		{
			double rem;
			while ((rem = due - Now) > 0)
				Thread.Sleep(rem >= 1 ? (int)rem : 1);
		}
	}
}
