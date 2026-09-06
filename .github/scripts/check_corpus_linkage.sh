#!/usr/bin/env bash
# Requires a PR that could change what AL observes to DECLARE its corpus linkage
# (#3255).
#
# .claude/rules/bc-behavior-tests-go-upstream.md says a test asserting plain BC
# behaviour must live upstream in StefanMaron/BusinessCentral.AL.Language.Tests,
# where a real service tier adjudicates it, or the PR must name a structural
# reason why it cannot. Nothing enforced any part of that: measured on main at
# 65e5e562, pr-check.yml had one job and pr-gate.yml had eight, and none of them
# looked at corpus linkage. Whether a PR honoured the rule was visible only if a
# reviewer read the whole body -- and in one session the rule was invoked
# correctly by three agents and as an excuse by a fourth, told apart only that
# way.
#
# WHAT THIS CHECKS, AND WHAT IT DELIBERATELY DOES NOT
# ---------------------------------------------------
# It checks that the author DECLARED something. It does not check, and must never
# try to check, whether the PR actually asserts BC behaviour, or whether the
# declared reason is a good one. That is a judgement call CI gets wrong in both
# directions: told to decide, it either nags PRs that have nothing to declare
# until people paste past it, or stays silent on the ones that matter. A human
# reviewer makes the judgement; this makes sure there is something for them to
# read, in a fixed place, that survives into the merge commit.
#
# Two accepted declarations, each on its own line in the PR BODY:
#
#   Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/211
#   Corpus-NA: precompiled-dependency path; a corpus test source-compiles and would pass
#
# The reason after Corpus-NA is mandatory and must not be a bare placeholder.
# That is the same idiom as check_closing_reference.sh's "No linked issue:
# <reason>" hatch and it exists for the same reason: a marker with nothing after
# it gets pasted in reflexively, which is indistinguishable from not having the
# guard at all.
#
# WHY A TRAILER RATHER THAN A LABEL
# ---------------------------------
# A label is mutable state that never reaches the merge commit, so `git log`
# cannot answer "what did this PR claim about the corpus" six months later. It is
# also one click, which is precisely the reflexive-paste failure above with less
# friction. And pr-gate.yml already lists labeled/unlabeled in its trigger types
# because a label can change what a guard concludes -- a label-based guard would
# add that re-trigger dependency and gain nothing. The trailer is the idiom this
# repository already uses for exactly this shape of declaration.
#
# WHY THE Corpus-NA REASON IS FREE TEXT RATHER THAN AN ENUM
# ---------------------------------------------------------
# An enum (ci-only | precompiled-dep | runner-specific | ...) is a fixed
# vocabulary the author picks the nearest match from, and the reviewer then reads
# a token with the actual reason discarded. The reason in the example above --
# "precompiled-dependency path; a corpus test source-compiles and would pass" --
# is a real one from this repository and no enum would have carried it. Free text
# that must be non-empty is what makes the author write a sentence.
#
# WHY THE DECLARATION MUST BE IN THE PR BODY
# ------------------------------------------
# Same reasoning as the canonical-line rule in check_closing_reference.sh: the
# body is where a reviewer reads, and an inline mention buried in a sentence is
# prose, not a declaration. A commit message is not scanned -- unlike a closing
# reference, a corpus trailer has no effect GitHub acts on, so there is no
# second route to cover here.
#
# Inputs (environment variables, both required, either may be empty):
#   PR_BODY        - the pull request's body/description
#   CHANGED_FILES  - the PR's changed paths, one per line
#
# Second mode, for the ADVISORY half in pr-check.yml:
#
#   check_corpus_linkage.sh --print-corpus-pr-urls
#
# prints every well-formed Corpus-PR URL declared in PR_BODY, one per line, and
# exits 0 whether or not it found any. It exists so the parsing stays in this
# file, under the unit tests, while the part that cannot be unit-tested -- the
# gh call that resolves the URL -- stays in the workflow. Only PR_BODY is read
# in this mode.
#
# Exit codes
#   0  in scope and declared, or out of scope entirely (or extraction mode)
#   1  in scope and the declaration is missing or malformed
#   2  the check could not run (a required input was not passed at all)

