#!/usr/bin/env bash
# Recompute [Unreleased] against current origin/main and push it, retrying while
# main moves underneath us (#2630). Extracted from sync-changelog-unreleased.yml
# so the retry can be tested against a real moving repository (#3231) -- inline
# in YAML it was reachable only by merging and waiting for a three-way merge race.
#
# Each attempt must START FROM A CLEAN TREE, which is the whole of #3231.
# generate_changelog.py --unreleased writes CHANGELOG.md in place, so by the time
# any attempt reaches `checkout -B`, that write is sitting unreconciled in the
# working tree -- put there by the workflow's own preceding "Recompute" step
# before attempt 1, and by the previous iteration after that. `git checkout -B`
# refuses to overwrite it whenever origin/main carries a different CHANGELOG.md:
#
#     error: Your local changes to the following files would be overwritten by
#     checkout: CHANGELOG.md
#
# which is exactly the case the loop exists for -- another run won the race and
# pushed its own CHANGELOG. Under `set -e` that ends the step, so the retries
# never happen and the "main moved 5 times running" error below is unreachable.
# Measured in run 34053605452: the step failed 0.6s in, having printed no
# "attempt N of 5" line at all -- attempt 1, not attempt 2.
#
# -f is what makes the tree clean, and it is deliberately narrower than the
# alternatives. `git checkout -- CHANGELOG.md` hardcodes the filename and would
# silently stop working if the generator ever wrote a second file; `git reset
# --hard` discards unconditionally and is the sibling of the footgun in #2203.
# -f discards local modifications only for the paths the checkout must update,
# names no file, and leaves untracked files alone.
#
# Recompute against origin/main rather than rebasing the commit: the section is
# derived from `git describe --tags`..HEAD, so what it should say depends on
# which commits are on main NOW, not which were there when this run was queued.
set -uo pipefail

attempts="${SYNC_CHANGELOG_ATTEMPTS:-5}"

for attempt in $(seq 1 "$attempts"); do
  # Each of these must still be fatal: the loop retries a LOST PUSH RACE, and
  # nothing else. A fetch or checkout that fails is a broken checkout, and
  # retrying it four more times just hides the reason.
  git fetch origin main || exit 1
  git checkout -f -B sync-retry origin/main || exit 1
  out=$(python3 .github/scripts/generate_changelog.py --unreleased) || exit 1
  echo "$out"
  case "$out" in
    *changed=true*) ;;
    *)
      echo "nothing left to write against $(git rev-parse --short origin/main) — another run got there first"
      exit 0
      ;;
  esac
  git add CHANGELOG.md || exit 1
  # changed=true means the regenerated text differs from what is on disk, and
  # the checkout above put origin/main's copy there -- so the index genuinely
  # differs from HEAD and an empty-index commit failure is not reachable here.
  git commit -m "chore: update changelog [skip ci]" || exit 1
  if git push origin HEAD:main; then
    exit 0
  fi
  echo "main moved while pushing; recomputing (attempt $attempt of $attempts)"
done
echo "::error::main moved under this job $attempts times running — something is pushing to main continuously"
exit 1
