#!/bin/bash
# Builds the test harness with csc against .NET 8 reference assemblies + the release OpenRA.Game.dll (no NuGet needed).
set -e
cd "$(dirname "$0")"
REL=/home/claude/release/squashfs-root/usr/lib/openra
REF=/usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.31/ref/net8.0
mkdir -p out
for d in OpenRA.Game.dll Linguini.Bundle.dll Linguini.Shared.dll Linguini.Syntax.dll Eluant.dll ICSharpCode.SharpZipLib.dll Mono.Nat.dll Microsoft.Extensions.DependencyModel.dll; do cp -u "$REL/$d" out/; done
RSP=$(mktemp)
{
  echo "-nologo -target:exe -langversion:latest -unsafe+ -optimize+ -nostdlib+ -noconfig -nullable:disable -out:out/NetHarness.dll"
  for f in "$REF"/*.dll; do echo "-r:$f"; done
  echo "-r:out/OpenRA.Game.dll"
  ls src/*.cs
} > "$RSP"
dotnet /usr/lib/dotnet/sdk/8.0.131/Roslyn/bincore/csc.dll @"$RSP"
rm -f "$RSP"
cat > out/NetHarness.runtimeconfig.json <<JSON
{ "runtimeOptions": { "tfm": "net8.0", "rollForward": "Major", "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" } } }
JSON
