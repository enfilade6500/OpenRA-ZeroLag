using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OpenRA;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Server;

namespace NetHarness
{
	public sealed class CpuSpec
	{
		/// <summary>Wall time consumed by each world tick.</summary>
		public double TickMs = 2;

		/// <summary>Average hitches per second (Poisson). A hitch is one world tick that takes HitchMs (GC pause, disk stall, alt-tab...).</summary>
		public double HitchRatePerSec = 0;
		public double HitchMs = 0;

		/// <summary>Recurring heavy phases (big battles): every HeavyEverySec seconds, ticks cost HeavyTickMs for HeavyForSec seconds.</summary>
		public double HeavyEverySec = 0;
		public double HeavyForSec = 0;
		public double HeavyTickMs = 0;

		public override string ToString() =>
			$"tick {TickMs}ms" + (HitchRatePerSec > 0 ? $" hitch {HitchMs}ms@{HitchRatePerSec}/s" : "") +
			(HeavyEverySec > 0 ? $" heavy {HeavyTickMs}ms for {HeavyForSec}s every {HeavyEverySec}s" : "");
	}

	public sealed class ClientMetrics
	{
		public string Name;
		public string Link;
		public string Cpu;
		public List<double> TickWallTimes = new();
		public List<double> OwnOrderLatency = new();
		public List<double> RemoteOrderLatency = new();
		public List<(double T, float Scale)> TickScales = new();
		public List<(double T, int Queue)> QueueSamples = new();

		/// <summary>Everything the release client's ReplayRecorder would have written, in order.</summary>
		public List<(int ClientId, byte[] Packet)> Recording = new();
		public List<int> LobbyClientIndices = new();
		public Dictionary<int, int> SyncHashes = new();
		public string Fatal;
		public int Desyncs;
		public int FramesProcessed;
	}

	/// <summary>
	/// A headless client that speaks the release network protocol (using the release OpenRA.Game.dll
	/// for all serialization) and reproduces the release client's lockstep pacing logic:
	/// Game.Loop, Game.InnerLogicTick, OrderManager.TryTick/ProcessOrders, TickTime and
	/// NetworkConnection.Receive. The world simulation is replaced by a configurable CPU cost,
	/// and the world sync hash is replaced by a hash of the order stream, so any lockstep
	/// violation by the server shows up as a desync or as the release client's frame-mismatch crash.
	/// </summary>
	public sealed class FakeClient
	{
		const int JankThreshold = 250; // Game.TimestepJankThreshold
		const int MaxLogicTicksBehind = 250; // Game.Loop
		const int UiTimestep = 40;

		public readonly ClientMetrics Metrics = new();
		public int ClientId => clientId;
		public bool GameStarted => netFrameNumber != 0;
		public bool Failed => Metrics.Fatal != null;
		public Session LobbyInfo = new();

		/// <summary>Never reports ready, so the server kicks it when the game starts (tests the kick/flush path).</summary>
		public bool NeverReady;

		/// <summary>Crash test: never report "NotReady" and let the admin start anyway.</summary>
		public static bool SkipReady;

		/// <summary>
		/// Crash test: once everyone is ready, the admin changes the map and immediately starts the game, before
		/// anyone (including itself) has confirmed it has the new map. A real player can only do this by clicking
		/// Start within one round trip of changing the map.
		/// </summary>
		public static string MapRaceUid;

		/// <summary>
		/// Simulated defeat: from net frame DefeatAtFrame on, every client reports world player DefeatBit as
		/// defeated in its sync packets (all clients agree per frame, as the real game would). -1 = nobody.
		/// </summary>
		public static int DefeatBit = -1;
		public static int DefeatAtFrame = int.MaxValue;

		/// <summary>Game speed the lobby admin selects before starting (null = mod default).</summary>
		public static string GameSpeed;
		bool speedRequested;
		public int ExpectedTotalClients;

		/// <summary>This client joins as a spectator (no slot).</summary>
		public bool Spectator;
		bool spectateSent;
		public volatile bool GotServerError;
		public volatile bool Closed;

