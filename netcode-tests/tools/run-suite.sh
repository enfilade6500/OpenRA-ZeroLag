#!/bin/bash
# run-suite.sh <variant> <label> <duration> <seed> <baseport> scenarios...
V=$1; L=$2; D=$3; SEED=$4; P=$5; shift 5
for s in "$@"; do
  /home/claude/tools/run-scenario.sh $V $s $D $SEED $P $L > /home/claude/results/$L-$s-$SEED.txt 2>&1
  P=$((P+1))
done
