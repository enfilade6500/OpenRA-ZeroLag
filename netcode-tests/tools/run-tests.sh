#!/bin/bash
# Compile and run the standalone FrameScheduler tests against a built OpenRA.Game.dll.
# Usage: run-tests.sh <dir containing OpenRA.Game.dll>
set -e
BUILD="$1"
REF=/home/claude/netref6/usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/6.0.36/ref/net6.0
T=/home/claude/tests
cp "$BUILD/OpenRA.Game.dll" "$T/OpenRA.Game.dll"
RSP=$(mktemp)
{
  echo "-nologo -target:exe -langversion:9 -nostdlib+ -nowarn:CS8632 -out:$T/tests.dll"
  for f in "$REF"/*.dll; do echo "-r:$f"; done
  echo "-r:$T/OpenRA.Game.dll"
  echo "$T/SchedulerTests.cs"
} > "$RSP"
dotnet /usr/lib/dotnet/sdk/8.0.131/Roslyn/bincore/csc.dll -noconfig @"$RSP"
rm -f "$RSP"
cd "$T" && dotnet tests.dll
