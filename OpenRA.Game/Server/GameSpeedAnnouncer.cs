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

			/// <summary>A message for the player concerned only, or null.</summary>
			public readonly string PrivateMessage;
			public readonly int PrivateClient;

			public Announcement(string message, string privateMessage = null, int privateClient = -1)
			{
				Message = message;
				PrivateMessage = privateMessage;
				PrivateClient = privateClient;
			}
		}

		readonly bool namePlayer;
		readonly bool fastGameSpeed;
		readonly string voteHint;
		readonly Func<int, string> describeClient;

		// The player last told privately that the game is slowed down for them (-1: nobody), and the private lines
		// waiting to be sent
		int toldClient = -1;
		readonly Queue<(long Due, int Client, string Text)> pendingPrivate = new();

		int lastAnnouncedSpeed = 100;
		int fastestSinceAnnounce = 100;
		long lastAnnounceTime;
		bool announced;
		long fullSpeedSince = -1;
		int announcedTooSlow;
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
		/// <param name="fastGameSpeed">The game runs at "faster" or "fastest" (a timestep under 34 ms), where two 60 Hz
		/// refresh periods no longer fit in a tick and VSync alone holds such a client under 100%; the private advice says so.</param>
		public GameSpeedAnnouncer(bool namePlayer, string voteHint, Func<int, string> describeClient, bool fastGameSpeed = false)
		{
			this.namePlayer = namePlayer;
			this.voteHint = string.IsNullOrEmpty(voteHint) ? "" : " " + voteHint;
			this.describeClient = describeClient ?? (c => $"client {c}");
			this.fastGameSpeed = fastGameSpeed;
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
						return Announce(now, "The game is back to full speed.");
					}

					// The game probes back up rather than jumping (the others have not been tested above this speed);
					// say so now, and "back to full speed" follows when it gets there
					lastAnnouncedSpeed = fastestSinceAnnounce = speedPercent;
					return Announce(now, "Speeding the game back up.");
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

			// A player being left behind is the more important news, and follows a slowdown closely
			if (tooSlowCount > announcedTooSlow)
			{
				announcedTooSlow = tooSlowCount;
				var who = namePlayer ? $"{describeClient(lastTooSlowPlayer)}'s computer" : "The slowest computer";
				TellPrivately(now, lastTooSlowPlayer,
					$"{Flag(lastTooSlowPlayer)} your computer can't keep up with the game even at {minSpeedPercent}% speed, " +
					$"so the game will not be slowed down any further for you.{Advice}");
				return Announce(now,
					$"{who} can't keep up even at {minSpeedPercent}% speed, so the game will not be slowed down any further and that player will fall behind on their own.");
			}

			if (speedPercent >= 100)
			{
				if (lastAnnouncedSpeed >= 100 || now - fullSpeedSince < FullSpeedSettleTime)
					return null;

				lastAnnouncedSpeed = fastestSinceAnnounce = 100;
				AllClear(now);
				return Announce(now, "The game is back to full speed.");
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
				return Announce(now, message);

			var managing = slowestSpeedPercent > 0 ? $" (it is managing about {slowestSpeedPercent}%)" : "";
			TellPrivately(now, slowestPlayer.Value,
				$"{Flag(slowestPlayer.Value)} the game is slowed to {speedPercent}% because your computer is not keeping up{managing}.{Advice}");
			return Announce(now, message);
		}

		/// <summary>The opening of a private line: the player's own name, in capitals, is the one word that cuts through.</summary>
		string Flag(int client) => $">>> {describeClient(client).ToUpperInvariant()}, THIS IS ABOUT YOU:";

		string Advice =>
			(fastGameSpeed ? " At this game speed, VSync alone causes this on a 60 Hz monitor." : "") +
			" Settings > Display: untick \"Enable VSync\", tick \"Limit framerate to game tick rate\"; close other programs.";

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

			pendingPrivate.Enqueue((now + PrivateDelay, toldClient, $">>> {describeClient(toldClient).ToUpperInvariant()}: your computer is keeping up again."));
			toldClient = -1;
		}

		Announcement Announce(long now, string message)
		{
			announced = true;
			lastAnnounceTime = now;
			return new Announcement(message);
		}
	}
}
