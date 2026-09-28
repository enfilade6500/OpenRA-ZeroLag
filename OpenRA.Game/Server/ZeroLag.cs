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
			$"MaxCatchUpSpeed={s.MaxCatchUpSpeed} MaxWaitForStalledPlayer={s.MaxWaitForStalledPlayer} AnnounceGameSpeed={s.AnnounceGameSpeed} " +
			$"NameSlowestPlayer={s.NameSlowestPlayer} VoteKickSlowest={s.VoteKickSlowest} EnableVoteKick={s.EnableVoteKick} ZeroLagNotice={s.ZeroLagNotice}";
	}
}
