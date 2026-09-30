#!/bin/bash
# Selected-only runs with probe 1 installed: by test (47), by codeunit (275).
D="$(dirname "$0")"
AL_RUNNER_EXACT_TESTS="$D/sel-p1-test.txt" "$D/runcli.sh" cli-sel-p1-test "$D/buckets/Tests-SMB" "$D/probes/Probe1"
AL_RUNNER_EXACT_TESTS="$D/sel-p1-cu.txt" "$D/runcli.sh" cli-sel-p1-cu "$D/buckets/Tests-SMB" "$D/probes/Probe1"
echo done > "$D/selruns.done"
