#!/usr/bin/env bash
# Flags a PR that declares "Closes #N" while its own body routes remaining
# work AT issue N -- the scope-mismatch direction of the closing-reference
# family (#4293).
#
# check_closing_reference.sh owns whether a reference FIRES: missing (#2121),
# unintended (#2128), or a branch declaring nothing (#3678). This script owns
# a reference that fires exactly as intended, on an issue the PR only partly
# addresses. "Closes #N" is binary; scope is not. The cost is asymmetric --
# nobody notices, because a closed issue looks finished.
#
# Two measured instances, two days apart, the second to the issue filed about
# the first:
#
#   #4253 closed #4249, deferring two items to "#4249's own follow-up" -- a
#     follow-up that did not exist and, once #4249 closed, could not. The
#     orphaned items were re-homed as #4255.
#   #4256 closed #4255 while its body said the remaining items "stay on #4255
#     for a follow-up", and its own landed doc comment said it "does NOT close
#     reachability in general". Re-homed a second time, as #4292.
#
# WHAT THIS KEYS ON, and why it is this narrow.
#
# The detectable shape is not "the body hedges" -- bodies here discuss what
# they did not do constantly, and this repository's own rules REQUIRE that:
# batch-sibling-issues-by-file.md point 5 asks authors to say what they did
# not fold, "even when nothing folds". A cue-phrase gate would fire on nearly
# every PR. Measured against the 200 most recently merged PRs (#4017-#4389,
# 164 of which declare a closing reference): a paragraph-window check for
# "not folded" / "deferred to" / "belongs in ... follow-up" co-occurring with
# a declared target flagged 12 PRs, of which 10 were the ordinary and correct
# queue-scan paragraph -- deferring a DIFFERENT, separately-numbered issue.
#
# What separates the two real instances from those 10 is the DESTINATION of
# the deferral. In all 10 false positives the work is routed at another issue
# (#4365, #4325, #4301, #4121, #2218 ...), which is a home; the closed number
# merely appears nearby as scan prose. In both true positives the work is
# routed at the closed issue ITSELF -- "#4249's own follow-up", "#4255's
# part 2", "stay on #4255" -- which is precisely the state that cannot exist
# after the merge, because N is what just closed.
#
# So the rule is: a possessive or destination construction whose target
# number is one this PR declares Closes on. That is a statement the author
# has already made in their own words; the script only notices that its
# destination is about to be shut.
#
# THE EXEMPTION, and why it is load-bearing. PR #4291 deferred the same two
# items, closed the same #4255, and was RIGHT: it filed #4292 as their home
# 42 minutes before merging, and said so. A gate that reds that punishes the
# one author who handled this correctly, and would teach the rest to delete
# the sentence rather than file the issue -- which is the silent direction.
# So a body that names a DIFFERENT issue number as the destination passes:
# the work is homed, whatever else the paragraph says.
#
# Inputs (environment variables):
#   PR_BODY - the pull request's body (required, may be empty)
#
# Exit codes (guards-need-a-third-state.md):
#   0 - measured, and fine (no routed-at-a-closing-target deferral)
#   1 - measured, and broken (a deferral routed at an issue this PR closes)
#   3 - could not measure (PR_BODY unset)

set -uo pipefail

if [ -z "${PR_BODY+x}" ]; then
  echo "::error::PR_BODY is unset, so this check read nothing. It is not reporting success: a body it never saw cannot be judged, and answering 0 here would pass every pull request in the repository. Set PR_BODY (it may legitimately be an empty string)." >&2
  exit 3
fi

KEYWORDS='close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved'
REF_HASH='(?:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)?#[0-9]+'
REF_URL='https?://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/(?:issues|pull)/[0-9]+'
REF="(?:${REF_HASH}|${REF_URL})"
SEP='[[:space:]]*[,;:]?[[:space:]]*'
CANONICAL_LINE_RE="^[[:space:]]*(?:${KEYWORDS})${SEP}${REF}[[:space:]]*[.]?[[:space:]]*\$"

extract_number() {
  printf '%s' "$1" | command grep -oE '[0-9]+' | tail -1
}

# --- Pass 1: the PR's declared canonical targets ----------------------------
#
# Deliberately the same canonical-line shape check_closing_reference.sh
# accepts, so the two gates cannot disagree about what a body declared. Only
# canonical lines count: a stray inline closing keyword is that script's
# failure to report, and reading it here would make this script fire a second
# error about the same defect.
declared_targets=""
while IFS= read -r line; do
  if printf '%s' "$line" | command grep -qiP "$CANONICAL_LINE_RE"; then
    match=$(printf '%s' "$line" | command grep -oiP "(?:${KEYWORDS})${SEP}${REF}")
    num=$(extract_number "$match")
    [ -n "$num" ] && declared_targets="$declared_targets $num"
  fi
