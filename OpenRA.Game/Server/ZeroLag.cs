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
using System.Linq;
using System.Reflection;

namespace OpenRA.Server
{
	public static class ZeroLag
	{
		/// <summary>The ZeroLag version this server was built from, as stamped by the build ("unknown" if it was not).</summary>
		public static readonly string Version = typeof(ZeroLag).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "ZeroLagVersion")?.Value ?? "unknown";

		/// <summary>The settings that change what the server log means, for the top of the log.</summary>
		public static string DescribeSettings(ServerSettings s) =>
			$"Netcode={s.Netcode} MaxPlayerLag={s.MaxPlayerLag} MinGameSpeed={s.MinGameSpeed} MaxPlayerBuffer={s.MaxPlayerBuffer} " +
			$"MaxCatchUpSpeed={s.MaxCatchUpSpeed} MaxWaitForStalledPlayer={s.MaxWaitForStalledPlayer} StartDelay={s.StartDelay} " +
			$"AnnounceGameSpeed={s.AnnounceGameSpeed} AnnounceSpeedChanges={s.AnnounceSpeedChanges} NameSlowestPlayer={s.NameSlowestPlayer} " +
			$"VoteKickSlowest={s.VoteKickSlowest} EnableVoteKick={s.EnableVoteKick} ZeroLagNotice={s.ZeroLagNotice} " +
			$"LobbyOptions={string.Join(",", s.LobbyOptions)} MinGameSpeedByPlayers={string.Join(",", s.MinGameSpeedByPlayers)}";

		/// <summary>
		/// Server.MinGameSpeedByPlayers ("players:percent" entries; "players=percent" is accepted too) as (players, percent)
		/// pairs sorted by players. Malformed entries are skipped and logged.
		/// </summary>
		public static (int Players, int Percent)[] ParseFloorTable(IEnumerable<string> entries)
		{
			var table = new List<(int, int)>();
			foreach (var entry in entries ?? Enumerable.Empty<string>())
			{
				var parts = entry.Split(new[] { ':', '=' }, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 2 && int.TryParse(parts[0], out var players) && int.TryParse(parts[1], out var percent)
					&& players > 0 && percent >= 0 && percent <= 100)
					table.Add((players, percent));
				else
					Log.Write("server", $"Server.MinGameSpeedByPlayers: cannot read \"{entry}\" (expected players:percent); ignored.");
			}

			return table.OrderBy(t => t.Item1).ToArray();
		}

		/// <summary>The floor (percent of normal speed; 0 = none) for a game of the given number of players.</summary>
		public static int FloorFor(int players, int minGameSpeed, (int Players, int Percent)[] table)
		{
			var floor = minGameSpeed;
			foreach (var (count, percent) in table)
				if (players >= count)
					floor = percent;

			return floor;
		}

		/// <summary>
		/// Server.LobbyOptions ("id=value" entries; "id:value" and "id value" are accepted too) as option id and value.
		/// Boolean words are normalised to the "True"/"False" a lobby option stores; other values are passed as written.
		/// Malformed entries are skipped and logged; "none" (the way to switch the default preferences off) is skipped silently.
		/// </summary>
		public static IEnumerable<KeyValuePair<string, string>> ParseLobbyOptions(IEnumerable<string> entries)
		{
			foreach (var entry in entries ?? Enumerable.Empty<string>())
			{
				if (string.Equals(entry.Trim(), "none", StringComparison.OrdinalIgnoreCase))
					continue;

				var parts = entry.Split(new[] { '=', ':', ' ' }, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
				{
					Log.Write("server", $"Server.LobbyOptions: cannot read \"{entry}\" (expected id=value); ignored.");
					continue;
				}

				var value = parts[1].ToLowerInvariant() switch
				{
					"true" or "on" or "yes" or "enabled" or "1" => "True",
					"false" or "off" or "no" or "disabled" or "0" => "False",
					_ => parts[1],
				};

				yield return new KeyValuePair<string, string>(parts[0].ToLowerInvariant(), value);
			}
		}
	}
}