set -uo pipefail

# Deliberately distinguishes "unset" from "set but empty". An empty CHANGED_FILES
# is a legitimate state (nothing to check); an UNSET one means the caller never
# computed it, and a guard handed nothing checks nothing and passes -- the
# green-tick-meaning-nothing-was-read failure pr_commit_messages.sh also refuses.
if [ -z "${PR_BODY+set}" ]; then
  echo "::error::check_corpus_linkage.sh: PR_BODY is required (it may be empty, but it must be passed)." >&2
  exit 2
fi

CORPUS_REPO_URL='https?://github\.com/StefanMaron/BusinessCentral\.AL\.Language\.Tests/pull/[0-9]+/?'

CORPUS_PR_LINE_RE="^[[:space:]]*Corpus-PR:[[:space:]]*${CORPUS_REPO_URL}[[:space:]]*[.]?[[:space:]]*\$"
CORPUS_NA_LINE_RE='^[[:space:]]*Corpus-NA:[[:space:]]*(.*)$'
# Matches the marker whatever follows it, so a malformed Corpus-PR can be
# reported as malformed rather than as absent -- those need different remedies.
CORPUS_PR_MARKER_RE='^[[:space:]]*Corpus-PR:'

# --- Extraction mode (advisory half) -----------------------------------------

if [ "${1:-}" = "--print-corpus-pr-urls" ]; then
  while IFS= read -r line; do
    if printf '%s' "$line" | command grep -qiP "$CORPUS_PR_LINE_RE"; then
      printf '%s' "$line" | command grep -oiP "$CORPUS_REPO_URL"
    fi
  done <<< "$PR_BODY"
  exit 0
fi

if [ -z "${CHANGED_FILES+set}" ]; then
  echo "::error::check_corpus_linkage.sh: CHANGED_FILES is required (it may be empty, but it must be passed)." >&2
  exit 2
fi