done <<< "$PR_BODY"

if [ -z "$declared_targets" ]; then
  echo "No canonical closing target declared; a deferral cannot orphan work on an issue this PR does not close."
  exit 0
fi

is_declared() {
  local n="$1" t
  for t in $declared_targets; do
    [ "$t" = "$n" ] && return 0
  done
  return 1
}

# --- Pass 2: deferrals routed AT a number -----------------------------------
#
# Each pattern captures the destination issue number. They are the
# constructions measured in the real instances plus their immediate
# inflections; a construction not listed here is not detected, which is
# stated plainly in the rule rather than implied to be covered.
#
#   possessive      "#4249's own follow-up", "#4255's part 2"
#   destination     "stay on #4255", "belong in #4249", "deferred to #N"
#
# The possessive arm requires a follow-up/part/remainder noun within a short
# window: "#4255's part 2" routes work, while "#4255's body says ..." is a
# citation and must not fire.
# The possessive arm requires a ROUTING verb before the possessive -- belong,
# stay, remain, leave, defer, go. Measured on #4176, a false positive of the
# bare possessive form: "This is #3482's second half" uses a COPULA, and it
# asserts the opposite of a deferral -- the PR *is* the remainder, completing
# the issue. #3482 closed once and was never reopened, so the close was
# correct and a gate reding it would be wrong. A possessive noun phrase is
# only a destination when something is being sent to it.
ROUTED_RES=(
  "\\b(?:belongs?|belonged|stays?|stayed|remains?|remained|leave|leaves|left|defer(?:red|s|ring)?|goes|go|going|pushed|moved|land(?:s|ed)?)\\b[^.\\n]{0,40}?#([0-9]+)'s\\b[^.\\n]{0,80}?(?:follow-?ups?|part[[:space:]]+[0-9]|remaining|remainder|rest\\b|other half|second half|own issue)"
  "#([0-9]+)'s\\b[^.\\n]{0,40}?(?:follow-?ups?|part[[:space:]]+[0-9]|remainder|other half|second half|own issue)[^.\\n]{0,60}?\\b(?:stays?|stayed|remains?|remained|is[[:space:]]+deferred|are[[:space:]]+deferred|is[[:space:]]+left|are[[:space:]]+left|is[[:space:]]+not[[:space:]]+folded|are[[:space:]]+not[[:space:]]+folded)\\b"
  "\\b(?:stay|stays|stayed|remain|remains|remained|left|lives|live|sit|sits)\\b[[:space:]]+(?:on|in|with|at)[[:space:]]+#([0-9]+)"
  "\\bbelongs?\\b[[:space:]]+(?:in|on|to|with)[[:space:]]+#([0-9]+)"
  "\\bdefer(?:red|s|ring)?\\b[[:space:]]+(?:to|onto|into)[[:space:]]+#([0-9]+)"
)

hit_number=""
hit_text=""
for rx in "${ROUTED_RES[@]}"; do
  matches=$(printf '%s' "$PR_BODY" | command grep -oiP "$rx" || true)
  [ -n "$matches" ] || continue
  while IFS= read -r m; do
    [ -n "$m" ] || continue
    # tail, not head: every ROUTED_RES arm ends at its destination number,
    # and an arm beginning with a routing verb can span an EARLIER "#N".
    # Taking the first would report the wrong issue, sending the author to
    # the wrong remedy -- and on the exemption side below the same slip
    # REFUSED a correctly-homed body, the false-positive direction.
    #
    # Checkable example, which is the point of quoting one. Against a body
    # declaring "Closes #500":
    #
    #   Item two belongs, per #12's triage, in #500's own follow-up.
    #
    # matches "belongs, per #12's triage, in #500's own follow-up", spanning
    # BOTH numbers: tail -> 500 (declared, so exit 1, correct) and head -> 12
    # (not declared, so exit 0 -- the gate goes silent on a real orphaning).
    #
    # Trap for a later editor: the example this replaced --
    # "The rest of #12's work belongs in #500's follow-up" -- is exit 1 under
    # BOTH, because the match starts at "belongs" and #12 falls outside the
    # span. Try that one and the two forms look identical, which is an
    # argument for "simplifying" this line. Any replacement example must be
    # one where the routing verb PRECEDES the foreign number.
    num=$(printf '%s' "$m" | command grep -oP '#\K[0-9]+' | tail -1)
    [ -n "$num" ] || continue
    if is_declared "$num"; then
      hit_number="$num"
      hit_text="$m"
      break
    fi
  done <<< "$matches"
  [ -n "$hit_number" ] && break
