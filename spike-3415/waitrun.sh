#!/bin/bash
# waitrun.sh <label> [max-seconds]: wait until rc.txt holds something other than 127/absent, max 580 s.
R="$(dirname "$0")/runs/$1"
end=$(( $(date +%s) + ${2:-580} ))
while [ "$(date +%s)" -lt "$end" ]; do
  rc=$(cat "$R/rc.txt" 2>/dev/null)
  if [ -n "$rc" ] && [ "$rc" != "127" ] && [ "$rc" != "running" ]; then echo "DONE rc=$rc"; exit 0; fi
  if [ -f "$R/summary.txt" ] && command grep -q peakRSS "$R/summary.txt"; then echo "DONE (server)"; exit 0; fi
  sleep 5
done
echo "STILL RUNNING"