		/// <summary>This client is expected to be kicked during the game, so losing the connection after a kick message is not a failure.</summary>
		public bool ExpectKick;

		/// <summary>Chat lines to send to the server (as the release client does when the player presses Enter).</summary>
		public readonly ConcurrentQueue<string> ChatToSend = new();

		/// <summary>System lines received from the server ("Message" orders), and the keys of localized ones ("FluentMessage").</summary>
		public readonly ConcurrentQueue<(double T, string Text)> ServerMessages = new();
		public double ClosedAt = double.NaN;

		readonly string name;
		readonly CpuSpec cpu;
		readonly Random rng;
		readonly int expectedClients;
		readonly double ordersPerSecond;
		readonly ConcurrentQueue<(int FromClient, byte[] Data)> receivedPackets = new();

		Socket socket;
		NetworkStream stream;
		volatile int clientId = -1;
		volatile bool stop;
		volatile bool connectionLost;
		Thread mainThread;

		// OrderManager state
		readonly Dictionary<int, Queue<(int Frame, byte[] Payload, bool Disconnect)>> pendingOrders = new();
		readonly Dictionary<int, int> syncForFrame = new();
		readonly List<Order> localOrders = new();
		readonly List<Order> localImmediateOrders = new();
		int netFrameNumber;
		int localFrameNumber;
		int sentOrdersFrame;
		float tickScale = 1f;
		int worldTimestep = 40;
		int netFrameInterval = 3;
		TickTime lastTickTime;
		uint orderStreamHash = 2166136261;
		int orderSeq;
		bool startRequested;
		bool stateSent;
		double nextHitch = double.MaxValue;

		// NetworkConnection state
		readonly Queue<OrderPacket> sentOrders = new();
		readonly Queue<byte[]> queuedSyncPackets = new();

		public FakeClient(string name, CpuSpec cpu, int seed, int expectedClients, double ordersPerSecond)
		{
			this.name = name;
			this.cpu = cpu;
			this.expectedClients = expectedClients;
			this.ordersPerSecond = ordersPerSecond;
			rng = new Random(seed);
			Metrics.Name = name;
			Metrics.Cpu = cpu.ToString();
			lastTickTime = new TickTime(() => SuggestedTimestep, Clock.RunTime);
		}

		public void Connect(IPEndPoint endpoint)
		{
			socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
			socket.Connect(endpoint);
			stream = new NetworkStream(socket);
			new Thread(ReceiveLoop) { IsBackground = true, Name = name + "-recv" }.Start();
			mainThread = new Thread(MainLoop) { IsBackground = true, Name = name + "-main" };
			mainThread.Start();
		}

		public void Stop()
		{
			stop = true;
			mainThread?.Join(2000);
			try { socket?.Dispose(); } catch { }
		}

		void Fail(string message)
		{
			Metrics.Fatal ??= $"{name} @ {Clock.Now / 1000:F1}s: {message}";
			stop = true;
		}

		// NetworkConnection.NetworkConnectionReceive
		void ReceiveLoop()
		{
			try
			{
				var r = new BinaryReader(stream);
				var handshakeProtocol = r.ReadInt32();
				if (handshakeProtocol != ProtocolVersion.Handshake)
					throw new InvalidOperationException($"Handshake protocol mismatch {handshakeProtocol}");

				clientId = r.ReadInt32();
				while (!stop)
				{
					var len = r.ReadInt32();
					var client = r.ReadInt32();
					var buf = r.ReadBytes(len);
					if (len == 0)
						throw new NotImplementedException();

					receivedPackets.Enqueue((client, buf));
				}
			}
			catch (Exception e)
			{
				Closed = true;
				ClosedAt = Clock.Now;
				if (NeverReady)
					Console.WriteLine($"[debug] {name} connection closed at {Clock.Now:F0}: {e.GetType().Name} {e.Message}");
				// A kicked client still has the server's last packets (including the kick message) queued for the
				// main thread, exactly as the release client does; it judges the disconnect once it has read them
				if (ExpectKick)
					connectionLost = true;
				else if (!stop && !NeverReady)
					Fail("connection lost: " + e.Message);
			}
		}

