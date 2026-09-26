#!/bin/bash
# Compile OpenRA.Game.dll from a source tree, the way the release does (net6.0 reference assemblies,
# same third-party dependency versions), without NuGet: third-party DLLs are taken from the official
# release-20250330 download, and the .NET 6 reference assemblies from Ubuntu's dotnet-targeting-pack-6.0.
# Usage: build-game.sh <source-root> <output-dir>
set -e
SRC="$1"; OUTDIR="$2"; mkdir -p "$OUTDIR"
REL=/home/claude/release/squashfs-root/usr/lib/openra
REF=/home/claude/netref6/usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/6.0.36/ref/net6.0
RSP=$(mktemp)
AI=$(mktemp --suffix=.cs)
REV=$(git -C "$SRC" rev-parse HEAD 2>/dev/null || echo unknown)
cat > "$AI" <<CS
[assembly: System.Reflection.AssemblyCompany("OpenRA.Game")]
[assembly: System.Reflection.AssemblyProduct("OpenRA")]
[assembly: System.Reflection.AssemblyTitle("OpenRA.Game")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) The OpenRA Developers and Contributors")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.0+$REV")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v6.0", FrameworkDisplayName = "")]
CS
{
  echo "-nologo -target:library -langversion:9 -unsafe+ -optimize+ -nostdlib+"
  echo "-debug:portable -deterministic -nowarn:CS1591,CS8632"
  # Same preprocessor symbols the .NET SDK defines for a net6.0 Release build
  echo "-define:TRACE;RELEASE;NET;NET6_0;NETCOREAPP;NET5_0_OR_GREATER;NET6_0_OR_GREATER;NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;NETCOREAPP3_1_OR_GREATER"
  echo "-out:$OUTDIR/OpenRA.Game.dll"
  for f in "$REF"/*.dll; do echo "-r:$f"; done
  for d in Eluant Linguini.Bundle Linguini.Shared Linguini.Syntax ICSharpCode.SharpZipLib Mono.Nat Microsoft.Extensions.DependencyModel; do echo "-r:$REL/$d.dll"; done
  echo "$AI"
  find "$SRC/OpenRA.Game" -name '*.cs' | sort
} > "$RSP"
dotnet /usr/lib/dotnet/sdk/8.0.131/Roslyn/bincore/csc.dll -noconfig @"$RSP"
rm -f "$RSP" "$AI"
