#!/bin/bash
# chain.sh <probe-number>... : run the bucket with each probe app, one after another.
D="$(dirname "$0")"
for n in "$@"; do
  "$D/runcli.sh" "cli-probe$n" "$D/buckets/Tests-SMB" "$D/probes/Probe$n"
done
echo chain-done > "$D/chain-$1.done"