		int SuggestedTimestep
		{
			get
			{
				if (!GameStarted)
					return UiTimestep;

				if (tickScale != 1f)
					return Math.Max((int)(tickScale * worldTimestep), 1);

				return worldTimestep;
			}
		}

		// Game.Loop (logic part only; rendering is not simulated)
		void MainLoop()
		{
			try
			{
				var nextLogic = Clock.RunTime;
				while (!stop)
				{
					var logicInterval = SuggestedTimestep;
					var now = Clock.RunTime;
					if (now - nextLogic > MaxLogicTicksBehind)
						nextLogic = now;

					if (now >= nextLogic)
					{
						nextLogic += logicInterval;
						InnerLogicTick();
					}
					else
						Thread.Sleep((int)(nextLogic - now));
				}
			}
			catch (Exception e)
			{
				Fail("exception: " + e);
			}
		}

		// Game.InnerLogicTick
		void InnerLogicTick()
		{
			var tick = Clock.RunTime;
			if (!lastTickTime.ShouldAdvance(tick))
				return;

			lastTickTime.AdvanceTickTime(tick);

			// OrderManager.TickImmediate
			while (ChatToSend.TryDequeue(out var chat))
				localImmediateOrders.Add(Order.Chat(chat));

			SendImmediateOrders();
			Receive();

			if (!GameStarted || stop)
				return;

			// Simulated player input
			if (rng.NextDouble() < ordersPerSecond * worldTimestep / 1000.0)
				localOrders.Add(Order.FromTargetString("NetTest", $"{clientId}:{++orderSeq}:{Clock.Now:F3}", false));

			if (TryTick())
			{
				Metrics.TickWallTimes.Add(Clock.Now);
				WorldTick();
			}
		}

		void WorldTick()
		{
			var cost = cpu.TickMs;
			var now = Clock.Now;
			if (cpu.HeavyEverySec > 0 && !double.IsNaN(GameStartTime) && (now - GameStartTime) / 1000 % cpu.HeavyEverySec >= cpu.HeavyEverySec - cpu.HeavyForSec)
				cost = cpu.HeavyTickMs;

			if (cpu.HitchRatePerSec > 0)
			{
				if (nextHitch == double.MaxValue)
					nextHitch = now + -Math.Log(1 - rng.NextDouble()) / cpu.HitchRatePerSec * 1000;

				if (now >= nextHitch)
				{
					cost = cpu.HitchMs;
					nextHitch = now + -Math.Log(1 - rng.NextDouble()) / cpu.HitchRatePerSec * 1000;
				}
			}

			if (cost > 0)
				Clock.SleepUntil(now + cost);
		}

		bool IsNetFrame => localFrameNumber % netFrameInterval == 0;

		// OrderManager.TryTick
		bool TryTick()
		{
			var shouldTick = true;
			if (IsNetFrame)
			{
				shouldTick = pendingOrders.All(p => p.Key == clientId || p.Value.Count > 0);
				if (shouldTick)
					SendOrders();
			}

			var willTick = shouldTick;
			if (willTick && IsNetFrame)
			{
				willTick = GameStarted && pendingOrders.All(p => p.Value.Count > 0);
				if (willTick)
					ProcessOrders();
			}

			if (willTick)
				localFrameNumber++;

			return willTick;
		}

		void SendOrders()
		{
			if (GameStarted && sentOrdersFrame < netFrameNumber)
			{
				var packet = new OrderPacket(localOrders.ToArray());
				sentOrders.Enqueue(packet);
				Send(packet.Serialize(netFrameNumber));
				localOrders.Clear();
				sentOrdersFrame = netFrameNumber;
			}
		}

		void SendImmediateOrders()
		{
			if (localImmediateOrders.Count != 0)
			{
				var immediate = new OrderPacket(localImmediateOrders.ToArray()).Serialize(0);
				Send(immediate);
				Metrics.Recording.Add((clientId, immediate));
			}

			localImmediateOrders.Clear();
		}