# --- Is this diff in scope? --------------------------------------------------
#
# Scoped to paths that could plausibly change what AL observes, and no wider.
# Getting this wrong in the NOISY direction is worse than not shipping the guard:
# a trailer demanded on every CI and tooling PR produces "Corpus-NA: ci only"
# that nobody reads, and trains everyone to paste past it. So the list is derived
# from what this repository actually contains, and each entry earns its place:
#
#   AlRunner/Patches/               -- 144 files, every one of them substituting
#                                      BC method behaviour. This is already the
#                                      audit boundary .claude/rules/loud-failures.md
#                                      draws ("any new patch under AlRunner/Patches/").
#                                      MockTestPage.cs and every virtual table live here.
#   AlRunner/Rewriters/             -- rewrites applied to our own AL output.
#   AlRunner/Infrastructure/NclCecilRewrite*  -- Cecil rewrites of the runtime
#                                      engine's IL. AL-observable by construction,
#                                      and the mechanism that rots silently when a
#                                      BC service update reroutes callers past it.
#   AlRunner/BcCompiler*, BcAssembler.cs      -- the AL compile and emit path;
#                                      changes here change what AL compiles to.
#
# Deliberately OUT, with reasons:
#
#   the rest of AlRunner/Infrastructure/  -- a genuinely mixed directory of ~100
#       files. PhaseLog, ShardPlanner, ParallelFanOut, the backup tooling and the
#       cache layer are plumbing that cannot change an AL-visible answer, and a
#       few files in it (FieldPoke, AlValueCapture) can. A per-file allowlist
#       inside a directory that size would rot silently, and rot in the noisy
#       direction, so the whole directory stays out except the Cecil rewrites.
#       This is the known soft edge of this guard -- see the PR that added it.
#   AlRunner.Tests/, tests/  -- tests and fixtures, including the corpus pin and
#       the expectations manifests. These record behaviour, they do not change it.
#   .github/, tools/, scripts/, docs/, .claude/, top-level docs -- CI, tooling and
#       prose. This is the noise the scoping exists to prevent.
#   *.md anywhere, including inside an in-scope directory -- a doc file is a doc
#       file wherever it sits. require-tests.yml draws the same line.
in_scope_file=""
while IFS= read -r f; do
  [ -z "$f" ] && continue
  case "$f" in
    *.md) continue ;;
  esac
  case "$f" in
    AlRunner/Patches/*|\
    AlRunner/Rewriters/*|\
    AlRunner/Infrastructure/NclCecilRewrite*|\
    AlRunner/BcCompiler*|\
    AlRunner/BcAssembler.cs)
      in_scope_file="$f"
      break
      ;;
  esac
done <<< "$CHANGED_FILES"

if [ -z "$in_scope_file" ]; then
  echo "No file in this diff can change what AL observes, so no corpus declaration is required."
  exit 0
fi

# --- In scope: require a well-formed declaration -----------------------------

found_pr_line=""
found_pr_marker=""
found_na_reason=""
found_na_marker=""

while IFS= read -r line; do
  if printf '%s' "$line" | command grep -qiP "$CORPUS_PR_LINE_RE"; then
    found_pr_line="1"
  elif printf '%s' "$line" | command grep -qiP "$CORPUS_PR_MARKER_RE"; then
    # A Corpus-PR: line that does not carry a well-formed corpus PR URL.
    found_pr_marker="1"
  fi
  if [[ "$line" =~ ^[[:space:]]*[Cc]orpus-[Nn][Aa]:[[:space:]]*(.*)$ ]]; then
    found_na_marker="1"
    reason="${BASH_REMATCH[1]}"
    reason="$(printf '%s' "$reason" | command sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    # Placeholder tokens are rejected by an explicit, fixed list rather than by
    # judging the prose. Mechanical, not a quality bar: it only catches a marker
    # with nothing behind it.
    case "$(printf '%s' "$reason" | tr '[:upper:]' '[:lower:]')" in
      ""|"n/a"|"na"|"none"|"no"|"-"|"--"|"."|"?"|"tbd"|"todo"|"x") ;;
      *) found_na_reason="1" ;;
    esac
  fi
done <<< "$PR_BODY"

if [ -n "$found_pr_line" ] || [ -n "$found_na_reason" ]; then
  echo "Corpus linkage is declared (triggered by $in_scope_file)."
  exit 0
fi

REMEDY="Add ONE of these as its own line in the PR body:
  Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/<N>
    -- the corpus PR carrying the proving test. Open it yourself; that needs no
       approval, and the corpus CI boots a real BC service tier on every PR, so
       opening it IS the real-BC verification step.
  Corpus-NA: <reason>
    -- why this change needs no corpus test. Be specific; the reason is what a
       reviewer reads. 'ci only' or 'n/a' is not one."

if [ -n "$found_pr_marker" ]; then
  echo "::error::This PR has a 'Corpus-PR:' line but it does not carry a well-formed corpus pull request URL. It must be a full https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/<N> URL -- not a bare '#N', not an /issues/ link, and not the AL.Runner repository (pointing at this repo instead of the corpus is the common slip). $REMEDY" >&2
  exit 1
fi

if [ -n "$found_na_marker" ]; then
  echo "::error::This PR has a 'Corpus-NA:' line with no usable reason after the colon. The reason is the entire point of the declaration -- a bare marker gets pasted in reflexively, which is the same as having no guard. Say specifically why this change cannot or need not be proven by a corpus test. $REMEDY" >&2
  exit 1
fi

echo "::error::This PR changes $in_scope_file, which can change what AL observes, and its body declares no corpus linkage. .claude/rules/bc-behavior-tests-go-upstream.md requires that a test asserting plain BC behaviour lives upstream in the al-language corpus, where a real service tier adjudicates it -- a runner-local test for a BC-behaviour claim only proves the runner agrees with itself. This check does NOT decide whether your PR asserts BC behaviour; it only asks you to say. $REMEDY" >&2
exit 1
