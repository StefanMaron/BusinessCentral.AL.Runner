#!/bin/bash
# status of a run: st.sh <label>
R="$(dirname "$0")/runs/$1"
ls "$R"
echo "rc=$(cat "$R/rc.txt" 2>/dev/null) elapsed=$(cat "$R/elapsed.txt" 2>/dev/null)"
tail -c "${2:-800}" "$R/run.log" 2>/dev/null
cat "$R/summary.txt" 2>/dev/null
free -g | head -2
