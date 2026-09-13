#!/usr/bin/env bash
# Tests for sync_changelog_push.sh -- the CHANGELOG sync's push-retry loop (#3231).
#
# The property under test is not "the script mentions -f". It is: WITH A DIRTY
# CHANGELOG.md IN THE TREE AND origin/main CARRYING A DIFFERENT ONE, the loop
# still reaches a push. That is the exact state run 34053605452 died in, and it
# is reproducible here in a scratch repository in well under a second -- no
# GitHub, no merge wave, no waiting for three PRs to land 6 seconds apart.
#
# Every case below drives the REAL script against real git remotes. The
# generator is stubbed (PATH shim) so the tests are about the retry loop's
# tree handling rather than about changelog formatting, which
# test_generate_changelog.py already covers.
#
# Run directly: bash .github/scripts/test_sync_changelog_push.sh

set -uo pipefail

# Scratch commits must not reach the user's global/system config: a signing box
# otherwise fails or blocks on every commit below (#4001).
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null GIT_CONFIG_NOSYSTEM=1
unset GIT_CONFIG_PARAMETERS GIT_CONFIG_COUNT

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/sync_changelog_push.sh"
WORKFLOW="$(cd "$SCRIPT_DIR/../workflows" && pwd)/sync-changelog-unreleased.yml"

pass=0
fail=0
ok()  { echo "ok   - $1"; pass=$((pass + 1)); }
bad() { echo "FAIL - $1"; fail=$((fail + 1)); }

check_eq() {
  local desc="$1" expected="$2" got="$3"
  if [ "$expected" = "$got" ]; then ok "$desc"; else
    bad "$desc: expected '$expected', got '$got'"
  fi
}

# --- Scaffolding -------------------------------------------------------------
#
# A bare "origin" plus a clone standing in for the runner's checkout, and a
# second clone standing in for whichever OTHER sync run wins the race. The
# generator is a stub on PATH that writes a caller-controlled line into
# CHANGELOG.md and reports changed=true/false the way the real one does.

ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

# $1 = scenario dir. Builds origin + work + rival, all on main.
build_repo() {
  local d="$ROOT/$1"
  mkdir -p "$d"
  git init -q --bare "$d/origin"

  git clone -q "$d/origin" "$d/work" 2>/dev/null
  git -C "$d/work" config user.email test@example.com
  git -C "$d/work" config user.name Test
  git -C "$d/work" checkout -q -b main
  printf '# Changelog\n\n## [Unreleased]\n\nbase\n' > "$d/work/CHANGELOG.md"
  git -C "$d/work" add -A
  git -C "$d/work" commit -qm "base"
  git -C "$d/work" push -q -u origin main

  git clone -q "$d/origin" "$d/rival" 2>/dev/null
  git -C "$d/rival" config user.email rival@example.com
  git -C "$d/rival" config user.name Rival
  echo "$d"
}

# A stub generate_changelog.py. Writes $STUB_TEXT into CHANGELOG.md and prints
# changed=true when that differs from what is already on disk -- the real
# script's own contract (update_unreleased returns False when updated ==
# content), which is what makes an empty-index commit unreachable.
install_stub() {
  local d="$1"
  mkdir -p "$d/bin" "$d/work/.github/scripts"
  cat > "$d/work/.github/scripts/generate_changelog.py" <<'STUB'
import os, sys
text = os.environ.get('STUB_TEXT', 'generated')
path = 'CHANGELOG.md'
old = open(path).read() if os.path.exists(path) else ''
new = '# Changelog\n\n## [Unreleased]\n\n' + text + '\n'
if new == old:
    print('changed=false')
else:
    open(path, 'w').write(new)
    print('changed=true')
STUB
}

# Runs the script inside $d/work with the stub in place.
run_sync() {
  local d="$1"
  ( cd "$d/work" && STUB_TEXT="${STUB_TEXT:-mine}" \
      SYNC_CHANGELOG_ATTEMPTS="${SYNC_CHANGELOG_ATTEMPTS:-5}" \
      bash "$SCRIPT" 2>&1 )
}

# Makes the rival clone push a different CHANGELOG.md onto origin/main.
rival_pushes() {
  local d="$1" text="$2"
  git -C "$d/rival" fetch -q origin main
  git -C "$d/rival" checkout -qf -B main origin/main
  printf '# Changelog\n\n## [Unreleased]\n\n%s\n' "$text" > "$d/rival/CHANGELOG.md"
  git -C "$d/rival" add -A
  git -C "$d/rival" commit -qm "rival"
  git -C "$d/rival" push -q origin main
}

# --- 1. THE REGRESSION: a dirty tree + a moved origin/main -------------------
#
# This is run 34053605452 exactly. The workflow's "Recompute" step has already
# written CHANGELOG.md, and while this job sat in the Actions queue another run
# pushed a DIFFERENT CHANGELOG.md. Before #3231 the first `checkout -B` refused
# and `bash -e` ended the step 0.6s in.

d="$(build_repo dirty-and-moved)"
install_stub "$d"
rival_pushes "$d" "rival-content"
# the tree is dirty exactly as the preceding workflow step leaves it
printf '# Changelog\n\n## [Unreleased]\n\nstale-local-write\n' > "$d/work/CHANGELOG.md"

out="$(STUB_TEXT=mine run_sync "$d")"; rc=$?
check_eq "a dirty tree against a moved origin/main still pushes" 0 "$rc"

if printf '%s' "$out" | command grep -q "would be overwritten by checkout"; then
  bad "the checkout refused -- this is the #3231 regression"
