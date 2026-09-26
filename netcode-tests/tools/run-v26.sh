#!/bin/bash
S="/home/claude/tools/run-suite.sh"
# Regression: same scenarios as v25, default settings (announcements on, naming and vote-kick off)
$S v26 v26 60 1 29900 clean slowlink spikes dropout hitch slowcpu battles slowspec potato outage chaos leave
# The new options
SERVER_ARGS="Server.VoteKickSlowest=True" EXTRA="--votekick 1" $S v26 v26-vote 90 2 29950 votekick
SERVER_ARGS="Server.NameSlowestPlayer=True" $S v26 v26-named 60 1 29960 potato
SERVER_ARGS="Server.AnnounceGameSpeed=False" $S v26 v26-quiet 60 1 29970 potato
SERVER_ARGS="Server.MinGameSpeed=75" $S v26 v26-floor75 60 1 29980 potato
EXTRA="--defeatbit 5 --defeatframe 40" $S v26 v26-defeated 60 1 29990 defeated
echo ALLDONE