done

if [ -z "$hit_number" ]; then
  echo "Deferred-scope OK: no remaining work is routed at a closing target (declared:$declared_targets)"
  exit 0
fi

# --- Pass 3: the exemption -- has the remainder been GIVEN a home? ----------
#
# #4291's shape, and the reason this pass is not just "does another number
# appear". Measured: matching any non-closing number anywhere in the body
# exempts all three real bodies, including both true positives -- bodies here
# cite dozens of issues as background, so mere presence is not evidence of a
# home. #4253 names #4079/#4239/#4085/#3469 and homed nothing; #4291 names
# #4256 in passing AND filed #4292.
#
# What separates them is an explicit FILING construction pointing at a number
# this PR does not close: "they are filed as #4292 before this merges". That
# is the author asserting the remainder has somewhere to live after the
# merge, which is the behaviour this check exists to produce. A citation
# ("the #3469 precedent") is not that assertion and must not exempt.
# Each arm requires the filing verb to take the number as its DESTINATION --
# "filed as #N", "tracked by #N", "follow-up is #N" -- not merely to occur
# near it. Measured on #4291, whose body contains both "filed about**, one
# cycle later: #4253" (a citation about a past PR) and "filed as **#4292**"
# (the real home). A verb-plus-proximity arm matched the citation first and
# exempted the PR for the wrong reason; only the second is an assertion that
# the remainder has somewhere to live, and exempting on the first is the
# false-negative direction -- it would let an orphaning body pass by merely
# mentioning that something was filed about some other issue.
#
# Markdown emphasis between the preposition and the number is expected
# (**#4292**), so the arms allow non-word padding rather than a bare space.
FILED_RES=(
  "\\b(?:filed|files|filing|opened|opens|raised|re-?homed|split[[:space:]]+out|moved)\\b[[:space:]]*(?:as|to|into|under|onto)\\b[^0-9A-Za-z\\n]{0,12}#([0-9]+)"
  "\\b(?:tracked|tracks|covered|handled|carried)\\b[[:space:]]*(?:by|in|on|under)\\b[^0-9A-Za-z\\n]{0,12}#([0-9]+)"
  "\\b(?:follow-?ups?|remainder|rest|remaining[[:space:]]+(?:work|items?))\\b[^.\\n]{0,40}?\\b(?:is|are|lives?|sits?)\\b[^0-9A-Za-z\\n]{0,12}#([0-9]+)"
)

other_home=""
home_text=""
for rx in "${FILED_RES[@]}"; do
  fmatches=$(printf '%s' "$PR_BODY" | command grep -oiP "$rx" || true)
  [ -n "$fmatches" ] || continue
  while IFS= read -r fm; do
    [ -n "$fm" ] || continue
    fnum=$(printf '%s' "$fm" | command grep -oP '#\K[0-9]+' | tail -1)
    [ -n "$fnum" ] || continue
    if ! is_declared "$fnum"; then
      other_home="$fnum"
      home_text="$fm"
      break
    fi
  done <<< "$fmatches"
  [ -n "$other_home" ] && break
done

if [ -n "$other_home" ]; then
  echo "Deferred work is routed at closing target #$hit_number (\"$hit_text\"), but the body files the remainder as #$other_home (\"$home_text\") -- the remainder has a home that survives this merge (the #4291 shape)."
  exit 0
fi

echo "::error::This PR declares 'Closes #$hit_number' while its body routes remaining work AT issue $hit_number: \"$hit_text\". Issue $hit_number is what closes on merge, so that destination will not exist -- the deferred work is orphaned, and nobody notices, because a closed issue looks finished. This happened twice in two days: #4253 closed #4249 deferring to \"#4249's own follow-up\" (re-homed as #4255), and #4256 closed #4255 deferring to \"#4255's part 2\" (re-homed as #4292). Pick one of three: (a) file a follow-up issue for the remainder and name it in the body -- the #4291 shape, and the one that makes this check pass; (b) replace the 'Closes #$hit_number' line with 'Part of #$hit_number', which lands what you landed and puts the issue back on the ready queue instead of closing it; or (c) fold the remaining work into this PR, with its own RED->GREEN. Do not simply delete the sentence: it is the only record that the work exists." >&2
exit 1
