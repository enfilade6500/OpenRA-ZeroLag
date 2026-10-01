using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;

namespace NetHarness
{
	sealed class PlayerSpec
	{
		public string Name;
		public LinkSpec Up = new();
		public LinkSpec Down = new();
		public CpuSpec Cpu = new();

		/// <summary>Seconds after measurement starts at which this player quits (closes its connection).</summary>
		public double LeaveAfterSec = double.NaN;

		/// <summary>Joins the lobby but never readies up, so the server kicks it at game start.</summary>
		public bool NeverReady;

		/// <summary>Joins as a spectator (no slot).</summary>
		public bool Spectator;

		/// <summary>Expected to be kicked during the game (by the vote script).</summary>
		public bool ExpectKick;
	}

	static class Scenarios
	{
		static PlayerSpec Good(string name) => new()
		{
			Name = name,
			Up = new LinkSpec { BaseMs = 15, JitterMs = 5 },
			Down = new LinkSpec { BaseMs = 15, JitterMs = 5 },
		};

		public static readonly Dictionary<string, (string Description, Func<List<PlayerSpec>> Build)> All = new()
		{
			["clean"] = ("4 players, all ~30ms round trip", () => new() { Good("p1"), Good("p2"), Good("p3"), Good("p4") }),

			["slowlink"] = ("p4 has a long but steady path: ~300ms round trip, +/-30ms jitter", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 150, JitterMs = 30 };
				p4.Down = new LinkSpec { BaseMs = 150, JitterMs = 30 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["spikes"] = ("p4 on a lossy link: ~80ms round trip plus 300ms freezes about every 3s each way (TCP retransmits / Wi-Fi)", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 40, JitterMs = 10, SpikeRatePerSec = 0.33, SpikeMs = 300 };
				p4.Down = new LinkSpec { BaseMs = 40, JitterMs = 10, SpikeRatePerSec = 0.33, SpikeMs = 300 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["hitch"] = ("p4's PC stalls for 400ms about every 5s (good network)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 2, HitchRatePerSec = 0.2, HitchMs = 400 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["leave"] = ("p4 quits 20s into the game; a 5th client never readies and is kicked at start", () =>
			{
				var p4 = Good("p4");
				p4.LeaveAfterSec = 20;
				var lurker = Good("lurker");
				lurker.NeverReady = true;
				return new() { Good("p1"), Good("p2"), Good("p3"), p4, lurker };
			}),

			["chaos"] = ("every player has a different problem: slow link, lossy link, CPU hitches, 600 APM", () =>
			{
				var p1 = Good("p1");
				var p2 = Good("p2");
				p2.Up = new LinkSpec { BaseMs = 120, JitterMs = 40 };
				p2.Down = new LinkSpec { BaseMs = 90, JitterMs = 40 };
				var p3 = Good("p3");
				p3.Up = new LinkSpec { BaseMs = 30, JitterMs = 20, SpikeRatePerSec = 0.25, SpikeMs = 250 };
				p3.Down = new LinkSpec { BaseMs = 30, JitterMs = 20, SpikeRatePerSec = 0.25, SpikeMs = 250 };
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 8, HitchRatePerSec = 0.3, HitchMs = 300 };
				return new() { p1, p2, p3, p4 };
			}),

			["dropout"] = ("p4's connection drops out completely for 1.5s about every 20s (both directions)", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 20, JitterMs = 5, SpikeRatePerSec = 0.05, SpikeMs = 1500 };
				p4.Down = new LinkSpec { BaseMs = 20, JitterMs = 5, SpikeRatePerSec = 0.05, SpikeMs = 1500 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["downjitter"] = ("p4's download path is jittery: 60ms +/-120ms, upload clean", () =>
			{
				var p4 = Good("p4");
				p4.Down = new LinkSpec { BaseMs = 60, JitterMs = 120 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["battles"] = ("p4's PC slows to ~80% speed for 15s out of every 30s (big battles), otherwise fine", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 2, HeavyEverySec = 30, HeavyForSec = 15, HeavyTickMs = 50 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["slowspec"] = ("4 players on good connections, plus a spectator whose PC only manages ~70% speed", () =>
			{
				var spec = Good("spec");
				spec.Spectator = true;
				spec.Cpu = new CpuSpec { TickMs = 57 };
				return new() { Good("p1"), Good("p2"), Good("p3"), Good("p4"), spec };
			}),

			["defeated"] = ("p4's PC only manages ~90% speed, and p4 is defeated early (run with --defeatbit 5 --defeatframe 40 on Hypothermia)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 44 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["potato"] = ("p4's PC only manages ~55% speed for the whole game (the 'Sir Spunk' case)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 73 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["outage"] = ("p4's connection goes completely dead for 6s about every 40s", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 20, JitterMs = 5, SpikeRatePerSec = 0.025, SpikeMs = 6000 };
				p4.Down = new LinkSpec { BaseMs = 20, JitterMs = 5, SpikeRatePerSec = 0.025, SpikeMs = 6000 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["votekick"] = ("p4's PC only manages ~55% speed; once the game has been slowed, p1 asks !speed and p1-p3 vote !kickslow (run with --votekick 1 and Server.VoteKickSlowest=True)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 73 };
				p4.ExpectKick = true;
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["slowcpu"] = ("p4's PC needs 44ms per 40ms tick (can only manage ~90% speed)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 44 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			// Scenarios built from patterns measured in real games (see NETCODE.md, "v1.1")
			["holetrain"] = ("the 'Condemner Meryph' case: p4 far away (~180ms RTT) on a link that drops out in bursts: 400ms holes (some 2-8x longer) about 1.5 times a second for 10s out of every 50s, a few in between, download side; capable PC", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 90, JitterMs = 10, SpikeRatePerSec = 0.03, SpikeMs = 400, SpikeDoublePct = 25 };
				p4.Down = new LinkSpec { BaseMs = 90, JitterMs = 10, SpikeRatePerSec = 0.05, SpikeMs = 400, SpikeDoublePct = 30, BurstEverySec = 50, BurstForSec = 10, BurstSpikeRatePerSec = 1.5 };
				p4.Cpu = new CpuSpec { TickMs = 12 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["decline"] = ("the 'Ragnarok' case: p4's PC slides from ~110% to ~50% capacity over two minutes as the map fills up, then stays there (run 180s)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { Schedule = "0:36,20:44,140:80" };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["lossload"] = ("the 'BIG LOBBY' case: p4 loses a packet every ~8s all game (400ms holes, download side) and its PC drifts from 75% load to ~105% after two minutes (run 180s)", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 40, JitterMs = 10, SpikeRatePerSec = 0.05, SpikeMs = 400 };
				p4.Down = new LinkSpec { BaseMs = 40, JitterMs = 10, SpikeRatePerSec = 0.12, SpikeMs = 400, SpikeDoublePct = 15 };
				p4.Cpu = new CpuSpec { Schedule = "0:30,60:30,120:42" };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["outage8"] = ("the 'Hako' case: p4's connection is completely dead for 8s, 20s into the game, then fine", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 40, JitterMs = 10, DeadAtSec = 20, DeadForSec = 8 };
				p4.Down = new LinkSpec { BaseMs = 40, JitterMs = 10, DeadAtSec = 20, DeadForSec = 8 };
				p4.Cpu = new CpuSpec { TickMs = 12 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["deadlink"] = ("a dead connection: p4's link dies 20s into the game and never comes back (the server drops p4 after its 60s timeout); the others must not be frozen for long (run 90s)", () =>
			{
				var p4 = Good("p4");
				p4.Up = new LinkSpec { BaseMs = 20, JitterMs = 5, DeadAtSec = 20, DeadForSec = 600 };
				p4.Down = new LinkSpec { BaseMs = 20, JitterMs = 5, DeadAtSec = 20, DeadForSec = 600 };
				p4.ExpectKick = true;
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["plateau"] = ("a steadily slow PC: p4 manages ~66% for the whole game (60ms per 40ms tick); the game should settle there, not saw-tooth (run 180s)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 60 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["floorreturn"] = ("p4's PC manages only ~40% for the first minute (below the 50% floor: left behind), then recovers fully and should catch back up (run 120s)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { Schedule = "0:100,60:100,62:10" };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			// Scenarios from the first day of v1.1 (see NETCODE.md, "v1.2")
			["melo"] = ("the 14-player 'Melo' case: p4's PC is fine for a minute, slides to ~60% over 30s as a battle builds, stays there two minutes, recovers to ~85% and later to ~105% (run 420s)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { Schedule = "0:39,60:39,90:67,210:67,240:47,360:47,390:38" };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["offsethold"] = ("the 'super maq' case (1 Oct 2026): p4's PC manages ~70% for the first 60s, then is fine, but its lateness reads 600ms high (a round trip its ping does not show); the game must not stay held at 70% (run 240s)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { Schedule = "0:57,55:57,65:30", ReceiveDelayMs = 600 };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["wander"] = ("the 'K$' case: p4's PC wanders between ~66% and ~92% on a minute-and-a-half timescale for eight minutes (run 480s)", () =>
			{
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { Schedule = "0:47,60:52,120:60,210:46,270:44,330:56,420:50,480:60" };
				return new() { Good("p1"), Good("p2"), Good("p3"), p4 };
			}),

			["nextslowest"] = ("p4's PC manages ~60% and p3's ~80%; p4 leaves after two minutes, and the game must find p3's ceiling without leaving p3 seconds behind (run 240s)", () =>
			{
				var p3 = Good("p3");
				p3.Cpu = new CpuSpec { TickMs = 50 };
				var p4 = Good("p4");
				p4.Cpu = new CpuSpec { TickMs = 67 };
				p4.LeaveAfterSec = 120;
				return new() { Good("p1"), Good("p2"), p3, p4 };
			}),
		};
	}

	static class Program
	{
		static int Main(string[] args)
		{
			var opts = ParseArgs(args);
			var port = int.Parse(opts.GetValueOrDefault("port", "21234"));
			var scenarioName = opts.GetValueOrDefault("scenario", "clean");
			var duration = double.Parse(opts.GetValueOrDefault("duration", "60"), CultureInfo.InvariantCulture);
			var warmup = double.Parse(opts.GetValueOrDefault("warmup", "5"), CultureInfo.InvariantCulture);
			var seed = int.Parse(opts.GetValueOrDefault("seed", "1"));
			var label = opts.GetValueOrDefault("label", "server");
			var outPath = opts.GetValueOrDefault("out", null);
			var ordersPerSecond = double.Parse(opts.GetValueOrDefault("apm", "180"), CultureInfo.InvariantCulture) / 60.0;

			FakeClient.SkipReady = opts.GetValueOrDefault("crashtest", "0") == "1";
			FakeClient.GameSpeed = opts.GetValueOrDefault("speed", null);
			FakeClient.MapRaceUid = opts.GetValueOrDefault("maprace", null);
			FakeClient.DefeatBit = int.Parse(opts.GetValueOrDefault("defeatbit", "-1"));
			FakeClient.DefeatAtFrame = int.Parse(opts.GetValueOrDefault("defeatframe", int.MaxValue.ToString()));
			var (description, build) = Scenarios.All[scenarioName];
			var specs = build();
			var server = new IPEndPoint(IPAddress.Loopback, port);

			var proxies = new List<DelayProxy>();
			var clients = new List<FakeClient>();
			for (var i = 0; i < specs.Count; i++)
			{
				var s = specs[i];
				var proxy = new DelayProxy(server, s.Up, s.Down, seed * 100 + i);
				proxies.Add(proxy);
				var c = new FakeClient(s.Name, s.Cpu, seed * 1000 + i, specs.Count(x => !x.NeverReady), ordersPerSecond) { NeverReady = s.NeverReady, ExpectedTotalClients = specs.Count, Spectator = s.Spectator, ExpectKick = s.ExpectKick };
				c.Metrics.Link = $"up {s.Up} / down {s.Down}";
				clients.Add(c);
				c.Connect(new IPEndPoint(IPAddress.Loopback, proxy.Port));

				// Join in order so the first client becomes the lobby admin
				Thread.Sleep(400);
			}

			// Wait for the game to start
			var lurkers = clients.Where(c => c.NeverReady).ToList();
			var all = clients;
			clients = clients.Where(c => !c.NeverReady).ToList();
			var deadline = Clock.Now + 30000;
			while (!clients.All(c => c.GameStarted) && Clock.Now < deadline && !clients.Any(c => c.Failed))
				Thread.Sleep(50);

			if (!clients.All(c => c.GameStarted))
			{
				Console.Error.WriteLine("Game failed to start: " + string.Join("; ", clients.Select(c => c.Metrics.Fatal ?? $"{c.Metrics.Name} started={c.GameStarted}")));
				return 2;
			}

			var start = clients.Max(c => c.GameStartTime);
			Clock.GameStart = start;
			var windowStart = start + warmup * 1000;
			var windowEnd = start + (warmup + duration) * 1000;
			var leavers = clients.Select((c, i) => (c, specs.Where(x => !x.NeverReady).ElementAt(i).LeaveAfterSec)).Where(x => !double.IsNaN(x.LeaveAfterSec)).ToList();
			// Scripted chat: once the server has announced a slowdown, p1 asks !speed, then p1, p2, p3 vote !kickslow
			var voteScript = opts.GetValueOrDefault("votekick", "0") == "1";
			var voteStage = 0;
			double nextVoteAction = 0;
			while (Clock.Now < windowEnd && !clients.Any(c => c.Failed))
			{
				foreach (var (c, leaveAfter) in leavers)
					if (!c.Closed && Clock.Now >= windowStart + leaveAfter * 1000)
						c.Stop();

				if (voteScript && voteStage < 5)
				{
					if (voteStage == 0)
					{
						if (clients[0].ServerMessages.Any(m => m.Text.StartsWith("Slowing the game", StringComparison.Ordinal)))
						{
							voteStage = 1;
							nextVoteAction = Clock.Now + 2000;
						}
					}
					else if (Clock.Now >= nextVoteAction)
					{
						if (voteStage == 1)
					{
						clients[0].ChatToSend.Enqueue("is it just me or is this LAGGING?");
						clients[1].ChatToSend.Enqueue("yeah slow here too");
					}

					clients[voteStage == 1 ? 0 : voteStage - 2].ChatToSend.Enqueue(voteStage == 1 ? "!speed" : "!kickslow");
						voteStage++;
						nextVoteAction = Clock.Now + 2000;
					}
				}

				Thread.Sleep(100);
			}

			foreach (var c in all)
				c.Stop();

			var extraChecks = new List<string>();
			foreach (var l in lurkers)
				extraChecks.Add($"{l.Metrics.Name}: kicked={(l.Closed ? "yes" : "NO")}, got kick message before disconnect={(l.GotServerError ? "yes" : "NO")}");

			if (voteScript)
			{
				var kicked = clients.Where(c => c.ExpectKick).ToList();
				var voters = clients.Where(c => !c.ExpectKick).ToList();
				foreach (var k in kicked)
				{
					var minAfter = voters.Min(x => x.Metrics.FramesProcessed) - k.Metrics.FramesProcessed;
					extraChecks.Add($"{k.Metrics.Name}: kicked by vote={(k.Closed && k.GotServerError ? "yes" : "NO")} at frame {k.Metrics.FramesProcessed}; the others kept going for {minAfter} more frames");
					extraChecks.Add($"{k.Metrics.Name} saw: " + string.Join(" | ", k.ServerMessages.Select(m => m.Text)));
				}

				foreach (var v in voters)
					extraChecks.Add($"{v.Metrics.Name} saw: " + string.Join(" | ", v.ServerMessages.Select(m => m.Text)));

				if (voteStage < 5 || kicked.Any(k => !k.Closed || !k.GotServerError))
					extraChecks.Add("VOTE KICK FAIL: " + (voteStage < 5 ? $"script only reached stage {voteStage}" : "slowest player not kicked"));
			}

			foreach (var (c, _) in leavers)
			{
				var remaining = clients.Where(x => !leavers.Any(l => l.c == x)).ToList();
				var minAfter = remaining.Min(x => x.Metrics.FramesProcessed) - c.Metrics.FramesProcessed;
				extraChecks.Add($"after {c.Metrics.Name} left at frame {c.Metrics.FramesProcessed}, the others kept going for {minAfter} more frames");
			}

			for (var i = 0; i < all.Count; i++)
				all[i].Metrics.Link += " | injected: " + proxies[i].Injected;

			foreach (var p in proxies)
				p.Dispose();

			// Client-side replays must still play back correctly
			var replayFailures = 0;
			foreach (var c in clients)
			{
				var latency = c.WorldTimestep switch { 80 => 2, 50 => 3, 40 => 3, 35 => 4, 30 => 4, 20 => 6, _ => 3 };
				var (replayOk, replayDetail) = ReplayCheck.Check(c.Metrics, latency);
				if (!replayOk)
				{
					replayFailures++;
					extraChecks.Add($"{c.Metrics.Name}: REPLAY FAIL: {replayDetail}");
				}
				else if (c == clients[0])
					extraChecks.Add($"{c.Metrics.Name}: {replayDetail} (all other clients' replays checked too)");
			}

			var result = Summarize(label, scenarioName, description, clients, windowStart, Math.Min(windowEnd, Clock.Now));
			if (replayFailures > 0)
				result.LockstepOk = false;
			result.ExtraChecks = extraChecks;
			if (lurkers.Any(l => !l.Closed || !l.GotServerError))
				result.LockstepOk = false;
			if (extraChecks.Any(x => x.StartsWith("VOTE KICK FAIL", StringComparison.Ordinal)))
				result.LockstepOk = false;
			var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
			if (outPath != null)
				File.WriteAllText(outPath, json);

			var dumpPath = opts.GetValueOrDefault("dump", null);
			if (dumpPath != null)
				File.WriteAllText(dumpPath, JsonSerializer.Serialize(clients.Select(c => new
				{
					c.Metrics.Name,
					Ticks = c.Metrics.TickWallTimes,
					Scales = c.Metrics.TickScales.Select(x => new[] { x.T, x.Scale }),
					Queue = c.Metrics.QueueSamples.Select(x => new[] { x.T, x.Queue }),
					Start = windowStart,
					End = windowEnd
				})));

			PrintSummary(result);
			return result.LockstepOk ? 0 : 1;
		}

		public sealed class ClientSummary
		{
			public string Name { get; set; }
			public string Link { get; set; }
			public string Cpu { get; set; }
			public double SpeedPct { get; set; }
			public int Freezes { get; set; }
			public double FreezesPerMin { get; set; }
			public double FrozenMsPerMin { get; set; }
			public double MaxGapMs { get; set; }
			public double GapP99Ms { get; set; }
			public double OwnLatencyP50 { get; set; }
			public double OwnLatencyP95 { get; set; }
			public double RemoteLatencyP50 { get; set; }
			public double RemoteLatencyP95 { get; set; }
			public double MinTickScale { get; set; }
			public double MaxTickScale { get; set; }
			public double PctTimeScaled { get; set; }
			public double AvgQueue { get; set; }
			public int FramesProcessed { get; set; }
			public string Fatal { get; set; }
		}

		public sealed class RunSummary
		{
			public string Label { get; set; }
			public string Scenario { get; set; }
			public string Description { get; set; }
			public double WindowSeconds { get; set; }
			public bool LockstepOk { get; set; }
			public int ComparedFrames { get; set; }
			public string LockstepDetail { get; set; }
			public List<ClientSummary> Clients { get; set; }
			public List<string> ExtraChecks { get; set; } = new();
		}

		static double Pct(List<double> xs, double p)
		{
			if (xs.Count == 0)
				return double.NaN;

			var s = xs.OrderBy(x => x).ToList();
			var idx = Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Count) - 1, 0, s.Count - 1);
			return s[idx];
		}

		static RunSummary Summarize(string label, string scenario, string description, List<FakeClient> clients, double t0, double t1)
		{
			const double FreezeThresholdMs = 100;
			var windowMs = t1 - t0;
			var summaries = new List<ClientSummary>();
			foreach (var c in clients)
			{
				var m = c.Metrics;
				var ticks = m.TickWallTimes.Where(t => t >= t0 && t <= t1).ToList();
				var gaps = ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToList();
				var freezes = gaps.Where(g => g >= FreezeThresholdMs).ToList();

				// Time-weighted share of the window where a non-1 tick scale was in force
				double scaled = 0, current = 1, last = t0;
				double minScale = 1, maxScale = 1;
				foreach (var (t, s) in m.TickScales)
				{
					if (t < t0)
					{
						current = s;
						continue;
					}

					if (t > t1)
						break;

					if (current != 1)
						scaled += t - last;

					last = t;
					current = s;
					minScale = Math.Min(minScale, s);
					maxScale = Math.Max(maxScale, s);
				}

				if (current != 1)
					scaled += t1 - last;

				summaries.Add(new ClientSummary
				{
					Name = m.Name,
					Link = m.Link,
					Cpu = m.Cpu,
					SpeedPct = ticks.Count * c.WorldTimestep / windowMs * 100,
					Freezes = freezes.Count,
					FreezesPerMin = freezes.Count / (windowMs / 60000),
					FrozenMsPerMin = freezes.Sum(g => g - c.WorldTimestep) / (windowMs / 60000),
					MaxGapMs = gaps.Count > 0 ? gaps.Max() : double.NaN,
					GapP99Ms = Pct(gaps, 99),
					OwnLatencyP50 = Pct(m.OwnOrderLatency, 50),
					OwnLatencyP95 = Pct(m.OwnOrderLatency, 95),
					RemoteLatencyP50 = Pct(m.RemoteOrderLatency, 50),
					RemoteLatencyP95 = Pct(m.RemoteOrderLatency, 95),
					MinTickScale = minScale,
					MaxTickScale = maxScale,
					PctTimeScaled = scaled / windowMs * 100,
					AvgQueue = m.QueueSamples.Where(q => q.T >= t0 && q.T <= t1).Select(q => (double)q.Queue).DefaultIfEmpty(double.NaN).Average(),
					FramesProcessed = m.FramesProcessed,
					Fatal = m.Fatal
				});
			}

			// Lockstep check: every client must have produced the same order-stream hash for every frame they all processed
			var common = clients.Select(c => c.Metrics.SyncHashes.Keys.ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; });
			var mismatches = common.Where(f => clients.Select(c => c.Metrics.SyncHashes[f]).Distinct().Count() > 1).OrderBy(f => f).ToList();
			var fatals = clients.Where(c => c.Metrics.Fatal != null).Select(c => c.Metrics.Fatal).ToList();
			var ok = mismatches.Count == 0 && fatals.Count == 0 && common.Count > 0;
			var detail = ok ? $"all {clients.Count} clients agree on {common.Count} frames"
				: (fatals.Count > 0 ? string.Join("; ", fatals) + ". " : "") + (mismatches.Count > 0 ? $"{mismatches.Count} mismatched frames, first {mismatches[0]}" : "");

			return new RunSummary
			{
				Label = label,
				Scenario = scenario,
				Description = description,
				WindowSeconds = windowMs / 1000,
				LockstepOk = ok,
				ComparedFrames = common.Count,
				LockstepDetail = detail,
				Clients = summaries
			};
		}

		static void PrintSummary(RunSummary r)
		{
			Console.WriteLine($"== {r.Label} / {r.Scenario}: {r.Description} ({r.WindowSeconds:F0}s measured)");
			Console.WriteLine($"   lockstep: {(r.LockstepOk ? "OK" : "FAIL")} - {r.LockstepDetail}");
			foreach (var x in r.ExtraChecks)
				Console.WriteLine($"   check: {x}");
			Console.WriteLine("   client  speed%  freezes/min  frozen-ms/min  max-gap  own-lat p50/p95  remote-lat p50/p95  scale[min,max] %scaled  queue");
			foreach (var c in r.Clients)
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"   {0,-6} {1,6:F1} {2,11:F1} {3,14:F0} {4,8:F0} {5,8:F0}/{6,-6:F0} {7,10:F0}/{8,-6:F0} [{9:F2},{10:F2}] {11,6:F0} {12,6:F1}",
					c.Name, c.SpeedPct, c.FreezesPerMin, c.FrozenMsPerMin, c.MaxGapMs, c.OwnLatencyP50, c.OwnLatencyP95,
					c.RemoteLatencyP50, c.RemoteLatencyP95, c.MinTickScale, c.MaxTickScale, c.PctTimeScaled, c.AvgQueue));
		}

		static Dictionary<string, string> ParseArgs(string[] args)
		{
			var d = new Dictionary<string, string>();
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
					d[args[i][2..]] = args[++i];
			}

			return d;
		}
	}
}
