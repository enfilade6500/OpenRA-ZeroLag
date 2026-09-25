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
using System.Diagnostics;
using System.Linq;

namespace OpenRA.Server
{
	/// <summary>
	/// Keeps the clients' simulations in phase by sending each of them a TickScale.
	///
	/// In lockstep every client needs every other client's orders for a frame before it can simulate it,
	/// so the game runs smoothly only when every client's orders for a frame reach the server at about
	/// the same time. When one client falls behind (a CPU hitch, a slow link), the others run into its
	/// missing orders and freeze until they arrive.
	///
	/// Each interval this measures, per frame, how much later each client's orders arrive than the
	/// earliest client's orders for the same frame, and corrects part of the difference over the next
	/// interval (correcting all of it at once overshoots, because the measurement lags the correction):
	///  * A late client that still has other players' frames buffered (it reports its queue depth in
	///    ping replies) is told to run faster, so it catches up without anyone else noticing.
	///  * A late client that is starved of data, or that was asked to speed up but could not
	///    (a CPU-bound machine), sets the pace instead: clients ahead of it are slowed down smoothly,
	///    which is much less disruptive than letting them repeatedly freeze.
	///
	/// Release clients accept any TickScale value; only this server-side policy decides what to send.
	/// </summary>
	public class OrderBuffer
	{
		// Corrections are recalculated this often (ms)
		const int Interval = 1000;

		// Need at least this many complete frames of arrival data in an interval to make a correction
		const int MinSamples = 3;

		// Only the most recent frames are used, so the estimate reflects where the clients are now
		// rather than where they were while the previous correction was still being applied
		const int RecentSamples = 5;

		// Correct only part of the measured difference each interval: the measurement lags behind the
		// correction, so correcting all of it makes the clients overshoot and oscillate
		const float Gain = 0.6f;

		// Ignore phase differences smaller than this (ms): they are within measurement noise.
		// Note that clients apply TickScale in whole milliseconds per tick ((int)(scale * timestep)),
		// so small corrections round to no change, especially at fast game speeds.
		const int Deadband = 25;

		// A client may be told to run up to 1 / 0.75 = 33% faster to catch up
		const float MinTickScale = 0.75f;

		// Clients may be slowed down by up to 20% to wait for a player that cannot keep up
		const float MaxTickScale = 1.2f;

		// A client reporting at least this many queued frames has buffered orders to run through
		const int SpeedUpMinQueue = 2;

		// If speed-up requests repeatedly recover less than this fraction of the requested amount, the
		// client is assumed to be unable to run faster (e.g. CPU-bound) and is waited for instead.
		// It is retried after a backoff that doubles each time it fails again.
		const float SpeedUpMinEffect = 0.3f;
		const int SpeedUpFailuresBeforeBackoff = 2;
		const int SpeedUpBackoffIntervals = 5;
		const int MaxSpeedUpBackoffIntervals = 40;

		// Drop incomplete frame records that are further behind than this
		const int MaxTrackedFrames = 200;

		sealed class PlayerState
		{
			public readonly List<long> Lateness = new();
			public int QueueLength = -1;
			public int LastTickMs;
			public long LastLateness;
			public bool HasLastLateness;
			public int SpeedUpFailures;
			public int SpeedUpBackoff;
			public int NextSpeedUpBackoff = SpeedUpBackoffIntervals;
		}

		readonly object syncRoot = new();
		readonly Dictionary<int, PlayerState> players = new();
		readonly Dictionary<int, Dictionary<int, long>> arrivals = new();

		Stopwatch gameTimer;
		long nextUpdate;
		int timestep;
		int ticksPerInterval;
		int minTickMs;
		int maxTickMs;
		int latestFrame;

		public void Start(GameSpeed gameSpeed, IEnumerable<int> playerIndices)
		{
			lock (syncRoot)
			{
				timestep = gameSpeed.Timestep;
				ticksPerInterval = Interval / timestep;
				minTickMs = Math.Max(1, (int)Math.Ceiling(MinTickScale * timestep));
				maxTickMs = Math.Max(timestep, (int)Math.Floor(MaxTickScale * timestep));

				foreach (var p in playerIndices)
					players.TryAdd(p, new PlayerState { LastTickMs = timestep });

				gameTimer = Stopwatch.StartNew();
				nextUpdate = gameTimer.ElapsedMilliseconds + Interval;
			}
		}

		/// <summary>Record the arrival of a player's orders for a given (server-scheduled) frame.</summary>
		public void AddOrderTimestamp(int playerIndex, int frame)
		{
			lock (syncRoot)
			{
				if (gameTimer == null || !players.ContainsKey(playerIndex))
					return;

				if (!arrivals.TryGetValue(frame, out var frameArrivals))
					arrivals[frame] = frameArrivals = new Dictionary<int, long>();

				frameArrivals[playerIndex] = gameTimer.ElapsedMilliseconds;
				latestFrame = Math.Max(latestFrame, frame);

				if (frameArrivals.Count >= players.Count && players.Keys.All(frameArrivals.ContainsKey))
				{
					var earliest = frameArrivals.Values.Min();
					foreach (var (p, state) in players)
						state.Lateness.Add(frameArrivals[p] - earliest);

					arrivals.Remove(frame);
				}

				if (arrivals.Count > MaxTrackedFrames)
					foreach (var stale in arrivals.Keys.Where(f => f < latestFrame - MaxTrackedFrames).ToList())
						arrivals.Remove(stale);
			}
		}

