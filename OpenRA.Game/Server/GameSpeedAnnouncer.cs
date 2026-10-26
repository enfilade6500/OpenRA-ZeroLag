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

namespace OpenRA.Server
{
	/// <summary>
	/// Decides when to tell the players, in the game chat, that the game has been slowed down for a slow
	/// computer or has sped up again. The scheduler adjusts the speed every second; players only need to hear
	/// about it occasionally, so messages are held back until the change is worth mentioning and enough time
	/// has passed since the last one.
	/// </summary>
	public sealed class GameSpeedAnnouncer
	{
		// Never send more than one message this often (ms)
		const int MinInterval = 30000;

		// While the game is slowed down, a change in speed is only worth a message if it is at least this large
		const int MinChangePercent = 10;

		// Full speed is announced only once it has lasted this long (ms), so a brief recovery makes no noise
		const int FullSpeedSettleTime = 10000;

		// A private line follows the public one it belongs to by this much (ms), so that it arrives as a line of its
		// own, with its own chat sound, instead of blending into the speed message everyone gets at the same moment
		const int PrivateDelay = 2500;

		public readonly struct Announcement
		{
			/// <summary>The message for everyone.</summary>
			public readonly string Message;

			/// <summary>
			/// The message reports a change of game speed (slowed down, back to full speed). Servers may keep these to
			/// the players who asked for them (Server.AnnounceSpeedChanges, !speed on); the other messages, a player
			/// left behind by the floor or one the game has stopped waiting for, are news for everyone.
			/// </summary>
			public readonly bool SpeedChange;

			/// <summary>A message for the player concerned only, or null.</summary>
			public readonly string PrivateMessage;
			public readonly int PrivateClient;

			public Announcement(string message, string privateMessage = null, int privateClient = -1, bool speedChange = false)
			{
				Message = message;
				SpeedChange = speedChange;
				PrivateMessage = privateMessage;
				PrivateClient = privateClient;
			}
		}

		readonly bool namePlayer;
		readonly string voteHint;
		readonly Func<int, string> describeClient;

		// The player last told privately that the game is slowed down for them (-1: nobody), the players told so
		// at any point in this game (each is told once), and the private lines waiting to be sent
		int toldClient = -1;
		readonly HashSet<int> privatelyTold = new();
		readonly Queue<(long Due, int Client, string Text)> pendingPrivate = new();

		int lastAnnouncedSpeed = 100;
		int fastestSinceAnnounce = 100;
		long lastAnnounceTime;
		bool announced;
		long fullSpeedSince = -1;
		int announcedTooSlow;

		// Players already announced as left behind by the floor: a player who catches up and is left behind
		// again is news for the log, not for the chat
		readonly HashSet<int> tooSlowAnnounced = new();
		bool slowestPlayerGone;
		readonly Queue<int> continuedWithout = new();

		/// <summary>
		/// The player the game was slowed down for has been kicked, has left or has been defeated. That the game is
		/// speeding back up is announced at once, so players see the effect of a kick.
		/// </summary>
		public void SlowestPlayerGone()
		{
			slowestPlayerGone = true;
		}

		/// <summary>The game has stopped waiting for a player who stopped responding; everyone is told at once.</summary>
		public void ContinuedWithout(int client)
		{
			continuedWithout.Enqueue(client);
		}

		/// <summary>Creates an announcer for one game.</summary>
		/// <param name="namePlayer">Name the player the game is slowed down for. Otherwise they are only told privately.</param>
		/// <param name="voteHint">Appended to slowdown messages (e.g. how to vote to kick the slowest player), or null.</param>
		/// <param name="describeClient">Returns a player's name.</param>
		public GameSpeedAnnouncer(bool namePlayer, string voteHint, Func<int, string> describeClient)
		{
			this.namePlayer = namePlayer;
			this.voteHint = string.IsNullOrEmpty(voteHint) ? "" : " " + voteHint;
			this.describeClient = describeClient ?? (c => $"client {c}");
		}

