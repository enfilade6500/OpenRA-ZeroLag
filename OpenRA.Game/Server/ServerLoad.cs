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
using System.Globalization;

namespace OpenRA.Server
{
	/// <summary>
	/// Whether the server itself is keeping up. Everything else the server measures is about the players; this is
	/// the one measurement that tells a starved host from a slow player, and it is what a shared-core cloud VM
	/// that has run out of CPU credit looks like from the inside: frames close late against the scheduler's own
	/// clock, scheduled time is lost when the loop runs more than a period late (the scheduler then restarts from
	/// now, and the game runs that much slower in real time without any pacing having been decided), and
	/// incoming packets wait in the event queue before the loop gets to them.
	///
	/// Measured over windows of WindowMs; the last completed window is what !speed and the pace log report, and
	/// the game's totals go into the end-of-game summary. The server log gets a line, at most once per
	/// LogIntervalMs, for every window in which the server was not keeping up.
	/// </summary>
	public sealed class ServerLoad
	{
		/// <summary>Length of a measurement window (ms).</summary>
		public const int WindowMs = 10000;

		/// <summary>At most one "not keeping up" log line this often (ms).</summary>
		public const int LogIntervalMs = 30000;

		/// <summary>A window delivering less than this fraction of its scheduled frames is not keeping up.</summary>
		public const float SpeedWarnBelow = 0.95f;

		/// <summary>A window whose frames closed later than this on average (ms) is not keeping up.</summary>
		public const int LateWarnMs = 50;

		/// <summary>A window whose incoming packets waited longer than this on average (ms) is not keeping up.</summary>
		public const int QueueWarnMs = 100;

		// The window being measured
		long windowStart = -1;
		int closes;
		long lateSum, lateMax, lostMs;
		int waits;
		long waitSum, waitMax;

		// The last completed window
		public bool HasWindow { get; private set; }
		public float Speed { get; private set; } = 1f;
		public float LateMean { get; private set; }
		public long LateMax { get; private set; }
		public float QueueMean { get; private set; }
		public long QueueMax { get; private set; }

		// Whether each of the last RecentWindows windows kept up, and the worst recent one, so that a player who asks
		// during a brief good spell in a bad stretch is still told
		public const int RecentWindows = 3;
		readonly bool[] recentOk = { true, true, true };
		int recentIndex;
		float worstRecentSpeed = 1f, worstRecentLate, worstRecentQueue;

		// The game so far
		long totalElapsed, totalLost;
		int totalCloses;
		long totalLateSum, totalLateMax;
		int totalWaits;
		long totalWaitSum, totalWaitMax;
		int badWindows, windows;
		long lastLogTime = -1;

		/// <summary>The last completed window was within every limit.</summary>
		public bool LastWindowOk => !HasWindow || (Speed >= SpeedWarnBelow && LateMean <= LateWarnMs && QueueMean <= QueueWarnMs);

		/// <summary>Every one of the last RecentWindows windows was within every limit.</summary>
		public bool KeepingUp => recentOk[0] && recentOk[1] && recentOk[2];

		/// <summary>A frame closed at now that was scheduled for scheduledClose; period is the current frame period.</summary>
		public void NoteClose(long now, double scheduledClose, double period)
		{
			if (windowStart < 0)
				windowStart = now;

			var late = (long)Math.Max(0, now - scheduledClose);
			closes++;
			lateSum += late;
			if (late > lateMax)
				lateMax = late;

			// The scheduler restarts from now when it is more than a period late (FrameScheduler.TryCloseFrame): that much
			// scheduled time is lost, and the game runs correspondingly slower in real time
			if (late > period)
				lostMs += late;
		}

		/// <summary>An event waited this long (ms) in the queue before the server loop took it.</summary>
		public void NoteEventWait(long ms)
		{
			if (windowStart < 0)
				return;

			waits++;
			waitSum += ms;
			if (ms > waitMax)
				waitMax = ms;
		}