		/// <summary>Queue depth reported by the client in its ping replies (OrderManager.OrderQueueLength).</summary>
		public void ReceiveQueueLength(int playerIndex, int queueLength)
		{
			lock (syncRoot)
			{
				if (players.TryGetValue(playerIndex, out var state))
					state.QueueLength = queueLength;
			}
		}

		public IEnumerable<(int PlayerIndex, float TickScale)> GetTickScales()
		{
			lock (syncRoot)
			{
				if (gameTimer == null || players.Count == 0)
					return Array.Empty<(int, float)>();

				var now = gameTimer.ElapsedMilliseconds;
				if (now < nextUpdate)
					return Array.Empty<(int, float)>();

				nextUpdate = now + Interval;

				// Keep accumulating until there is enough fresh data.
				// Clients keep applying the last TickScale they were sent, so cancel any correction in
				// progress rather than letting it run for another interval.
				if (players.Values.Any(s => s.Lateness.Count < MinSamples))
				{
					var reset = new List<(int, float)>();
					foreach (var (p, state) in players)
					{
						if (state.LastTickMs != timestep)
						{
							state.LastTickMs = timestep;
							reset.Add((p, 1f));
						}
					}

					return reset;
				}

				// Fresh measurements only: samples taken before the last correction took effect would
				// make the controller overshoot and oscillate
				var lateness = players.ToDictionary(p => p.Key, p => Median(p.Value.Lateness.Skip(Math.Max(0, p.Value.Lateness.Count - RecentSamples))));
				foreach (var s in players.Values)
					s.Lateness.Clear();

				// Detect clients that were asked to speed up but could not
				foreach (var (p, state) in players)
				{
					if (state.SpeedUpBackoff > 0)
						state.SpeedUpBackoff--;

					if (state.LastTickMs < timestep && state.HasLastLateness)
					{
						var requested = (timestep - state.LastTickMs) * ticksPerInterval;
						var recovered = state.LastLateness - lateness[p];
						if (recovered >= SpeedUpMinEffect * requested)
						{
							state.SpeedUpFailures = 0;
							state.NextSpeedUpBackoff = SpeedUpBackoffIntervals;
						}
						else if (++state.SpeedUpFailures >= SpeedUpFailuresBeforeBackoff)
						{
							state.SpeedUpFailures = 0;
							state.SpeedUpBackoff = state.NextSpeedUpBackoff;
							state.NextSpeedUpBackoff = Math.Min(2 * state.NextSpeedUpBackoff, MaxSpeedUpBackoffIntervals);
						}
					}

					state.LastLateness = lateness[p];
					state.HasLastLateness = true;
				}

				bool CanSpeedUp(int p)
				{
					var state = players[p];
					return state.QueueLength >= SpeedUpMinQueue && state.SpeedUpBackoff == 0;
				}

				// Everyone converges on the latest client that cannot catch up by itself
				// (or on the earliest client, if every late client can catch up)
				var target = players.Keys.Where(p => !CanSpeedUp(p)).Select(p => lateness[p]).DefaultIfEmpty(0).Max();

				var result = new List<(int, float)>(players.Count);
				foreach (var (p, state) in players)
				{
					// Positive: the client is ahead of the target and must slow down. Negative: it is behind and should speed up.
					var diff = target - lateness[p];
					var tickMs = timestep;
					if (Math.Abs(diff) >= Deadband && (diff > 0 || CanSpeedUp(p)))
					{
						// Spread the correction over the ticks of the next interval, in the whole
						// milliseconds per tick that the client will actually apply
						var raw = timestep + Gain * diff / ticksPerInterval;
						tickMs = ((int)Math.Round(raw)).Clamp(minTickMs, maxTickMs);
					}

					state.LastTickMs = tickMs;

					// Clients compute (int)(scale * timestep); the half millisecond keeps float rounding
					// from landing just below the intended value
					result.Add((p, tickMs == timestep ? 1f : (tickMs + 0.5f) / timestep));
				}

				return result;
			}
		}

		static long Median(IEnumerable<long> values)
		{
			var a = values.ToArray();
			Array.Sort(a);
			var n = a.Length;

			if (n % 2 != 0)
				return a[n / 2];

			return (a[(n - 1) / 2] + a[n / 2]) / 2;
		}

		public void RemovePlayer(int player)
		{
			lock (syncRoot)
			{
				players.Remove(player);

				// Frames still waiting for this player's orders would never complete; start afresh
				arrivals.Clear();
			}
		}
	}
}