		// NetworkConnection.Send(byte[]): sync packets ride along with the next write
		void Send(byte[] packet)
		{
			try
			{
				var ms = new MemoryStream();
				ms.Write(BitConverter.GetBytes(packet.Length));
				ms.Write(packet);
				while (queuedSyncPackets.Count > 0)
				{
					var q = queuedSyncPackets.Dequeue();
					ms.Write(BitConverter.GetBytes(q.Length));
					ms.Write(q);
				}

				ms.WriteTo(stream);
			}
			catch (Exception) { /* reader thread notices disconnects */ }
		}

		// OrderManager.ProcessOrders
		void ProcessOrders()
		{
			List<int> toRemove = null;
			foreach (var (id, queue) in pendingOrders)
			{
				var (frame, payload, disconnect) = queue.Dequeue();
				if (frame != netFrameNumber)
				{
					// This is the release client's hard crash:
					// "Attempted to process orders from client {id} for frame {frame} on frame {NetFrameNumber}"
					Fail($"Attempted to process orders from client {id} for frame {frame} on frame {netFrameNumber}");
					return;
				}

				if (disconnect)
				{
					(toRemove ??= new()).Add(id);
					continue;
				}

				HashOrders(id, payload);
				RecordLatencies(id, payload);
			}

			if (toRemove != null)
				foreach (var id in toRemove)
					pendingOrders.Remove(id);

			var hash = (int)orderStreamHash;
			Metrics.SyncHashes[netFrameNumber] = hash;
			var defeatState = DefeatBit >= 0 && netFrameNumber >= DefeatAtFrame ? 1UL << DefeatBit : 0UL;
			queuedSyncPackets.Enqueue(OrderIO.SerializeSync((netFrameNumber, hash, defeatState)));
			ReceiveSync(netFrameNumber, hash);
			Metrics.Recording.Add((clientId, OrderIO.SerializeSync((netFrameNumber, hash, defeatState))));
			Metrics.FramesProcessed = netFrameNumber;
			netFrameNumber++;
		}

		void HashOrders(int id, byte[] payload)
		{
			unchecked
			{
				var h = orderStreamHash;
				void Mix(byte b) { h ^= b; h *= 16777619; }
				foreach (var b in BitConverter.GetBytes(netFrameNumber)) Mix(b);
				foreach (var b in BitConverter.GetBytes(id)) Mix(b);
				for (var i = 4; i < payload.Length; i++) Mix(payload[i]);
				orderStreamHash = h;
			}
		}

		void RecordLatencies(int id, byte[] payload)
		{
			if (payload.Length <= 4)
				return;

			if (!OrderIO.TryParseOrderPacket(payload, out var parsed))
				return;

			var now = Clock.Now;
			foreach (var o in parsed.Orders.GetOrders(null))
			{
				if (o.OrderString != "NetTest")
					continue;

				var parts = o.TargetString.Split(':');
				var sentAt = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
				if (id == clientId)
					Metrics.OwnOrderLatency.Add(now - sentAt);
				else
					Metrics.RemoteOrderLatency.Add(now - sentAt);
			}
		}

		void ReceiveSync(int frame, int hash)
		{
			if (syncForFrame.TryGetValue(frame, out var existing))
			{
				if (existing != hash)
				{
					Metrics.Desyncs++;
					Fail($"desync at frame {frame}");
				}
			}
			else
				syncForFrame[frame] = hash;
		}

		int OrderQueueLength => pendingOrders.Count > 0 ? pendingOrders.Min(q => q.Value.Count) : 0;