else
  ok "the checkout is not refused by the stale local write"
fi

# Read the last non-blank line: the stub writes the section body there, and a
# positional tail would pick up the trailing blank line instead.
pushed="$(git -C "$d/origin" show main:CHANGELOG.md | command grep -v '^$' | tail -1)"
check_eq "origin/main ends up carrying this run's recomputed section" "mine" "$pushed"

# The rival's commit must still be an ancestor: the fix may not discard
# somebody else's merged work to clean the tree (#2203's failure mode).
if git -C "$d/work" merge-base --is-ancestor \
     "$(git -C "$d/rival" rev-parse HEAD)" "$(git -C "$d/origin" rev-parse main)"; then
  ok "the rival's commit survives -- nothing was reverted to clean the tree"
else
  bad "the rival's commit was discarded"
fi

# --- 2. A LOST PUSH RACE, which is what the retry is FOR ---------------------
#
# Attempt 1 computes against origin/main, and the rival pushes in between the
# computation and the push, so the push is rejected. Attempt 2 must recover --
# and it starts from a tree the first attempt dirtied.

d="$(build_repo lost-race)"
install_stub "$d"
# A pre-push hook on the bare repo rejects exactly the first push attempt, then
# lets the rival's own push and the retry through.
# A pre-receive hook on the bare origin rejects the FIRST push only. The marker
# lives in the bare repo, so it survives between the two push processes; no
# environment needs to reach the hook, which a local-path push would not carry.
mkdir -p "$d/origin/hooks"
cat > "$d/origin/hooks/pre-receive" <<'HOOK'
#!/usr/bin/env bash
marker="$(dirname "$0")/../rejected-once"
if [ ! -f "$marker" ]; then
  touch "$marker"
  echo "simulated: main moved while pushing" >&2
  exit 1
fi
exit 0
HOOK
chmod +x "$d/origin/hooks/pre-receive"

printf '# Changelog\n\n## [Unreleased]\n\nstale-local-write\n' > "$d/work/CHANGELOG.md"
out="$( cd "$d/work" && STUB_TEXT=mine bash "$SCRIPT" 2>&1 )"
rc=$?
check_eq "a rejected first push is retried to success" 0 "$rc"
if printf '%s' "$out" | command grep -q "attempt 1 of"; then
  ok "the retry actually ran a second iteration"
else
  bad "no second iteration happened: $(printf '%s' "$out" | tail -3 | tr '\n' ' ')"
fi
if printf '%s' "$out" | command grep -q "would be overwritten by checkout"; then
  bad "iteration 2's checkout was refused by iteration 1's write"
else
  ok "iteration 2 starts from a clean tree"
fi

# --- 3. Another run got there first: nothing left to write -------------------
#
# The rival pushes CONTENT IDENTICAL to what this run would compute. The
# generator reports changed=false and the loop exits 0 without committing.

d="$(build_repo already-done)"
install_stub "$d"
rival_pushes "$d" "mine"
printf '# Changelog\n\n## [Unreleased]\n\nstale-local-write\n' > "$d/work/CHANGELOG.md"
before="$(git -C "$d/origin" rev-parse main)"
out="$(STUB_TEXT=mine run_sync "$d")"; rc=$?
check_eq "nothing left to write exits 0" 0 "$rc"
check_eq "and pushes no commit" "$before" "$(git -C "$d/origin" rev-parse main)"
if printf '%s' "$out" | command grep -q "another run got there first"; then
  ok "it says why it stopped"
else
  bad "no explanation printed"
fi

# --- 4. Exhausting the retries is still a loud failure -----------------------
#
# guards-need-a-third-state.md: the give-up path must be reachable and must
# fail, not quietly succeed. Before #3231 it was unreachable, because the
# checkout killed the step first.

d="$(build_repo exhausted)"
install_stub "$d"
cat > "$d/origin/hooks/pre-receive" <<'HOOK'
#!/usr/bin/env bash
echo "simulated: main always moves" >&2
exit 1
HOOK
chmod +x "$d/origin/hooks/pre-receive"
printf '# Changelog\n\n## [Unreleased]\n\nstale-local-write\n' > "$d/work/CHANGELOG.md"
out="$( cd "$d/work" && STUB_TEXT=mine SYNC_CHANGELOG_ATTEMPTS=3 bash "$SCRIPT" 2>&1 )"
rc=$?
check_eq "exhausting every attempt exits 1" 1 "$rc"
if printf '%s' "$out" | command grep -q "::error::main moved under this job 3 times"; then
  ok "the give-up path is reachable and loud"
else
  bad "the give-up error never printed"
fi
if [ "$(printf '%s' "$out" | command grep -c "attempt . of 3")" = "3" ]; then
  ok "all 3 attempts ran"
else
  bad "expected 3 attempts, got $(printf '%s' "$out" | command grep -c "attempt . of 3")"
fi

# --- 5. The workflow calls the script ----------------------------------------
#
# Extracting the loop is only a fix if the workflow actually runs the extracted
# copy; otherwise the tests above grade a file nothing executes.

if command grep -q "sync_changelog_push.sh" "$WORKFLOW"; then
  ok "the workflow invokes sync_changelog_push.sh"
else
  bad "the workflow does not invoke sync_changelog_push.sh"
fi
if command grep -qE '^\s+git checkout -B sync-retry' "$WORKFLOW"; then
  bad "the workflow still carries its own unguarded checkout -B loop"
else
  ok "the workflow no longer carries an inline retry loop"
fi

echo
echo "passed: $pass, failed: $fail"
[ "$fail" -eq 0 ]
