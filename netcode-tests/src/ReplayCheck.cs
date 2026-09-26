using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA;
using OpenRA.Network;

namespace NetHarness
{
	/// <summary>
	/// Plays back what a client recorded the way the release game's ReplayConnection + OrderManager
	/// would (same chunking, same "feed chunks up to NetFrameNumber + OrderLatency" rule) and checks
	/// that the replay neither stalls nor diverges from what the client saw live.
	/// </summary>
	public static class ReplayCheck
	{
		sealed class Chunk
		{
			public int Frame;
			public (int ClientId, byte[] Packet)[] Packets;
		}

		public static (bool Ok, string Detail) Check(ClientMetrics m, int orderLatency)
		{
			// ReplayConnection constructor
			var chunks = new Queue<Chunk>();
			var packets = new List<(int, byte[])>();
			foreach (var (client, packet) in m.Recording)
			{
				var frame = BitConverter.ToInt32(packet, 0);
				packets.Add((client, packet));

				if (packet.Length > 4 && (packet[4] == (byte)OrderType.Disconnect || packet[4] == (byte)OrderType.SyncHash))
					continue;

				if (frame != 0)
				{
					chunks.Enqueue(new Chunk { Frame = frame, Packets = packets.ToArray() });
					packets.Clear();
				}
			}

			// OrderManager fed by ReplayConnection.Receive
			var pending = m.LobbyClientIndices.ToDictionary(i => i, _ => new Queue<(int Frame, byte[] Data, bool Disconnect)>());
			var net = 1;
			uint hash = 2166136261;
			var matched = 0;
			while (true)
			{
				while (chunks.Count != 0 && chunks.Peek().Frame <= net + orderLatency)
				{
					foreach (var (client, packet) in chunks.Dequeue().Packets)
					{
						if (OrderIO.TryParseDisconnect((client, packet), out var disconnect))
						{
							if (pending.TryGetValue(disconnect.ClientId, out var dq))
								dq.Enqueue((disconnect.Frame, null, true));
						}
						else if (OrderIO.TryParseSync(packet, out _))
						{
						}
						else if (OrderIO.TryParseOrderPacket(packet, out var orders))
						{
							if (orders.Frame == 0)
								continue;

							if (!pending.TryGetValue(client, out var q))
								return (false, $"replay: packet from disconnected client {client} at frame {orders.Frame}");

							q.Enqueue((orders.Frame, packet, false));
						}
						else
							return (false, $"replay: unknown packet from client {client}");
					}
				}

				if (pending.Count == 0 || !pending.Values.All(q => q.Count > 0))
				{
					if (chunks.Count == 0)
						break;

					return (false, $"replay stalls at frame {net}: waiting for orders that are only in later chunks (next chunk is frame {chunks.Peek().Frame})");
				}

				List<int> remove = null;
				foreach (var (id, q) in pending)
				{
					var (frame, data, disc) = q.Dequeue();
					if (frame != net)
						return (false, $"replay crashes at frame {net}: client {id} packet is for frame {frame}");

					if (disc)
					{
						(remove ??= new()).Add(id);
						continue;
					}

					unchecked
					{
						var h = hash;
						void Mix(byte b) { h ^= b; h *= 16777619; }
						foreach (var b in BitConverter.GetBytes(net)) Mix(b);
						foreach (var b in BitConverter.GetBytes(id)) Mix(b);
						for (var i = 4; i < data.Length; i++) Mix(data[i]);
						hash = h;
					}
				}

				if (remove != null)
					foreach (var id in remove)
						pending.Remove(id);

				if (m.SyncHashes.TryGetValue(net, out var live))
				{
					if (live != (int)hash)
						return (false, $"replay diverges from the live game at frame {net}");

					matched++;
				}

				net++;
			}

			var liveFrames = m.SyncHashes.Count;
			if (matched < liveFrames)
				return (false, $"replay ends at frame {net - 1}, before the live game did ({liveFrames} frames)");

			return (true, $"replay plays back all {matched} frames identically");
		}
	}
}