		// NetworkConnection.Receive + UnitOrders (lobby subset)
		void Receive()
		{
			var lost = connectionLost;
			while (receivedPackets.TryDequeue(out var p))
			{
				if (OrderIO.TryParseDisconnect(p, out var disconnect))
				{
					if (GameStarted && pendingOrders.TryGetValue(disconnect.ClientId, out var q))
						q.Enqueue((disconnect.Frame, null, true));

					Metrics.Recording.Add((p.FromClient, p.Data));
				}
				else if (OrderIO.TryParseSync(p.Data, out var sync))
				{
					ReceiveSync(sync.Frame, sync.SyncHash);
					Metrics.Recording.Add((p.FromClient, p.Data));
				}
				else if (OrderIO.TryParseTickScale(p, out var scale))
				{
					tickScale = scale;
					Metrics.TickScales.Add((Clock.Now, scale));
				}
				else if (OrderIO.TryParsePingRequest(p, out var timestamp))
				{
					var ql = OrderQueueLength;
					if (GameStarted)
						Metrics.QueueSamples.Add((Clock.Now, ql));

					Send(OrderIO.SerializePingResponse(timestamp, (byte)ql));
				}
				else if (OrderIO.TryParseAck(p, out var ackFrame, out var ackCount))
				{
					if (ackCount > sentOrders.Count)
					{
						Fail($"Received Ack for {ackCount} > {sentOrders.Count} frames.");
						return;
					}

					OrderPacket packet;
					if (ackCount != 1)
						packet = OrderPacket.Combine(Enumerable.Range(0, ackCount).Select(_ => sentOrders.Dequeue()).ToList());
					else
						packet = sentOrders.Dequeue();

					var serialized = packet.Serialize(ackFrame);
					ReceiveOrders(clientId, ackFrame, serialized);
					Metrics.Recording.Add((clientId, serialized));
				}
				else if (OrderIO.TryParseOrderPacket(p.Data, out var orders))
				{
					if (orders.Frame == 0)
						HandleImmediate(p.FromClient, orders.Orders);
					else
						ReceiveOrders(p.FromClient, orders.Frame, p.Data);

					Metrics.Recording.Add((p.FromClient, p.Data));
				}
				else
					Fail($"Received unknown packet from client {p.FromClient} with length {p.Data.Length}");

				if (stop)
					return;
			}

			// Everything the server sent before closing the connection has now been read
			if (lost && !stop)
			{
				if (!GotServerError)
					Fail("connection lost without a kick message");

				stop = true;
			}
		}

		void ReceiveOrders(int fromClient, int frame, byte[] data)
		{
			if (pendingOrders.TryGetValue(fromClient, out var queue))
				queue.Enqueue((frame, data, false));
			else
				Fail($"Received packet from disconnected client '{fromClient}'");
		}

		void HandleImmediate(int fromClient, OrderPacket packet)
		{
			foreach (var order in packet.GetOrders(null))
			{
				if (NeverReady)
					Console.WriteLine($"[debug] {name} got {order.OrderString} at {Clock.Now:F0}");

				switch (order.OrderString)
				{
					case "HandshakeRequest":
					{
						var request = HandshakeRequest.Deserialize(order.TargetString, order.OrderString);
						var color = Color.FromAhsv(255, (float)rng.NextDouble(), 0.7f, 0.8f);
						var response = new HandshakeResponse
						{
							Client = new Session.Client
							{
								Name = name,
								PreferredColor = color,
								Color = color,
								Faction = "Random",
								SpawnPoint = 0,
								Team = 0,
								State = Session.ClientState.Invalid
							},
							Mod = request.Mod,
							Version = request.Version,
							Password = "",
							OrdersProtocol = ProtocolVersion.Orders
						};

						localImmediateOrders.Add(new Order("HandshakeResponse", null, false)
						{
							Type = OrderType.Handshake,
							IsImmediate = true,
							TargetString = response.Serialize()
						});
						break;
					}

					case "SyncInfo":
						LobbyInfo = Session.Deserialize(order.TargetString, order.OrderString);
						MaybeStart();
						break;

					case "SyncLobbyClients":
					{
						var clients = new List<Session.Client>();
						foreach (var node in MiniYaml.FromString(order.TargetString, order.OrderString))
							if (node.Key.Split('@')[0] == "Client")
								clients.Add(Session.Client.Deserialize(node.Value));

						LobbyInfo.Clients = clients;
						MaybeStart();
						break;
					}

					case "SyncLobbyGlobalSettings":
						foreach (var node in MiniYaml.FromString(order.TargetString, order.OrderString))
							if (node.Key.Split('@')[0] == "GlobalSettings")
								LobbyInfo.GlobalSettings = Session.Global.Deserialize(node.Value);
						break;

					case "StartGame":
						if (!NeverReady)
							StartGame();
						break;

					case "ServerError":
						GotServerError = true;
						ServerMessages.Enqueue((Clock.Now, "error:" + order.TargetString));
						break;

					case "Message":
						ServerMessages.Enqueue((Clock.Now, order.TargetString));
						break;

					case "FluentMessage":
						foreach (var node in MiniYaml.FromString(order.TargetString, order.OrderString))
							ServerMessages.Enqueue((Clock.Now, "fluent:" + new FluentMessage(node.Value).Key));
						break;
				}
			}
		}

