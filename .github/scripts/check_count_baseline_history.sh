#!/usr/bin/env bash
# Requires a PR that bumps the al-language corpus pin to record WHY, in
# tests/expectations/count-baseline/history.md (#3591).
#
# tests/expectations/count-baseline/README.md has required this since the file
# existed: "Bumped the corpus pin -- edit al-language's tests.default to the
# number the run reported, and add an entry to history.md under ## al-language
# saying which upstream PRs came in and that you measured it rather than
# computed it." Nothing enforced it. AlRunner.Tests/CountBaselineMergeShapeTests.cs
# is the only thing that reads history.md, and it validates a section's SHAPE if
# one is written -- so an omission is invisible to it, and nothing in .github/ or
# tools/ looked at the file at all.
#
# PR #3588 is the instance: it bumped the pin, passed all 13 required checks
# green, and had no entry. A reviewer caught it, and it was fixed afterwards by
# 26ddc371.
#
# WHY THE ENTRY IS NOT DECORATION
# -------------------------------
# The reasoning for where a bump STOPPED is the thing the next agent needs, and
# history.md is where they look. On #3588 that reasoning -- corpus #293 brings a
# test the runner fails until #3580 lands -- existed only in the PR body, and a
# PR body is not reachable from a checkout. Six months on, `git log` and the
# working tree are what remain; a review comment is not.
#
# WHAT FIRES IT, AND WHY IT IS THE PIN AND NOT THE COUNT
# ------------------------------------------------------
# The trigger is the tests/al-language gitlink moving. That is narrower than
# "test-count-baseline.json changed", and the narrowing is measured rather than
# preferred. On main at e03dbfc1, of the last 12 commits touching
# test-count-baseline.json, 10 carried a history.md change and 2 did not:
#
#   b071a9d4  fix(query): honour DataItemTableFilter ... (#3630)
#   01338b5e  fix(testpage): report the close BC actually performs ... (#3595)
#
# Neither was a violation. Both added one runner-extras GROUP line
# ("query-dataitem-filter-precompiled-dep", "testpage-close-message-consumed")
# and neither moved the submodule pin. The README asks for a history entry under
# "Bumped the corpus pin"; adding a runner-extras group line is a different row
# of the same table and carries no such requirement, because the group name and
# its count are self-describing in a way a corpus count is not. Firing on those
# would be the noisy direction check_corpus_linkage.sh warns about -- a guard
# that nags on a legitimate shape gets pasted past, and then it protects
# nothing.
#
# In the other direction the rule is absolute, so this guard is too: of the 31
# commits moving the pin since history.md was created (c133aa97, #2879), 31
# carried an entry. There is no observed legitimate silent bump.
#
# The pin firing on its own -- without a baseline edit -- is deliberate. The
# requirement attaches to the bump, not to the count moving, and a bump that
# leaves the count unchanged is the case whose reasoning is LEAST recoverable
# from the diff: nothing in it says how far the pin went or why it stopped
# there.
#
# WHY AN OPT-OUT EXISTS WHEN NOTHING OBSERVED NEEDS ONE
# -----------------------------------------------------
# 31 of 31 says unconditional would be correct today. What argues for the hatch
# is NOT that it is cheap -- "one sentence when it is needed" would justify a
# hatch on any guard at all, so it decides nothing. It is that the expensive
# failure is UNRECOVERABLE BY THE AUTHOR: an unconditional guard meeting a
# legitimate silent bump leaves exactly one way past it, writing a history entry
# that says nothing. That is this guard forcing a junk entry into the very file
# it exists to keep meaningful -- corrupting its own asset -- and the author
# cannot avoid it. A guard whose failure mode degrades the thing it protects
# needs a way out even before anything has needed one.
#
#   Count-Baseline-History-NA: <reason>
#
# Same idiom, and the same mandatory non-placeholder reason, as
# check_corpus_linkage.sh's Corpus-NA and check_closing_reference.sh's
# "No linked issue:" hatch. A bare marker is the same as no guard.
#
# WHAT THIS DOES NOT CHECK
# ------------------------
# Whether the entry is any GOOD. Not whether it names the upstream PRs, not
# whether it is under ## al-language, not whether the number in it matches the
# baseline. Told to judge that, CI gets it wrong in both directions and the
# author writes to the checker instead of to the reader. Shape is
# CountBaselineMergeShapeTests's job; a human reviewer judges content. This asks
# only that there is something for them to read.
#
# Inputs (environment variables, both required, either may be empty):
#   PR_BODY        - the pull request's body/description
#   CHANGED_FILES  - the PR's changed paths, one per line
#
# Exit codes
#   0  no pin bump, or the entry is present, or a well-formed opt-out
#   1  the pin moved and neither an entry nor a usable opt-out is present
#   2  the check could not run (a required input was not passed at all)

set -uo pipefail