		/// <summary>
		/// Call once per scheduler interval. Returns a message to send, or null.
		/// </summary>
		/// <param name="now">Current time (ms).</param>
		/// <param name="speedPercent">Current game speed as a percentage of normal.</param>
		/// <param name="slowestPlayer">The player the game is slowed down for, if any.</param>
		/// <param name="tooSlowCount">Players who would need the game slower than the floor and are falling behind on their own.</param>
		/// <param name="lastTooSlowPlayer">The most recent such player, for the message.</param>
		/// <param name="minSpeedPercent">The floor the game is never slowed below.</param>
		/// <param name="slowestSpeedPercent">What the slowest player's computer was measured managing, as a percentage of
		/// normal speed (0: unknown), for their private message.</param>
		public Announcement? Tick(long now, int speedPercent, int? slowestPlayer, int tooSlowCount, int lastTooSlowPlayer, int minSpeedPercent,
			int slowestSpeedPercent = 0)
		{
			// A private line whose moment has come goes out on its own, outside the rate limit on public messages
			if (pendingPrivate.Count > 0 && now >= pendingPrivate.Peek().Due)
			{
				var (_, client, text) = pendingPrivate.Dequeue();
				return new Announcement(null, text, client);
			}

			if (speedPercent >= 100)
			{
				if (fullSpeedSince < 0)
					fullSpeedSince = now;
			}
			else
				fullSpeedSince = -1;

			// Players who caught up again need no message; only note it so a later one is announced again
			if (tooSlowCount < announcedTooSlow)
				announcedTooSlow = tooSlowCount;

			if (slowestPlayerGone)
			{
				slowestPlayerGone = false;
				if (lastAnnouncedSpeed < 100)
				{
					if (speedPercent >= 100)
					{
						lastAnnouncedSpeed = fastestSinceAnnounce = 100;
						AllClear(now);
						return Announce(now, "The game is back to full speed.", speedChange: true);
					}

					// The game probes back up rather than jumping (the others have not been tested above this speed);
					// say so now, and "back to full speed" follows when it gets there
					lastAnnouncedSpeed = fastestSinceAnnounce = speedPercent;
					return Announce(now, "Speeding the game back up.", speedChange: true);
				}
			}

			// A player the game has stopped waiting for is news that cannot wait: everyone just sat through the pause
			if (continuedWithout.Count > 0)
			{
				var client = continuedWithout.Dequeue();
				return Announce(now, $"{describeClient(client)} has stopped responding; the game continues without them. " +
					"They can catch up if their connection comes back.");
			}

			fastestSinceAnnounce = Math.Max(fastestSinceAnnounce, speedPercent);
			if (announced && now - lastAnnounceTime < MinInterval)
				return null;

			// A player being left behind is the more important news, and follows a slowdown closely; each player once
			if (tooSlowCount > announcedTooSlow)
			{
				announcedTooSlow = tooSlowCount;
				if (!tooSlowAnnounced.Add(lastTooSlowPlayer))
					return null;

				var who = namePlayer ? $"{describeClient(lastTooSlowPlayer)}'s computer" : "The slowest computer";

				// This line is final: no all-clear follows it when the game (without them) is back to full speed
				if (toldClient == lastTooSlowPlayer)
					toldClient = -1;

				pendingPrivate.Enqueue((now + PrivateDelay, lastTooSlowPlayer,
					$"{Flag(lastTooSlowPlayer)} your computer can't keep up with the game even at {minSpeedPercent}% speed, " +
					$"so the game will not be slowed down any further for you.{Advice}"));
				return Announce(now,
					$"{who} can't keep up even at {minSpeedPercent}% speed, so the game will not be slowed down any further and that player will fall behind on their own.");
			}

			if (speedPercent >= 100)
			{
				if (lastAnnouncedSpeed >= 100 || now - fullSpeedSince < FullSpeedSettleTime)
					return null;

				lastAnnouncedSpeed = fastestSinceAnnounce = 100;
				AllClear(now);
				return Announce(now, "The game is back to full speed.", speedChange: true);
			}

			// Partial recoveries are not announced (the game speeds back up in small steps, and only full speed
			// is worth a line); a further slowdown is, measured against the fastest the game has been since the
			// last message, so that "60%, then quietly up to 90%, then 75%" is reported as the slowdown it felt like
			if (speedPercent >= lastAnnouncedSpeed && speedPercent > fastestSinceAnnounce - MinChangePercent)
				return null;

			if (speedPercent < lastAnnouncedSpeed && lastAnnouncedSpeed - speedPercent < MinChangePercent)
				return null;

			lastAnnouncedSpeed = fastestSinceAnnounce = speedPercent;
			var subject = namePlayer && slowestPlayer.HasValue ? $"{describeClient(slowestPlayer.Value)}'s computer" : "the slowest computer";
			var message = $"Slowing the game to {speedPercent}% so that {subject} can keep up.{voteHint}";
			if (!slowestPlayer.HasValue)
				return Announce(now, message, speedChange: true);

			// The player is told once per game: the first time the game is slowed down for them. Further slowdowns
			// for the same player are the same news; a line each time was found excessive.
			if (privatelyTold.Add(slowestPlayer.Value))
			{
				var managing = slowestSpeedPercent > 0 ? $" (it is managing about {slowestSpeedPercent}%)" : "";
				TellPrivately(now, slowestPlayer.Value,
					$"{Flag(slowestPlayer.Value)} the game is slowed to {speedPercent}% because your computer is not keeping up{managing}.{Advice}");
			}

			return Announce(now, message, speedChange: true);
		}

		/// <summary>The opening of a private line: the player's own name.</summary>
		string Flag(int client) => $"{describeClient(client)}:";

		// What a player can do about a computer that is not keeping up: the bench showed that the display settings
		// (VSync, the frame limiters) change nothing about the simulation's speed, but a laptop on battery, or in a
		// power-saving mode, runs it at two thirds of its plugged-in speed.
		const string Advice = " Close other programs; on a laptop, plug in the charger and pick the performance power mode.";

		/// <summary>Queues a private line for the player the game is slowed down for; the player told before, if another, gets the all-clear.</summary>
		void TellPrivately(long now, int client, string text)
		{
			if (toldClient >= 0 && toldClient != client)
				AllClear(now);

			toldClient = client;
			pendingPrivate.Enqueue((now + PrivateDelay, client, text));
		}

		/// <summary>Tells the player last told that the game is no longer slowed down for them, so they know a change they made worked.</summary>
		void AllClear(long now)
		{
			if (toldClient < 0)
				return;

			pendingPrivate.Enqueue((now + PrivateDelay, toldClient, $"{Flag(toldClient)} your computer is keeping up again."));
			toldClient = -1;
		}

		Announcement Announce(long now, string message, bool speedChange = false)
		{
			announced = true;
			lastAnnounceTime = now;
			return new Announcement(message, speedChange: speedChange);
		}
	}
}