		void MaybeStart()
		{
			var me = LobbyInfo.Clients.FirstOrDefault(c => c.Index == clientId);
			if (me == null)
				return;

			// Become a spectator before readying up
			if (Spectator && !spectateSent && !me.IsObserver)
			{
				spectateSent = true;
				localImmediateOrders.Add(Order.Command("spectate"));
				return;
			}

			// The release client reports "NotReady" once it has the map; the server drops Invalid clients at game start
			if (me.IsInvalid && !stateSent && !NeverReady && !SkipReady)
			{
				stateSent = true;
				localImmediateOrders.Add(Order.Command("state NotReady"));
			}

			if (startRequested || !me.IsAdmin)
				return;

			if (GameSpeed != null && LobbyInfo.GlobalSettings.OptionOrDefault("gamespeed", "default") != GameSpeed)
			{
				if (!speedRequested)
				{
					speedRequested = true;
					localImmediateOrders.Add(Order.Command("option gamespeed " + GameSpeed));
				}

				return;
			}

			// Wait for everyone (including clients that will never ready up) to join, and for the players to be ready
			if (LobbyInfo.Clients.Count(c => !c.IsBot) < ExpectedTotalClients || (!SkipReady && LobbyInfo.Clients.Count(c => !c.IsBot && !c.IsInvalid) < expectedClients))
				return;

			startRequested = true;
			if (MapRaceUid != null)
				localImmediateOrders.Add(Order.Command("map " + MapRaceUid));

			localImmediateOrders.Add(Order.Command("startgame"));
		}

		// OrderManager.StartGame
		void StartGame()
		{
			if (GameStarted)
				return;

			foreach (var client in LobbyInfo.Clients)
			{
				if (!client.IsBot)
				{
					pendingOrders.Add(client.Index, new Queue<(int, byte[], bool)>());
					Metrics.LobbyClientIndices.Add(client.Index);
				}
			}

			var speed = LobbyInfo.GlobalSettings.LobbyOptions.TryGetValue("gamespeed", out var s) ? s.Value : "default";
			worldTimestep = speed switch
			{
				"slowest" => 80,
				"slower" => 50,
				"fast" => 35,
				"faster" => 30,
				"fastest" => 20,
				_ => 40
			};

			// Guard against the test silently running with the wrong roles (e.g. a map with too few slots
			// turning an intended player into an observer), which would invalidate the comparison
			var me = LobbyInfo.Clients.FirstOrDefault(c => c.Index == clientId);
			if (me != null && me.IsObserver != Spectator)
				Fail($"role mismatch at game start: intended {(Spectator ? "spectator" : "player")} but lobby has me as {(me.IsObserver ? "observer" : "player")} (map has too few slots?)");

			netFrameInterval = LobbyInfo.GlobalSettings.NetFrameInterval;
			netFrameNumber = 1;
			localFrameNumber = 0;
			lastTickTime.Value = Clock.RunTime;
			GameStartTime = Clock.Now;
		}

		public double GameStartTime = double.NaN;
		public int WorldTimestep => worldTimestep;
		public int NetFrameInterval => netFrameInterval;
	}
}
