#!/bin/bash
# run-scenario.sh <variant> <scenario> <duration> <seed> <port> [label]
V=$1; S=$2; D=$3; SEED=$4; PORT=$5; LABEL=${6:-$V}; export EXTRA
RES=/home/claude/results; mkdir -p $RES
/home/claude/tools/run-server.sh $V $PORT $SERVER_ARGS > $RES/server-$LABEL-$S-$SEED.out 2>&1 &
SPID=$!
for i in $(seq 1 100); do python3 -c "import socket;socket.create_connection(('127.0.0.1',$PORT),0.2).close()" 2>/dev/null && break; sleep 0.2; done
sleep 1
dotnet /home/claude/harness/out/NetHarness.dll --port $PORT --scenario $S --duration $D --seed $SEED --label $LABEL $EXTRA --out $RES/$LABEL-$S-$SEED.json
RC=$?
sleep 2   # let the server log the per-player summaries for the disconnecting clients
kill $SPID 2>/dev/null; pkill -f "Server.ListenPort=$PORT" 2>/dev/null; wait $SPID 2>/dev/null
cp /tmp/ora-support-$V-$PORT/Logs/dedicated-server.log $RES/serverlog-$LABEL-$S-$SEED.log 2>/dev/null
exit $RC
