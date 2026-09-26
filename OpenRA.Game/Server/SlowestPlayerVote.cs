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
using OpenRA.Network;

namespace OpenRA.Server
{
	/// <summary>
	/// A vote, held through a chat command, to kick whichever player the game is currently being slowed down
	/// for. Voters do not need to know who that is. The rules mirror the normal vote kick: only players still in
	/// the game (or the admin) can vote, a majority of them is needed, the vote lapses after Server.VoteKickTimer
	/// without a new vote, and whoever started a failed vote cannot start another for Server.VoteKickerCooldown.
	/// </summary>
	public sealed class SlowestPlayerVote
	{
		[FluentReference("player")]
		const string Kicked = "notification-kicked";

		[FluentReference]
		const string YouWereKicked = "notification-you-were-kicked";

		public const string Command = "!kickslow";

		readonly Server server;
		readonly HashSet<int> votes = new();
		readonly Dictionary<int, long> failedStarters = new();

		int target = -1;
		int starter = -1;
		long lastVoteTime;

		public SlowestPlayerVote(Server server)
		{
			this.server = server;
		}

		public bool InProgress => target >= 0;

		bool HasPower(Session.Client client) => client != null && (client.IsAdmin || (!client.IsObserver && !server.HasClientWonOrLost(client)));

		/// <summary>A player typed the command. <paramref name="slowest"/> is who the game is slowed down for right now, or null.</summary>
		public void Vote(Connection conn, long now, int? slowest)
		{
			var voter = server.GetClient(conn);
			if (!HasPower(voter))
			{
				server.SendOrderTo(conn, "Message", "Only players still in the game can vote.");
				return;
			}

			if (!slowest.HasValue)
			{
				server.SendOrderTo(conn, "Message", "The game is not being slowed down for anyone at the moment.");
				return;
			}

			if (slowest.Value == conn.PlayerIndex)
			{
				server.SendOrderTo(conn, "Message", "The game is currently being slowed down for your computer.");
				return;
			}

			if (InProgress && target != slowest.Value)
				End("The player the game was slowed down for has changed; the vote to kick the slowest player is cancelled.");

			if (!InProgress)
			{
				if (failedStarters.TryGetValue(conn.PlayerIndex, out var failedAt) && now - failedAt < server.Settings.VoteKickerCooldown)
				{
					server.SendOrderTo(conn, "Message", "You cannot start another vote yet.");
					return;
				}

				failedStarters.Remove(conn.PlayerIndex);
			}

			var targetConn = server.Conns.FirstOrDefault(c => c.Validated && c.PlayerIndex == slowest.Value);
			var targetClient = targetConn != null ? server.GetClient(targetConn) : null;
			if (targetClient == null)
			{
				server.SendOrderTo(conn, "Message", "The game is not being slowed down for anyone at the moment.");
				return;
			}

			// As for the normal vote kick: the host of a non-dedicated game cannot be kicked out of their own game
			if (targetClient.IsAdmin && server.Type != ServerType.Dedicated)
			{
				server.SendOrderTo(conn, "Message", "The host cannot be kicked.");
				return;
			}

			// Everyone still playing has a say. The slowest player counts as a vote against, so that in an even
			// team game one team cannot kick a member of the other on its own.
			var voters = server.Conns.Where(c => c.Validated && c != targetConn && HasPower(server.GetClient(c))).Select(c => c.PlayerIndex).ToList();
			var eligible = voters.Count + (HasPower(targetClient) ? 1 : 0);
			if (voters.Count < 2)
			{
				server.SendOrderTo(conn, "Message", "There are not enough players for a vote.");
				return;
			}

			if (!InProgress)
			{
				target = slowest.Value;
				starter = conn.PlayerIndex;
				votes.Clear();
				Log.Write("server", $"{voter.Name} started a vote to kick the slowest player ({targetClient.Name}).");
			}

			if (!votes.Add(conn.PlayerIndex))
			{
				server.SendOrderTo(conn, "Message", "You have already voted.");
				return;
			}

			lastVoteTime = now;
			votes.IntersectWith(voters);
			var needed = eligible / 2 + 1;
			if (votes.Count < needed)
				server.SendMessage($"Vote to kick the slowest player: {votes.Count} of {needed} votes needed. Type {Command} to vote.");
			else
			{
				Log.Write("server", $"Vote passed: kicking the slowest player, {targetClient.Name} (client {target}).");
				Reset();
				server.SendFluentMessage(Kicked, "player", targetClient.Name);
				server.SendOrderTo(targetConn, "ServerError", YouWereKicked);
				server.DropClient(targetConn);
				server.SyncLobbyClients();
				server.SyncLobbySlots();
			}
		}

		/// <summary>Call regularly; ends a vote that has lapsed or lost its target.</summary>
		public void Tick(long now, int? slowest)
		{
			if (!InProgress)
				return;

			if (slowest != target)
				End("The game is no longer being slowed down for that player; the vote to kick the slowest player is cancelled.");
			else if (now - lastVoteTime > server.Settings.VoteKickTimer)
			{
				failedStarters[starter] = now;
				End("The vote to kick the slowest player has failed.");
			}
		}

		void End(string message)
		{
			server.SendMessage(message);
			Reset();
		}

		void Reset()
		{
			target = -1;
			starter = -1;
			votes.Clear();
		}
	}
}
