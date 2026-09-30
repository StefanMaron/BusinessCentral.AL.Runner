#!/bin/bash
# usage: runcli.sh <label> <bundle>...
cd /home/stefan/.cache/claude-tmp/claude-1000/-home-stefan-Documents-Repos-community-BusinessCentral-AL-Runner/776ba642-1ff3-4341-80b6-57fb4292737b/scratchpad
label=$1; shift
out=/home/stefan/.cache/claude-tmp/claude-1000/-home-stefan-Documents-Repos-community-BusinessCentral-AL-Runner/776ba642-1ff3-4341-80b6-57fb4292737b/scratchpad/runs/$label; mkdir -p $out
export AL_RUNNER_EMIT_TIMEOUT_SEC=3600 DOTNET_GCHeapCount=2
start=$(date +%s)
exec_runner() { :; }; /home/stefan/Documents/Repos/community/BusinessCentral.AL.Runner/.claude/worktrees/agent-a49b61abb5ee11c8c/AlRunner/bin/Release/net8.0/al-runner "$@" --bc-version 28.4.53241.53955 \
  --package-cache $HOME/.al-runner/platform-apps-28.4.53241.53955 --package-cache $HOME/.al-runner/test-apps-28.4.53241.53955 \
  --cache /home/stefan/.cache/claude-tmp/claude-1000/-home-stefan-Documents-Repos-community-BusinessCentral-AL-Runner/776ba642-1ff3-4341-80b6-57fb4292737b/scratchpad/cache --test-timeout 300 --output-junit $out/junit.xml --out $out/results.json --quiet \
  --test-data=$HOME/.al-runner/test-data/28.4.53241.54318/BusinessCentral-W1.bak --test-data-company "CRONUS International Ltd_" > $out/run.log 2>&1
echo $? > $out/rc.txt
echo $(( $(date +%s) - start )) > $out/elapsed.txt