		/// <summary>
		/// Closes the window when it is over. Returns a log line when the window was not kept up with and the rate
		/// limit allows one, otherwise null.
		/// </summary>
		public string Update(long now)
		{
			if (windowStart < 0)
				return null;

			var elapsed = now - windowStart;
			if (elapsed < WindowMs)
				return null;

			Speed = Math.Clamp(1f - lostMs / (float)elapsed, 0f, 1f);
			LateMean = closes > 0 ? lateSum / (float)closes : 0f;
			LateMax = lateMax;
			QueueMean = waits > 0 ? waitSum / (float)waits : 0f;
			QueueMax = waitMax;
			HasWindow = true;

			totalElapsed += elapsed;
			totalLost += lostMs;
			totalCloses += closes;
			totalLateSum += lateSum;
			totalLateMax = Math.Max(totalLateMax, lateMax);
			totalWaits += waits;
			totalWaitSum += waitSum;
			totalWaitMax = Math.Max(totalWaitMax, waitMax);
			windows++;

			windowStart = now;
			closes = 0;
			lateSum = lateMax = lostMs = 0;
			waits = 0;
			waitSum = waitMax = 0;

			var ok = LastWindowOk;
			if (KeepingUp)
			{
				// The first bad window after good ones is the one to describe
				worstRecentSpeed = Speed;
				worstRecentLate = LateMean;
				worstRecentQueue = QueueMean;
			}
			else if (!ok && (Speed < worstRecentSpeed || (Speed == worstRecentSpeed && LateMean + QueueMean > worstRecentLate + worstRecentQueue)))
			{
				worstRecentSpeed = Speed;
				worstRecentLate = LateMean;
				worstRecentQueue = QueueMean;
			}

			recentOk[recentIndex] = ok;
			recentIndex = (recentIndex + 1) % RecentWindows;

			if (ok)
				return null;

			badWindows++;
			if (lastLogTime >= 0 && now - lastLogTime < LogIntervalMs)
				return null;

			lastLogTime = now;
			return "Server not keeping up: " + DescribeWindow() + " The host is overloaded, or not scheduling the server on time.";
		}

		/// <summary>The last window, for the server log.</summary>
		public string DescribeWindow()
		{
			var ic = CultureInfo.InvariantCulture;
			return string.Format(ic, "delivered {0:F0}% of frames in the last {1}s; frames closed {2:F0} ms late on average (max {3} ms); " +
				"incoming packets waited {4:F0} ms on average (max {5} ms).",
				Speed * 100, WindowMs / 1000, LateMean, LateMax, QueueMean, QueueMax);
		}

		/// <summary>What a player is told by !speed when the server is not keeping up: the worst of the recent windows.</summary>
		public string DescribeForPlayers()
		{
			var ic = CultureInfo.InvariantCulture;
			if (worstRecentSpeed < SpeedWarnBelow)
				return string.Format(ic, "The server itself has been delivering frames at {0:F0}% of real time: its host is not keeping up.", worstRecentSpeed * 100);

			return string.Format(ic, "The server itself has been running late (frames {0:F0} ms late, packets waiting {1:F0} ms): its host is not keeping up.",
				worstRecentLate, worstRecentQueue);
		}

		/// <summary>The game, for the end-of-game summary. Null if no window completed.</summary>
		public string Summary()
		{
			if (windows == 0)
				return null;

			var ic = CultureInfo.InvariantCulture;
			var speed = totalElapsed > 0 ? 1f - totalLost / (float)totalElapsed : 1f;
			var late = totalCloses > 0 ? totalLateSum / (float)totalCloses : 0f;
			var wait = totalWaits > 0 ? totalWaitSum / (float)totalWaits : 0f;
			var verdict = badWindows == 0 ? "The server kept up throughout."
				: string.Format(ic, "The server was not keeping up in {0} of {1} {2}-second windows.", badWindows, windows, WindowMs / 1000);
			return string.Format(ic, "Server summary: delivered {0:F1}% of frames in real time; frames closed {1:F0} ms late on average (max {2} ms); " +
				"incoming packets waited {3:F0} ms on average (max {4} ms). {5}",
				speed * 100, late, totalLateMax, wait, totalWaitMax, verdict);
		}
	}
}
