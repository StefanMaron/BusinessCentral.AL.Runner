#!/usr/bin/env bash
# Tests for check_closing_reference.sh -- #2121 (missing closing reference)
# and #2128 (unintended closing reference), the same script covering both
# directions of the same bug class.
#
# Run directly: bash .github/scripts/test_check_closing_reference.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_closing_reference.sh"

pass=0
fail=0

assert_exit() {
  local desc="$1" expected_rc="$2" title="$3" body="$4"
  local rc
  PR_TITLE="$title" PR_BODY="$body" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

# --- #2121: the missing direction --------------------------------------------

assert_exit "a plain Closes #N line passes" 0 "fix: something" "Closes #123"
assert_exit "Fixes, case-insensitive, passes" 0 "fix: something" "fixes #123"
assert_exit "Resolves with repo prefix passes" 0 "fix: something" "Resolves owner/repo#123"
assert_exit "closing line without a # is NOT a reference -- fails as missing (bare number, GitHub does not act on it)" 1 "fix: something" "Closes 123"
assert_exit "closing line with trailing period passes" 0 "fix: something" "Closes #123."

assert_exit "no closing reference and no escape hatch fails" 1 "fix: something" \
  "This PR fixes a bug in the classifier. No issue number here at all."

assert_exit "empty body fails" 1 "fix: something" ""

assert_exit "escape hatch WITH a reason passes" 0 "docs: fix typo" \
  "No linked issue: this only fixes a typo in README.md."

assert_exit "escape hatch WITHOUT a reason fails" 1 "docs: fix typo" \
  "No linked issue:"

assert_exit "escape hatch marker with only whitespace after the colon fails" 1 "docs: fix typo" \
  "No linked issue:    "

# --- #2128: the unintended direction -----------------------------------------

# The actual #2127 incident, reproduced: a declared target plus a stray
# closing keyword elsewhere naming a DIFFERENT issue, embedded in a sentence
# that explicitly says it should NOT close it. GitHub ignores the negation;
# this script must not.
assert_exit "the real #2127 sentence: negated close of a different issue fails" 1 \
  "fix: something unrelated" \
  "Closes #2126

This does not close #2125 -- that report stays open pending its own reproduction."

assert_exit "a body whose only closing keyword names an issue other than the declared target fails" 1 \
  "fix: something" \
  "Closes #2121

This change also closes #999 as a side effect, though that is not the point of this PR."

assert_exit "stray closing keyword with no canonical declaration at all fails" 1 \
  "fix: something" \
  "This fixes #2125 somewhere in a sentence, with no standalone trailer line."

assert_exit "stray keyword restating the SAME declared target is allowed" 0 \
  "fix: something" \
  "Closes #2121

This closes #2121 for good."

assert_exit "closing keyword in the TITLE naming an undeclared issue fails" 1 \
  "fix: something that also closes #2125" \
  "Closes #2121"

assert_exit "a body mentioning another issue WITHOUT a keyword passes" 0 \
  "fix: something" \
  "Closes #2121

See #2125 for background -- that investigation found the root cause."

assert_exit "reference via possessive form without a keyword passes" 0 \
  "fix: something" \
  "Closes #2121

#2125's investigation found the root cause of this."

# --- Reference-form correctness: only what GitHub actually honors ----------
# A prior version of this script made the "#" optional in its ref pattern,
# which flagged ordinary English like "This fixes 3 bugs in the parser" as
# an unintended close of issue #3. GitHub does not act on a bare number --
# only "#N", "owner/repo#N", and a full issue/PR URL are real closing
# references -- so these are locked in as regression tests.

assert_exit "bare number after a keyword is NOT a closing reference -- passes" 0 \
  "fix: something" \
  "Closes #2121

This fixes 3 bugs in the parser."

assert_exit "a second ordinary bare-number sentence also passes" 0 \
  "fix: something" \
  "Closes #2121

That closes 2 open questions."

assert_exit "inline #N after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This also fixes #999 in passing."

assert_exit "inline owner/repo#N after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This resolves other-owner/other-repo#999 as a side effect."

assert_exit "a standalone owner/repo#N canonical line is recognized as a declared target" 0 \
  "fix: something" \
  "Closes other-owner/other-repo#999"

assert_exit "inline full GitHub issue URL after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This also fixes https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/999, a pasted link."

assert_exit "inline full GitHub PR URL after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This also closes https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/999."

assert_exit "a standalone full-URL canonical line is recognized as a declared target" 0 \
  "fix: something" \
  "Closes https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/999"

# GH-N is deliberately NOT treated as a closing reference: it only becomes
# one if this repo configures a custom autolink for that prefix, which it
# does not (`gh api repos/.../autolinks` -> `[]`). Locking this in so a
# future change doesn't start flagging it without its own RED/GREEN case.
assert_exit "GH-N after a keyword is NOT treated as a closing reference -- passes" 0 \
  "fix: something" \
  "Closes #2121

This also fixes GH-999 (no autolink configured for that prefix in this repo)."

# --- CRLF line endings must not break canonical-line detection --------------
# GitHub bodies arriving through the API can carry \r\n. A stray trailing
# \r would break an anchored "$" match if [[:space:]] didn't absorb it.

assert_exit "canonical Closes line survives a CRLF line ending" 0 \
  "fix: something" \
  "$(printf 'Closes #2121\r\n\r\nOrdinary prose.\r\n')"

assert_exit "canonical Closes line with trailing period survives CRLF" 0 \
  "fix: something" \
  "$(printf 'Closes #2121.\r\n\r\nOrdinary prose.\r\n')"

assert_exit "canonical Closes line with trailing space survives CRLF" 0 \
  "fix: something" \
  "$(printf 'Closes #2121 \r\n\r\nOrdinary prose.\r\n')"

assert_exit "two canonical Closes lines both survive CRLF line endings" 0 \
  "fix: something" \
  "$(printf 'Closes #2121\r\nCloses #2128\r\n\r\nOrdinary prose.\r\n')"

# --- Multiple canonical targets: our own PR closes two issues at once -------

assert_exit "two standalone Closes lines both pass as declared targets" 0 \
  "fix: something" \
  "Closes #2121
Closes #2128

Fixes the missing and unintended closing-reference directions together."

assert_exit "one of two declared targets referenced again in prose is allowed" 0 \
  "fix: something" \
  "Closes #2121
Closes #2128

This PR resolves #2128 by adding a script that also covers #2121."

# --- Clean ordinary PR passes -------------------------------------------------

assert_exit "ordinary PR with a clean Closes line and unrelated prose passes" 0 \
  "fix: something" \
  "Closes #2121

This adds a script and a test. See CONTRIBUTING.md for details."

# --- #2491: the commit-message route -----------------------------------------
#
# The PR body is not the only text GitHub reads. This repository's squash
# setting is squash_merge_commit_message=COMMIT_MESSAGES, so the branch's
# commit messages ARE the merge commit's body. PR #2486 proved it: its
# declared closing references (via closingIssuesReferences) were #2478 and
# #2480, a COMMIT MESSAGE said "It does not close #2479", and merge commit
# 28cdcf65 closed #2479. The body-only check passed that PR.

assert_exit_commits() {
  local desc="$1" expected_rc="$2" title="$3" body="$4" commits="$5"
  local rc
  PR_TITLE="$title" PR_BODY="$body" PR_COMMITS="$commits" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

# The reproducer, in the shape it actually occurred.
assert_exit_commits "the #2486 shape: a commit message saying it does NOT close an undeclared issue fails" 1 \
  "fix: something" \
  "Closes #2478
Closes #2480" \
  "fix: the environment-key half

Scope note: this addresses the environment-key half of #2479. It does not close #2479
 -- the issue's own repro still shows the baseline going missing."

assert_exit_commits "a bare closing keyword in a commit message for an undeclared issue fails" 1 \
  "fix: something" "Closes #123" "fix: something else

Fixes #456"

# Negative direction, and the one that keeps this from being a blanket ban on
# the word: a commit message may restate a target the body already declared.
assert_exit_commits "a commit message restating a DECLARED target passes" 0 \
  "fix: something" "Closes #123" "fix: something

Closes #123"

assert_exit_commits "a commit message referring to an issue WITHOUT a closing keyword passes" 0 \
  "fix: something" "Closes #123" "fix: something

Investigated alongside #456; see that issue for the remaining half."

assert_exit_commits "an ordinary multi-commit branch with clean messages passes" 0 \
  "fix: something" "Closes #123" "fix: first commit

test: add coverage for the first commit

docs: note the behaviour change"

# Additive: the script must behave identically when PR_COMMITS is unset, so a
# caller that predates this change is not broken by it.
assert_exit "PR_COMMITS unset still passes a clean body" 0 "fix: something" "Closes #123"
assert_exit "PR_COMMITS unset still fails a stray body reference" 1 "fix: something" \
  "Closes #123

This does not close #456."

# --- #3094: the SEPARATOR, not the reference form ----------------------------
#
# The third occurrence of this bug class, after #2127/#2125 and #2486/#2479.
# Both earlier fixes widened WHERE the script looks (the body, then the commit
# messages). This one is about the shape of the reference itself: the script
# required whitespace between the keyword and the reference
# ("[[:space:]]+"), so no colon form matched EITHER pattern, while GitHub's
# parser honors it.
#
# Measured, not assumed. Merge commit bb09fa5b (PR #2951) carried the line
# below in a commit message, and the issue timeline attributes the close to it:
#
#   closed at 2026-09-06T09:55:35Z commit_id=bb09fa5b...
#
# #2942 was closed although the sentence says in plain words that it stays
# open. The guard was green.
#
# The first case is that exact string.
assert_exit_commits "the real #3094 sentence: 'closes: #N' in a commit message fails" 1 \
  "feat: something" \
  "Closes #2931" \
  "feat: something

open rather than #2931, which this PR closes: #2942 for RunPageLink and #2943"

# Both directions of the same defect, in the body this time.
assert_exit "a stray colon-form close of an undeclared issue fails" 1 "fix: something" \
  "Closes #123

This does not close: #456."
assert_exit "a stray colon-form close with no space fails" 1 "fix: something" \
  "Closes #123

This does not close:#456."
assert_exit "a stray semicolon-form close fails" 1 "fix: something" \
  "Closes #123

Superseded; fixes; #456 stays open."
assert_exit "a stray colon-form close naming a URL fails" 1 "fix: something" \
  "Closes #123

This does not close: https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/456"
assert_exit "a stray colon-form close with a repo prefix fails" 1 "fix: something" \
  "Closes #123

Not this one, resolved: owner/repo#456"

# The MIRROR bug: a body that declares its target with a colon is declaring it
# as far as GitHub is concerned, so the script must recognise it as the
# canonical line rather than reporting "no linked issue" while GitHub closes one.
assert_exit "a canonical 'Closes: #N' line is recognised as the declared target" 0 \
  "fix: something" "Closes: #123"
assert_exit "a canonical 'Closes: #N' line with a trailing period passes" 0 \
  "fix: something" "Closes: #123."

# Widening the separator must not start matching ordinary prose. A keyword and
# a reference separated by WORDS is not a closing reference in any form GitHub
# honors, and flagging it would train authors to ignore this check.
assert_exit "a keyword separated from the reference by prose still passes" 0 \
  "fix: something" "Closes #123

This fixes the regression reported in #456."
assert_exit "'fixes N bugs' with no # is still not a reference" 0 "fix: something" \
  "Closes #123

This fixes 3 bugs in the parser."

# --- #2646: the PR template must not answer the check on the author's behalf ---
#
# The template exists so the escape hatch is discoverable where the body is
# written rather than only where CI fails. That puts its own example text into
# every PR body, which is a trap in two directions, and BOTH were hit while
# writing it:
#
#   * a literal "Closes #<a real number>" anywhere in the template -- including
#     inside its HTML comment -- closes that issue on merge; and
#   * a complete "No linked issue: <a reason>" line satisfies the escape hatch,
#     so an author who edits nothing passes the check with no linked issue and
#     no reason. That is strictly worse than the failure this template replaces.
#
# So the contract is: the UNEDITED template must FAIL, and each of the two
# completed forms must PASS. These cases read the real file, so a future edit
# that reintroduces either trap fails here rather than on someone's PR.

TEMPLATE="$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)/.github/pull_request_template.md"

if [ ! -f "$TEMPLATE" ]; then
  echo "FAIL - .github/pull_request_template.md is missing (issue #2646 added it)"
  fail=$((fail + 1))
else
  template_body="$(cat "$TEMPLATE")"

  assert_exit "the UNEDITED PR template fails: neither form completed" 1 \
    "fix: something" "$template_body"

  assert_exit "the template with a real Closes number passes" 0 \
    "fix: something" "$(printf '%s' "$template_body" | sed 's/^Closes #$/Closes #4242/' | sed '/^No linked issue:$/d')"

  assert_exit "the template with the escape hatch completed passes" 0 \
    "fix: something" "$(printf '%s' "$template_body" | sed '/^Closes #$/d' | sed 's/^No linked issue:$/No linked issue: submodule pin bump/')"

  # The author who fills the reason but leaves the bare "Closes #" behind: a
  # bare marker with no number is not a reference GitHub acts on, so this is
  # the escape-hatch case and must pass rather than trip the stray check.
  assert_exit "escape hatch completed with a bare 'Closes #' left behind passes" 0 \
    "fix: something" "$(printf '%s' "$template_body" | sed 's/^No linked issue:$/No linked issue: docs typo/')"
fi

# --- #3678: the branch names an issue the body never declares -----------------
#
# 31 merged PRs in the 30-day retrospective window sat on a branch named
# agent/<id>/issue-N and declared no closing reference for N; 12 of those
# issues were still open afterwards, invisible to the ready queue because
# nothing relabelled them (retrospective finding b-20).
#
# So when the head branch names an issue, the body must say what the PR does
# about it, in exactly one of two shapes, each on its own line:
#
#   Closes #N     -- the PR closes it (already the canonical trailer)
#   Part of #N    -- the PR lands part of it; N stays open and the merge pass
#                    puts it back on the ready queue
#
# "Part of" carries no closing keyword, so it neither closes N nor trips the
# stray check. A "Part of #N" naming the branch issue also counts as the
# body's declaration when there is no "Closes" line at all: a partial landing
# has a linked issue, and pushing those authors at the "No linked issue:"
# escape hatch would make the escape hatch a lie.
#
# PR_HEAD_REF is optional and empty by default, so every case above -- none of
# which sets it -- must keep behaving exactly as it did.

assert_exit_branch() {
  local desc="$1" expected_rc="$2" branch="$3" title="$4" body="$5"
  local rc
  PR_TITLE="$title" PR_BODY="$body" PR_HEAD_REF="$branch" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

# "fail with the named message" is part of the contract, so one case reads
# stderr rather than only the exit code -- an author who gets a generic
# "no closing reference" error is not told that "Part of #N" is available.
assert_stderr_branch() {
  local desc="$1" needle="$2" branch="$3" title="$4" body="$5"
  local err
  err=$(PR_TITLE="$title" PR_BODY="$body" PR_HEAD_REF="$branch" "$SCRIPT" 2>&1 >/dev/null)
  if printf '%s' "$err" | command grep -qF "$needle"; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: stderr did not contain '$needle'; got: $err"
    fail=$((fail + 1))
  fi
}

assert_exit_branch "branch issue declared by a Closes line passes" 0 \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #3678"

assert_exit_branch "branch issue declared by a full-URL Closes line passes" 0 \
  "agent/fbk-2/issue-3678" "fix: something" \
  "Closes https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3678"

assert_exit_branch "branch issue undeclared but carried by a Part of line passes" 0 \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #123

Part of #3678"

assert_exit_branch "a Part of line alone, with no Closes at all, passes" 0 \
  "agent/fbk-2/issue-3678" "fix: something" "Part of #3678

This lands the first half; the rest stays open."

assert_exit_branch "Part of is case-insensitive and survives a trailing period" 0 \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #123

part of #3678."

assert_exit_branch "Part of survives CRLF line endings" 0 \
  "agent/fbk-2/issue-3678" "fix: something" \
  "$(printf 'Closes #123\r\nPart of #3678\r\n\r\nOrdinary prose.\r\n')"

assert_exit_branch "branch issue undeclared and no Part of line fails" 1 \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #123

An ordinary body that never mentions the branch's own issue."

assert_stderr_branch "the failure names both accepted shapes" "Part of #3678" \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #123"
assert_stderr_branch "the failure names the Closes shape too" "Closes #3678" \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #123"

assert_exit_branch "a Part of line naming a DIFFERENT issue does not satisfy the branch" 1 \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #123

Part of #999"

assert_exit_branch "an inline 'part of #N' in a sentence does not satisfy the branch" 1 \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #123

This is part of #3678, landing the first half."

assert_exit_branch "the escape hatch does not excuse a branch that names an issue" 1 \
  "agent/fbk-2/issue-3678" "docs: fix typo" "No linked issue: this only fixes a typo."

# A branch outside the agent/<id>/issue-N shape says nothing about an issue,
# so the whole check stands down: these must behave exactly as they do with
# PR_HEAD_REF unset.
assert_exit_branch "a non-agent branch with a clean Closes line passes" 0 \
  "feature/some-work" "fix: something" "Closes #123"
assert_exit_branch "a non-agent branch with the escape hatch passes" 0 \
  "docs/typo" "docs: fix typo" "No linked issue: this only fixes a typo."
assert_exit_branch "a non-agent branch with no declaration still fails" 1 \
  "feature/some-work" "fix: something" "Nothing linked here."
assert_exit_branch "an agent branch with no issue-N segment stands down" 0 \
  "agent/fbk-2/experiment" "fix: something" "Closes #123"
assert_exit_branch "an empty PR_HEAD_REF stands down" 0 \
  "" "fix: something" "Closes #123"

# The stray check still runs first: a branch-satisfying body with an
# unintended inline close must still fail for THAT reason.
assert_exit_branch "a stray close is still caught on an agent branch" 1 \
  "agent/fbk-2/issue-3678" "fix: something" "Closes #3678

This also fixes #999 in passing."

# --- #3934: prose after the issue number ---------------------------------------
#
# PR #3927 opened with "Part of #1883 — the NavDataTransfer cluster (...)" and
# the gate reported the declaration as absent. The marker still has to START
# the line; only the end is relaxed, and the number must not run on into more
# digits or letters.

assert_exit_branch "a Part of line with prose after the number passes" 0 \
  "agent/fbk-2/issue-3678" "fix: something" \
  "Part of #3678 — the NavDataTransfer cluster (7 of the 69 registrations)."

assert_exit_branch "a Part of line with prose after a colon form passes" 0 \
  "agent/fbk-2/issue-3678" "fix: something" "Part of: #3678, the first half"

assert_exit_branch "a Part of number that runs on into more digits is a different issue" 1 \
  "agent/fbk-2/issue-3678" "fix: something" "Part of #36789 — some other work"

assert_exit_branch "a Part of number glued to letters is not a declaration" 1 \
  "agent/fbk-2/issue-3678" "fix: something" "Part of #3678abc"

assert_stderr_branch "a mention that is not a declaration is reported as malformed, naming the line" \
  "malformed" "agent/fbk-2/issue-3678" "fix: something" "Closes #123

This is part of #3678, landing the first half."

assert_stderr_branch "...and quotes the line the author wrote" \
  "This is part of #3678, landing the first half." "agent/fbk-2/issue-3678" "fix: something" \
  "Closes #123

This is part of #3678, landing the first half."

assert_stderr_branch "a body with no mention at all is still reported as absent, not malformed" \
  "neither closes that issue nor says it stays open" "agent/fbk-2/issue-3678" "fix: something" \
  "Closes #123"

# --- #3792: a suffix after the branch's issue number ---------------------------
#
# One issue landed as several PRs on agent/<id>/issue-N-<step> branches is the
# case "Part of #N" exists for; the $ anchor stood the whole check down there.

assert_exit_branch "a suffixed branch reads its issue number and accepts Part of" 0 \
  "agent/fbk-2/issue-3678-codeunit" "fix: something" "Part of #3678"

assert_exit_branch "a suffixed branch still requires the body to declare its issue" 1 \
  "agent/fbk-2/issue-3678-codeunit" "fix: something" "Closes #123"

assert_exit_branch "a digit after the number is part of the number, not a suffix" 1 \
  "agent/fbk-2/issue-36780" "fix: something" "Part of #3678"

assert_exit_branch "letters glued to the number are not an issue branch, so the check stands down" 0 \
  "agent/fbk-2/issue-3678abc" "fix: something" "Closes #123"

# --- #4294: the historical mention in a queue scan -----------------------------
#
# This block also PINS the pre-publish recipe in .claude/rules/branch-and-pr.md
# (marked there with Recipe-pinned-by). That recipe tells an author to run this
# very script over a body before publishing it; the arms below are what make the
# advice worth following, because they are what says the script still answers
# correctly on the shapes an author actually writes.
#
# Two loops tripped this within four hours, both writing a TRUE statement about
# an issue that was already shut, in the queue-scan paragraph
# batch-sibling-issues-by-file.md point 5 and search-for-the-same-defect-first.md
# both require. The author is writing history, not a directive, so there is
# nothing to notice -- unlike the negation cases (#2127, #2486), where the author
# is at least thinking about closing behaviour while writing.
#
# What actually discriminates is NOT the tense. It is whether a WORD separates
# the keyword from the reference, because SEP matches at most one punctuation
# mark and cannot span a word. Measured on this script, both directions:
#
#   fires:  "closed #4249"   "closed: #4249"   "closed, #4255"
#   clean:  "closed via #4249"   "closed by #4249"   "fixed in #4249"
#
# So the safe rewrite keeps the author's verb and adds a preposition. That is
# worth pinning from BOTH sides: the fires-side arms stop a future narrowing of
# SEP from silently letting a real close through, and the clean-side arms stop a
# future widening from eating the rewrite this script's own error message now
# recommends.

# The fires side. Each is a true past-tense sentence about an already-closed
# issue that would nonetheless close it again on merge.
assert_exit "#4294 a past-tense 'closed #N' in a queue scan fires" 1 "fix: something" \
  "Closes #123

Queue scan: #456 closed #789 with two items left homeless."

assert_exit "#4294 a past-tense colon form 'closed: #N' fires" 1 "fix: something" \
  "Closes #123

Already closed: #789."

# The sub-shape nobody had named: in a state table the comma hands the keyword
# to the NEXT number on the line, so the issue that closes is not the one the
# word is about. Here the author is describing #456; the gate reports #789, and
# GitHub would close #789.
assert_exit "#4294 a comma in a state table fires on the FOLLOWING number" 1 "fix: something" \
  "Closes #123

| #456 - closed, #789 - open |"

# The comma separator is in SEP alongside the colon and semicolon, but only
# those two were pinned. Comma is the one a table row produces.
assert_exit "#4294 a bare stray comma form 'fixes, #N' fires" 1 "fix: something" \
  "Closes #123

Superseded, fixes, #456 stays open."

# Backticks are not protection: GitHub's parser does not see markdown. Recorded
# on #4294 by the agent that hit it while DOCUMENTING this defect -- quoting the
# offending text in a code span reproduced it.
assert_exit "#4294 a code span around the clause does not protect it" 1 "fix: something" \
  "Closes #123

The offending shape is \`closed #789\` in a queue scan."

# A fenced block is not protection either, and #4393 fixed the direction this
# used to fail in. A line whose ENTIRE content is "<keyword> #N" used to match
# CANONICAL_LINE_RE wherever it sat, fence included, so it was read as a
# DECLARATION: the gate printed "declared target(s): 123 789", exited 0, and #789
# closed on merge with no error for anyone to read. It is now a STRAY, which is
# what the code-span arm directly above already did -- the two shapes differ by
# one character and now agree. The full class (tilde fences, info strings,
# nesting, unclosed fences) is table-driven further down.
assert_exit "#4393 a bare clause alone on a fenced line is a STRAY, not a declaration" 1 \
  "fix: something" \
  "Closes #123

\`\`\`
closed #789
\`\`\`"

# Anything else on the line breaks the canonical match, and the stray check sees
# it again -- which is why the code-span case above fires and the bare one does
# not. These two arms bracket the hole, so a future fix that moves its edge is
# visible here rather than silent.
assert_exit "#4294 a fenced clause with other text on the line fires normally" 1 \
  "fix: something" \
  "Closes #123

\`\`\`
  # closed #789
\`\`\`"

# The clean side: the rewrite the error message recommends must keep passing.
# Without these, narrowing SEP is the only failure this block can detect, and a
# widening that ate the recommended rewrite would ship green.
assert_exit "#4294 the recommended rewrite 'closed via #N' passes" 0 "fix: something" \
  "Closes #123

Queue scan: #456 closed via #789."

assert_exit "#4294 'was closed by #N' passes" 0 "fix: something" \
  "Closes #123

#456 was closed by #789."

assert_exit "#4294 'fixed in #N' passes" 0 "fix: something" \
  "Closes #123

That was fixed in #789."

assert_exit "#4294 'settled by #N' carries no keyword at all and passes" 0 "fix: something" \
  "Closes #123

#456 was settled by #789."

# A parenthesised state word with no keyword-adjacent number is prose, not a
# reference -- the separator cannot span the ") and (" between them. This is the
# form an author can reach for when a table row is what they want.
assert_exit "#4294 a state word in parentheses after the number passes" 0 "fix: something" \
  "Closes #123

The queue scan found #456 (closed) and #789 (open)."

# The message the author reads must name the rewrite, not only 'see #N'. An
# author whose sentence is about what HAPPENED to an issue cannot use 'see #N'
# without losing the meaning, which is why both loops reworded and one of them
# reworded into the defect again.
run_and_capture_stderr() {
  PR_TITLE="fix: something" PR_BODY="$1" PR_HEAD_REF="" "$SCRIPT" 2>&1 >/dev/null
}
msg="$(run_and_capture_stderr "Closes #123

Queue scan: #456 closed #789 with two items homeless.")"
if printf '%s' "$msg" | command grep -q "closed via #789"; then
  echo "ok   - #4294 the error message names the preposition rewrite for the reported number"
  pass=$((pass + 1))
else
  echo "FAIL - #4294 the error message does not name the preposition rewrite ('closed via #789')"
  fail=$((fail + 1))
fi

# ...and the message must explain WHY the reported number can be the wrong one, not
# only offer the rewrite. Deleting the whole comma-handoff paragraph left the suite
# at 96/0 while the arm above still passed, because that arm greps the
# recommendation: the remedy was pinned and the reasoning behind it was not. Found
# in review of #4294, not by the author.
#
# The pattern is chosen to survive a meaning-PRESERVING rewrite and die on a
# meaning-DROPPING one, which is the whole difficulty -- an editor tightening this
# message is the realistic threat, and a grep for a memorable phrase passes right
# through that. Measured over three rewrites and four truncations before picking it:
# the slogan "READ THE NUMBER ABOVE" failed ALL THREE rewrites and still matched a
# truncation that kept the slogan and dropped the claim, i.e. exactly backwards.
#
# So key on the CLAIM -- the keyword binds to a LATER number, not the preceding one
# -- via two independent halves, both of which a rewrite that keeps the meaning must
# keep, and neither of which survives dropping it:
#   1. the direction: "NEXT"/"FOLLOWING"/"following" number
#   2. that the flagged number may not be the author's subject
MSG_DIRECTION='(NEXT|FOLLOWING|following|next) number'
# MSG_SUBJECT was five fixed phrases, which is a spelling check wearing a concept's
# clothes: one inserted word ("may not BE the number your clause concerns") missed
# every alternative. Review measured 4 of 6 plausible rewrites false-FAILING -- the
# same defect the G2/G3 pair exists to reject, one level down, and invisible to the
# author because the rewrites it was validated against were the author's own.
#
# Widened toward the RELATIONSHIP the half asserts -- the number this message named
# may not be the thing the author's sentence was about -- which English carries by
# two routes, so both are admitted:
#   (a) a NEGATION landing on the author's subject ("not ... your clause concerns")
#   (b) a CONTRAST with the preceding number ("rather than the one before", "but to")
#
# Requiring the negation to LAND on a subject-bearing phrase is what keeps this from
# becoming merely loose. A bare "[^.]{0,60}" after "not" admitted three adversarial
# texts that keep the direction half and drop the claim -- the realistic one being
# "If the NEXT number is not the one you declared, add a standalone trailer line",
# which is plausible text this message could legitimately gain.
#
# Measured before adopting, against the REAL grep -E rather than a Python probe:
# 6/6 reviewer rewrites pass, 3/3 author rewrites pass, 6/6 meaning-dropping
# truncations rejected, 7/7 adversarial texts rejected. The reviewer's own concrete
# suggestion -- admit an optional verb between "not" and "the" -- was measured at
# 4/6 and REGRESSED a rewrite that already passed, so it is not what landed.
MSG_SUBJECT='(not|need not)[^.]{0,60}(your (sentence|clause|row|prose|text)|sentence.s subject|you meant|what your|number your|(is|was) (about|describing)|concerns)|(rather than|instead of|and not|, not|but to|but the)[^.]{0,40}(the one before|the preceding|preceding number|first number|#[0-9]+)|not[^.]{0,30}(the preceding|the one before|preceding number)'
if printf '%s' "$msg" | command grep -qE "$MSG_DIRECTION" \
   && printf '%s' "$msg" | command grep -qE "$MSG_SUBJECT"; then
  echo "ok   - #4294 the error message explains that the keyword binds to the FOLLOWING number"
  pass=$((pass + 1))
else
  echo "FAIL - #4294 the error message does not explain the comma handoff (the number it names may not be the author's subject)"
  fail=$((fail + 1))
fi

# The example is worth its own arm: the claim above is abstract, and a reader
# scanning a blocked build reads the concrete row first. Keyed on the RELATIONSHIP
# the example demonstrates (a second number closing while a first does not), not on
# the literal numbers, so renumbering the example is free and deleting it is not.
if printf '%s' "$msg" | command grep -qE "closes #[0-9]+, not #[0-9]+"; then
  echo "ok   - #4294 the error message carries a worked example of the handoff"
  pass=$((pass + 1))
else
  echo "FAIL - #4294 the error message has no worked 'closes #X, not #Y' example of the handoff"
  fail=$((fail + 1))
fi

# --- #4393: a fence is not a declaration site -------------------------------
#
# The defect this block pins: a line whose ENTIRE content is a closing clause
# matched CANONICAL_LINE_RE wherever it sat, so a clause inside a fenced code
# block was recorded as a DECLARED TARGET. The gate exited 0, printed that
# number as declared, and the issue closed on merge with no error anywhere.
#
# The fix makes such a line a STRAY. That is not a new rule: GitHub's parser
# does not see markdown (the code-span arm above measures exactly that), so the
# clause DOES close the issue. Treating it as a stray is the script agreeing
# with what actually happens, and it is what the neighbouring "other text on the
# line" arm already did.
#
# THE DIRECTION THAT MATTERS. Demoting a real declaration would fail a correct
# PR, so the fence tracker is deliberately conservative -- it models only what
# CommonMark 4.5 decides unambiguously, and every shape it does NOT model keeps
# its previous behavior. The green controls below are what would catch an
# over-refusing tracker; without them, a tracker that called every line fenced
# would pass every red arm in this block.
#
# The shapes come from CommonMark 0.31.2 section 4.5, not from an idea of what a
# fence looks like: a corpus invented by whoever wrote the parser would agree
# with the parser by construction.

# Opened and closed by backticks, the shape #4393 reported.
assert_exit "#4393 backtick fence: a bare clause inside is a stray" 1 "fix: something" \
  "Closes #123

\`\`\`
closed #789
\`\`\`"

# CommonMark 4.5 allows tildes as fence characters. The old code was
# line-oriented and so had no opinion about either; a tracker that models only
# backticks would leave this one silently declaring #789.
assert_exit "#4393 tilde fence: a bare clause inside is a stray" 1 "fix: something" \
  "Closes #123

~~~
closed #789
~~~"

# An info string after the opening fence is the normal way a body shows a
# language. If the tracker requires a bare opening line, every realistic pasted
# example keeps the defect.
assert_exit "#4393 fence with an info string: a bare clause inside is a stray" 1 "fix: something" \
  "Closes #123

\`\`\`text
closed #789
\`\`\`"

# Four or more markers are a legal fence (CommonMark 4.5: at least three), and a
# fence is closed only by a run of the SAME character at least as long. This arm
# and the next are what make nesting work; a tracker keyed on the literal
# three-backtick string gets both wrong.
assert_exit "#4393 four-backtick fence: a bare clause inside is a stray" 1 "fix: something" \
  "Closes #123

\`\`\`\`
closed #789
\`\`\`\`"

# Nesting, which is why the closing rule is length-sensitive: the inner
# three-backtick lines are CONTENT of the four-backtick fence, not a close
# followed by a reopen. A tracker that toggles on any fence line would read the
# clause as unfenced here and declare #789.
assert_exit "#4393 nested fence: the inner clause is still fenced, so still a stray" 1 "fix: something" \
  "Closes #123

\`\`\`\`
\`\`\`
closed #789
\`\`\`
\`\`\`\`"

# A tilde fence is not closed by backticks (CommonMark 4.5: same character), so
# BOTH clauses here are inside one fence. The arm proves the tracker does not
# treat the two characters as interchangeable -- if it did, the second clause
# would read as unfenced and be declared.
assert_exit "#4393 a tilde fence is not closed by backticks: both clauses are strays" 1 "fix: something" \
  "Closes #123

~~~
closed #789
\`\`\`
closed #888
~~~"

# An unclosed fence runs to the end of the document (CommonMark 4.5). Bodies get
# truncated and pasted half-finished, so this is not a hypothetical.
assert_exit "#4393 an unclosed fence runs to end of body: the clause is a stray" 1 "fix: something" \
  "Closes #123

\`\`\`
closed #789"

# Already covered before #4393 and kept as a boundary: anything else on the line
# breaks CANONICAL_LINE_RE, so the stray check saw it even when the fence did
# not. The fix must not change this arm's verdict, only its reason.
assert_exit "#4393 a fenced clause with other text on the line is still a stray" 1 "fix: something" \
  "Closes #123

\`\`\`
  # closed #789
\`\`\`"

# --- #4393: the exit code alone cannot see the SAME-CHARACTER and WHOLE-LINE
# rules, so these arms read the DECLARED TARGETS instead -------------------
#
# Found by mutation, not by inspection. Deleting the same-character test, and
# deleting the whole-line test, each left every arm above GREEN: those bodies
# exit 1 either way, because the FIRST clause is fenced under every variant and
# check_stray_in_text returns on the first stray it finds. The exit code is
# simply too coarse an observable for the property those arms claim.
#
# The finer observable is which numbers the script DECLARES. A fence rule that
# closes too eagerly lets a LATER clause out of the fence, and that clause is
# then recorded as a declared target -- the #4393 defect itself, one line deeper
# in the body. So assert on stdout, and make the second clause the subject by
# declaring the first one so the run reaches stdout at all.
declared_line() {
  PR_TITLE="fix: something" PR_BODY="$1" "$SCRIPT" 2>/dev/null | command grep -oE "declared target\(s\):.*"
}

assert_not_declared() {
  local desc="$1" body="$2" unwanted="$3" out
  out=$(declared_line "$body")
  if printf '%s' "$out" | command grep -qE "(^| )$unwanted( |$)"; then
    echo "FAIL - $desc: #$unwanted was recorded as a declared target ($out)"
    fail=$((fail + 1))
  else
    echo "ok   - $desc"
    pass=$((pass + 1))
  fi
}

# SAME-CHARACTER rule. If backticks were allowed to close a tilde fence, #888
# escapes the fence and is declared.
assert_not_declared "#4393 a tilde fence is not closed by backticks: the later clause is not declared" \
  "Closes #789

~~~
closed #789
\`\`\`
closed #888
~~~" 888

# WHOLE-LINE rule: a line that merely STARTS with the marker is content, not a
# close (CommonMark 4.5 allows only trailing whitespace after a closing fence).
assert_not_declared "#4393 a line merely STARTING with the marker does not close the fence" \
  "Closes #789

\`\`\`
closed #789
\`\`\`x not a close
closed #888
\`\`\`" 888

# LENGTH rule, at the finer observable: a three-marker line inside a four-marker
# fence is content. The exit-code arm above catches this one too; both edges.
assert_not_declared "#4393 a shorter marker run does not close a longer fence" \
  "Closes #789

\`\`\`\`
closed #789
\`\`\`
closed #888
\`\`\`\`" 888

# The mirror. Without it, every arm above passes for a tracker that never closes
# a fence at all -- the over-refusal direction, at the finer observable.
out=$(declared_line "Closes #789

\`\`\`
closed #789
\`\`\`

Closes #888")
if printf '%s' "$out" | command grep -qE "(^| )888( |$)"; then
  echo "ok   - #4393 a properly closed fence lets the following trailer declare normally"
  pass=$((pass + 1))
else
  echo "FAIL - #4393 a properly closed fence did NOT let the following trailer declare ($out)"
  fail=$((fail + 1))
fi

# --- #4393: the two implementations must agree on WHAT COUNTS AS INDENTATION
#
# Found in review of this PR. The shell used [[:space:]] and the Python port
# used [ \t]; those differ on CR, VT and FF, so a line led by one of them opened
# a fence in Python and not in the shell -- with Python the PERMISSIVE side,
# which is the dangerous one: it would declare a hidden target while the gate
# called the same line a stray.
#
# Narrowed the SHELL to match Python rather than the reverse. CommonMark 0.31.2
# section 4.5 says the opening fence "may be preceded by up to three spaces of
# indentation" -- spaces, per section 2.1, not whitespace generally. A bare CR is
# a LINE TERMINATOR, so inside an already-split line it is not indentation at
# all; VT and FF have no block-indentation semantics (2.2 counts indentation in
# spaces, with tabs expanded). Narrowing also makes the shell see FEWER fences,
# so it strictly reduces false positives, and a false positive fails a CORRECT
# PR -- the expensive direction here. Spec answer and safe answer agree.
#
# THE PROBE SHAPE MATTERS, and the obvious one does not work. A lead character
# before a fence that is later CLOSED is invisible: the closing marker ends the
# block either way, so both implementations agree on every later line. The
# discriminating shape is an UNCLOSED one -- lead character, marker, then a
# trailer. If the lead opens a fence the trailer is swallowed; if it does not,
# the trailer declares. Measured before writing the arm, because the first
# version of it asserted the wrong side and passed for the wrong reason.
#
# Table-driven over the class: a single arm keyed on \r would be a spelling
# check, and VT and FF diverged identically without being reachable from it.
assert_lead_not_indentation() {
  local desc="$1" lead="$2" out
  out=$(PR_TITLE="fix: something" PR_BODY="Closes #123

${lead}\`\`\`
Closes #999" "$SCRIPT" 2>/dev/null | command grep -oE "declared target\(s\):.*")
  # The lead character is not indentation, so no fence opens, so the trailer for
  # #999 is an ordinary line and IS declared.
  if printf '%s' "$out" | command grep -qE "(^| )999( |$)"; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: it opened a fence and swallowed the trailer ($out)"
    fail=$((fail + 1))
  fi
}

assert_lead_is_indentation() {
  local desc="$1" lead="$2" out rc
  PR_TITLE="fix: something" PR_BODY="Closes #123

${lead}\`\`\`
Closes #999" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  # A space or a tab IS indentation, so the fence opens, runs to end of body, and
  # swallows the trailer -- leaving #999 undeclared. Pass 2 then reports it as a
  # stray, which is exit 1. This is the control: it fails if the narrowing goes
  # too far and stops treating spaces and tabs as indentation.
  if [ "$rc" = "1" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected the fence to open (exit 1), got exit $rc"
    fail=$((fail + 1))
  fi
}

# The three characters [[:space:]] matches and [ \t] does not.
assert_lead_not_indentation "#4393 a bare CR before a fence marker is not indentation" "$(printf '\r')"
assert_lead_not_indentation "#4393 a bare VT before a fence marker is not indentation" "$(printf '\v')"
assert_lead_not_indentation "#4393 a bare FF before a fence marker is not indentation" "$(printf '\f')"

# CONTROLS: the two characters that ARE indentation must keep opening a fence.
# Without these, narrowing the class to nothing at all would pass every arm above.
assert_lead_is_indentation "#4393 control: a space before a fence marker IS indentation" " "
assert_lead_is_indentation "#4393 control: a tab before a fence marker IS indentation" "$(printf '\t')"
assert_lead_is_indentation "#4393 control: three spaces before a fence marker IS indentation" "   "

# And an ordinary CRLF body must be unaffected: CR as a LINE ENDING is normal
# and common, and only a BARE CR inside a line is not indentation. The fenced
# clause names an undeclared issue, so this stays exit 1 as a stray.
CRLF_FENCE="$(printf '%s' '```')"
assert_exit "#4393 control: an ordinary CRLF body is unaffected" 1 "fix: something" \
  "$(printf 'Closes #123\r\n\r\n%s\r\nclosed #789\r\n%s' "$CRLF_FENCE" "$CRLF_FENCE")"

# STATED GAP, raised in review of this PR: CommonMark 0.31.2 section 4.5 says a
# BACKTICK fence's info string may not contain a backtick, so "\`\`\`x`y" does not
# open a fence. (A tilde fence has no such restriction.) Both implementations
# treat it as an opener, so they AGREE -- there is no parity risk here, and the
# only cost is a false positive, which fails loudly with the number named.
# Reaching the defect at all needs an unclosed pseudo-fence AND a later
# declaration. Pinned as the current behavior rather than implemented, to keep
# this diff to the defect it is about; a fix belongs with a real CommonMark
# info-string parser, not another special case in a line-oriented script.
assert_exit "#4393 STATED GAP: a backtick in a backtick fence's info string still opens a fence" 1 \
  "fix: something" \
  "Closes #123

\`\`\`x\`y
Closes #789"

# --- #4393 GREEN CONTROLS: the over-refusal direction -----------------------
#
# Every arm above passes for a tracker that refuses everything. These are the
# ones that do not: each is an honest declaration that MUST keep exiting 0, and
# each sits at a place a fence tracker could plausibly get wrong.

assert_exit "#4393 control: a plain trailer with no fence anywhere still passes" 0 "fix: something" \
  "Closes #123"

# Leading whitespace is legal on a trailer (CANONICAL_LINE_RE allows it) and is
# also how CommonMark indents a fence inside a list. Up to three spaces must stay
# a trailer.
assert_exit "#4393 control: a trailer indented three spaces still passes" 0 "fix: something" \
  "   Closes #123"

# The trailer is OUTSIDE the fence; a tracker that never closes its fence would
# swallow it and report the PR as having no closing reference at all.
assert_exit "#4393 control: a trailer AFTER a closed fence still passes" 0 "fix: something" \
  "\`\`\`
some example code
\`\`\`

Closes #123"

# The mirror: a trailer between two fences catches an off-by-one in which line
# the close applies to.
assert_exit "#4393 control: a trailer BETWEEN two fences still passes" 0 "fix: something" \
  "\`\`\`
a
\`\`\`

Closes #123

\`\`\`
b
\`\`\`"

# Same for tildes, and for a fence carrying an info string -- both are places the
# close rule could be asymmetric with the open rule.
assert_exit "#4393 control: a trailer after a closed TILDE fence still passes" 0 "fix: something" \
  "~~~
some example code
~~~

Closes #123"

assert_exit "#4393 control: a trailer after a fence with an info string still passes" 0 "fix: something" \
  "\`\`\`bash
echo hi
\`\`\`

Closes #123"

# The self-documenting case, and the reason this fix does not make writing about
# closing references impossible: a fenced clause naming an ALREADY-DECLARED
# target is a restatement, exempt via is_declared exactly as an inline one is.
# This PR's own body has this shape.
assert_exit "#4393 control: a fenced clause naming the DECLARED target is a harmless restatement" 0 \
  "fix: something" \
  "Closes #123

\`\`\`
Closes #123
\`\`\`"

# The colon form inside a fence is still a stray -- SEP is shared, so a fence
# tracker must not accidentally narrow it.
assert_exit "#4393 a fenced colon-form clause is a stray" 1 "fix: something" \
  "Closes #123

\`\`\`
closes: #789
\`\`\`"

# STATED, TESTED GAP -- an indented code block (CommonMark 4.4, four spaces) is
# deliberately NOT modelled. A trailer indented three spaces is legal and passes
# (control above); four spaces is an indented code block. The boundary is one
# space wide and invisible in a rendered body, and demoting a four-space trailer
# would fail a correct PR for a reason its author cannot see. So this shape keeps
# its pre-#4393 behavior, and this arm records that rather than leaving it to be
# rediscovered as a bug.
assert_exit "#4393 KNOWN GAP: an indented code block is not modelled, so its clause still declares" 0 \
  "fix: something" \
  "Closes #123

prose:

    closed #789"

# The escape hatch and the Part-of path must not be swallowed by the tracker:
# both are read from the body line by line the same way.
assert_exit "#4393 control: the escape hatch still works with a fence in the body" 0 "docs: typo" \
  "No linked issue: fixes a typo in README.md.

\`\`\`
some code
\`\`\`"

# --- #4396: SEP and CANONICAL_LINE_RE stay PERMISSIVE about CR, VT and FF ----
#
# This script writes both constants with [[:space:]]; tools/pr-body.py's port
# wrote them with [ \t], and the two differ on CR, VT and FF. The port was
# widened to match rather than this script narrowed, because THIS side is the
# one that matches GitHub:
#
#   * a body stored with CRLF line endings carries a CR at the end of every
#     line, the trailer included, and CANONICAL_LINE_RE is $-anchored;
#   * GitHub honours that trailer. PR #3967's body ends its trailer
#     "Closes #3964\r" and closed #3964 on merge; PR #3899's "Closes #3881\r"
#     closed #3881. Both read back from closingIssuesReferences.
#
# Narrowing this script to [ \t] was measured against those two real bodies and
# failed BOTH of them -- a false positive on a correct PR, which is the
# expensive direction: the cheapest way for an author to clear it is to delete
# the sentence that tripped it.
#
# So these rows pin the permissiveness as DELIBERATE. A future narrowing of
# SEP or CANONICAL_LINE_RE turns them red rather than silently failing correct
# PRs, which is the whole reason they are here.

assert_exit "#4396 a CR before the keyword still declares" 0 "fix: something" \
  "$(printf '\rCloses #123')"
assert_exit "#4396 a CR inside the keyword/number separator still declares" 0 "fix: something" \
  "$(printf 'Closes\r#123')"
assert_exit "#4396 a trailing CR after the reference still declares" 0 "fix: something" \
  "$(printf 'Closes #123\r')"
assert_exit "#4396 a VT inside the separator still declares" 0 "fix: something" \
  "$(printf 'Closes\v#123')"
assert_exit "#4396 an FF before the keyword still declares" 0 "fix: something" \
  "$(printf '\fCloses #123')"

# The reachable instance, as PR #3967 really is: every line CRLF-terminated.
assert_exit "#4396 a wholly CRLF body declares its trailer (PR #3967's shape)" 0 "fix: something" \
  "$(printf 'Closes #123\r\n\r\nSome prose about the change.\r\n')"

# ...and the number really is recorded, not merely "the script exited 0 for
# some other reason". assert_exit alone cannot tell those apart: a body the
# tracker made invisible ALSO exits non-zero, and one that hits the escape
# hatch exits 0 while declaring nothing.
assert_declared() {
  local desc="$1" body="$2" wanted="$3" out
  out=$(declared_line "$body")
  if printf '%s' "$out" | command grep -qE "(^| )$wanted( |$)"; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: #$wanted was NOT recorded as a declared target ($out)"
    fail=$((fail + 1))
  fi
}

assert_declared "#4396 the CRLF trailer's number is the DECLARED target, not just an exit 0" \
  "$(printf 'Closes #123\r\n\r\nSome prose about the change.\r\n')" "123"
assert_declared "#4396 a CR in the separator declares the number it precedes" \
  "$(printf 'Closes\r#123')" "123"

# GREEN CONTROLS. Every row above passes for a class widened to "anything", so
# these are the rows that discriminate: prose must still not declare, and a
# foreign keyword separated by a CR must still be caught as a stray.
assert_exit "#4396 control: a plain trailer still declares" 0 "fix: something" "Closes #123"
assert_exit "#4396 control: prose with a keyword and no # is still not a reference" 1 "fix: something" \
  "This fixes 3 bugs in the parser. No issue number here at all."
assert_exit "#4396 control: a CR-separated FOREIGN keyword is still a stray" 1 "fix: something" \
  "$(printf 'Closes #123\n\nThis does not close\r#999.')"
assert_not_declared "#4396 control: the CR-separated foreign number is not declared" \
  "$(printf 'Closes #123\n\nThis does not close\r#999.')" "999"

echo ""
echo "$pass passed, $fail failed"
if [ "$fail" -ne 0 ]; then
  exit 1
fi