# "Unset" and "set but empty" are deliberately different. An empty CHANGED_FILES
# is a legitimate state; an UNSET one means the caller never computed it, and a
# guard handed nothing checks nothing and passes green -- the failure
# pr_commit_messages.sh and check_corpus_linkage.sh both refuse.
if [ -z "${CHANGED_FILES+set}" ]; then
  echo "::error::check_count_baseline_history.sh: CHANGED_FILES is required (it may be empty, but it must be passed)." >&2
  exit 2
fi
if [ -z "${PR_BODY+set}" ]; then
  echo "::error::check_count_baseline_history.sh: PR_BODY is required (it may be empty, but it must be passed)." >&2
  exit 2
fi

PIN_PATH="tests/al-language"
HISTORY_PATH="tests/expectations/count-baseline/history.md"

# --- Did the pin move, and was an entry written? -----------------------------
#
# Exact string equality per line, never a prefix or a substring match.
# tests/al-language is a gitlink, so git names it as exactly that one path and
# never with anything after it: a path UNDER it is either a stale diff or a file
# in some other tree, and a prefix match would read every corpus source file as
# a pin bump. The same equality protects the history path from
# .../history.md.bak and friends.
pin_moved=""
history_written=""

while IFS= read -r f; do
  [ -z "$f" ] && continue
  # Strip a trailing CR, for a list that reached us through a CRLF round trip.
  # Only the CR: pr_changed_files.sh emits bare `git diff --name-only` output,
  # so a leading or trailing SPACE cannot occur in production and trimming one
  # would be untested code guarding an unreachable case.
  f="${f%$'\r'}"
  case "$f" in
    "$PIN_PATH")     pin_moved="1" ;;
    "$HISTORY_PATH") history_written="1" ;;
  esac
done <<< "$CHANGED_FILES"

if [ -z "$pin_moved" ]; then
  echo "This PR does not move the tests/al-language pin, so no history.md entry is required."
  exit 0
fi

if [ -n "$history_written" ]; then
  echo "The corpus pin moves and $HISTORY_PATH carries an entry."
  exit 0
fi

# --- The pin moved with no entry: is there a usable opt-out? -----------------

found_marker=""
found_reason=""

while IFS= read -r line; do
  # The marker and its reason share ONE line, the marker starts the line
  # (leading whitespace aside), and nothing may precede it. That is the
  # canonical-line rule check_closing_reference.sh and check_corpus_linkage.sh
  # both apply, and it is not pedantry: every shape it rejects -- bolded,
  # backticked, mid-sentence, split across two lines -- went red on a real PR
  # for the Corpus-PR marker in a single day (#3330). A declaration has to stand
  # where a reviewer reads it.
  if [[ "$line" =~ ^[[:space:]]*[Cc]ount-[Bb]aseline-[Hh]istory-[Nn][Aa]:[[:space:]]*(.*)$ ]]; then
    found_marker="1"
    reason="${BASH_REMATCH[1]}"
    reason="$(printf '%s' "$reason" | command sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    # A fixed placeholder list, not a quality bar. It catches a marker with
    # nothing behind it and judges no prose.
    case "$(printf '%s' "$reason" | tr '[:upper:]' '[:lower:]')" in
      ""|"n/a"|"na"|"none"|"no"|"-"|"--"|"."|"?"|"tbd"|"todo"|"x") ;;
      *) found_reason="1" ;;
    esac
  fi
done <<< "$PR_BODY"

if [ -n "$found_reason" ]; then
  echo "The corpus pin moves with no history.md entry, and the PR body declares why."
  exit 0
fi

REMEDY="Do ONE of these:
  Add an entry to $HISTORY_PATH under '## al-language' -- which upstream PRs came
    in, the count you MEASURED rather than computed, and, if the bump stopped
    short of corpus master, why it stopped where it did. That last part is the
    one the next agent cannot get anywhere else.
  Count-Baseline-History-NA: <reason>
    -- on its own line in the PR body, if this bump genuinely has nothing to
       record. Be specific; the reason is what a reviewer reads. 'n/a' is not one."

if [ -n "$found_marker" ]; then
  echo "::error::This PR moves the corpus pin with no $HISTORY_PATH entry, and its 'Count-Baseline-History-NA:' line carries no usable reason. The reason is the entire point of the opt-out -- a bare marker gets pasted in reflexively, which is the same as having no guard. $REMEDY" >&2
  exit 1
fi

echo "::error::This PR moves the tests/al-language corpus pin and writes no entry in $HISTORY_PATH. tests/expectations/count-baseline/README.md requires one on every pin bump, and nothing else records the reasoning: CountBaselineMergeShapeTests validates a section's shape if one is written, so an omission is invisible to it, and a PR body is not reachable from a checkout. PR #3588 is why this check exists -- it bumped the pin, passed all 13 required checks, and had no entry. $REMEDY" >&2
exit 1
