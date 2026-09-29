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
	/// for. Voters do not need to know who that is. Only players still in the game (or the admin) can vote, and a
	/// majority of them is needed. Unlike the normal vote kick, which everyone answers at once in a dialog, votes
	/// are typed whenever a player gets fed up, so they do not lapse after a fixed time: they stand for as long as
	/// the same player keeps the game slow (a brief return to full speed does not clear them), and are cancelled
	/// only when the game has not been slowed down for that player for TargetGrace, or is slowed down for
	/// someone else instead.
	/// </summary>
	public sealed class SlowestPlayerVote
	{
		[FluentReference("player")]
		const string Kicked = "notification-kicked";

		[FluentReference]
		const string YouWereKicked = "notification-you-were-kicked";

		public const string Command = "!kickslow";

		// How long the game may run at full speed, or be slowed down for nobody, before the votes are cleared (ms)
		const int TargetGrace = 120000;

		// Typos and guesses seen in the wild count as the command too
		static readonly string[] Aliases = { "!kickslow", "!kicklag", "!kickslowest", "!kickslower", "!ks" };

		readonly Server server;
		readonly HashSet<int> votes = new();

		int target = -1;
		long lastTargetTime;

		/// <summary>Whether a chat command (already lower-cased, starting with '!') is a vote, allowing for extra words after it.</summary>
		public static bool IsCommand(string command)
		{
			var word = command.Split(' ', 2)[0];
			return Aliases.Contains(word);
		}

		/// <summary>Whether a chat command looks like an attempt to kick someone by other means (so the player can be pointed at the vote).</summary>
		public static bool LooksLikeKick(string command) => command.StartsWith("!kick", System.StringComparison.Ordinal) && !IsCommand(command);

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
				lastTargetTime = now;
				votes.Clear();
				Log.Write("server", $"{voter.Name} started a vote to kick the slowest player ({targetClient.Name}).");
			}

			if (!votes.Add(conn.PlayerIndex))
			{
				server.SendOrderTo(conn, "Message", "You have already voted; the vote stands for as long as the game is slowed down for that player.");
				return;
			}

			votes.IntersectWith(voters);
			var needed = eligible / 2 + 1;
			if (votes.Count < needed)
				server.SendMessage($"Vote to kick the slowest player: {votes.Count} of {needed} votes needed, {needed - votes.Count} more. Type {Command} to vote.");
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

		/// <summary>Call regularly; ends a vote whose target has changed or has not been slowing the game for a while.</summary>
		public void Tick(long now, int? slowest)
		{
			if (!InProgress)
				return;

			if (slowest == target)
				lastTargetTime = now;
			else if (slowest.HasValue)
				End("The game is now being slowed down for a different player; the vote to kick the slowest player is cancelled.");
			else if (now - lastTargetTime > TargetGrace)
				End("The game is no longer being slowed down for that player; the vote to kick the slowest player is cancelled.");
		}

		void End(string message)
		{
			server.SendMessage(message);
			Reset();
		}

		void Reset()
		{
			target = -1;
			votes.Clear();
		}
	}
}
