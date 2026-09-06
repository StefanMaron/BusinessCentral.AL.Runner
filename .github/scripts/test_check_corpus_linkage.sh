#!/usr/bin/env bash
# Tests for check_corpus_linkage.sh -- the corpus-linkage declaration guard (#3255).
#
# Two halves get equal weight here on purpose. The TRIGGER cases prove the guard
# fires when a diff could change what AL observes; the SKIP cases prove it stays
# quiet otherwise. A guard scoped so narrowly it never fires is the same as no
# guard and looks green forever, so the skip list is not a formality -- every
# path family this repository actually contains is asserted one way or the other.
#
# Run directly: bash .github/scripts/test_check_corpus_linkage.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_corpus_linkage.sh"

pass=0
fail=0

CORPUS_URL="https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/211"

assert_exit() {
  local desc="$1" expected_rc="$2" files="$3" body="$4"
  local rc
  CHANGED_FILES="$files" PR_BODY="$body" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

# --- Scope: diffs that MUST trigger the guard --------------------------------
# Each is a path family that can change what AL observes. With no trailer in the
# body, every one of these must fail.

assert_exit "Patches/ triggers (substitutes BC method behaviour)" 1 \
  "AlRunner/Patches/MockTestPage.cs" "no trailer here"
assert_exit "Patches/ virtual table triggers" 1 \
  "AlRunner/Patches/RecordPatches.DateVirtualTable.cs" "no trailer here"
assert_exit "Cecil rewrite of the runtime engine triggers" 1 \
  "AlRunner/Infrastructure/NclCecilRewrite.Records.cs" "no trailer here"
assert_exit "bare NclCecilRewrite.cs triggers" 1 \
  "AlRunner/Infrastructure/NclCecilRewrite.cs" "no trailer here"
assert_exit "BcCompiler.cs triggers (the AL compile path)" 1 \
  "AlRunner/BcCompiler.cs" "no trailer here"
assert_exit "BcCompiler.Incremental.cs triggers" 1 \
  "AlRunner/BcCompiler.Incremental.cs" "no trailer here"
assert_exit "BcAssembler.cs triggers (the AL emit path)" 1 \
  "AlRunner/BcAssembler.cs" "no trailer here"
assert_exit "Rewriters/ triggers (rewrites over AL output)" 1 \
  "AlRunner/Rewriters/CallSiteArgWrap.cs" "no trailer here"

# One in-scope file among many out-of-scope ones is still in scope. This is the
# realistic shape -- a fix plus its docs -- and reading only the first path would
# miss it.
assert_exit "a mixed diff triggers on its one in-scope file" 1 \
  "$(printf 'docs/limitations.md\nREADME.md\nAlRunner/Patches/FormPatches.cs\n')" \
  "no trailer here"
assert_exit "an in-scope file listed last still triggers" 1 \
  "$(printf 'tools/ci-wait.py\nAlRunner/Patches/SessionPatches.cs\n')" "no trailer here"

# --- Scope: diffs that MUST NOT trigger the guard ----------------------------
# Requiring a trailer on these would produce "Corpus-NA: ci only" noise nobody
# reads, which trains people to paste it reflexively and kills the guard.

assert_exit "workflow changes are skipped" 0 ".github/workflows/pr-gate.yml" ""
assert_exit "CI script changes are skipped" 0 ".github/scripts/check_corpus_linkage.sh" ""
assert_exit "tools/ changes are skipped" 0 "tools/ci-wait.py" ""
assert_exit "scripts/ changes are skipped" 0 "scripts/server-mode-test.sh" ""
assert_exit "docs/ changes are skipped" 0 "docs/limitations.md" ""
assert_exit "agent rules are skipped" 0 ".claude/rules/al-language-submodule.md" ""
assert_exit "C# unit tests are skipped" 0 "AlRunner.Tests/MediaSetPatchesTests.cs" ""
assert_exit "the corpus submodule pin is skipped" 0 "tests/al-language" ""
assert_exit "expectations manifests are skipped" 0 "tests/expectations/known-gaps-ui.json" ""
assert_exit "runner-extras AL is skipped" 0 "tests/runner-extras/foo/Foo.Codeunit.al" ""
assert_exit "top-level docs are skipped" 0 "README.md" ""
assert_exit "an empty diff is skipped" 0 "" ""

# Infrastructure/ is deliberately NOT wholesale in scope: it is a mixed
# directory, ~100 files, most of them plumbing. Only the Cecil rewrites are in.
assert_exit "Infrastructure plumbing is deliberately out of scope" 0 \
  "AlRunner/Infrastructure/PhaseLog.cs" ""
assert_exit "Infrastructure sharding is deliberately out of scope" 0 \
  "AlRunner/Infrastructure/ShardPlanner.cs" ""

# A doc file inside an in-scope directory is still a doc file. require-tests.yml
# draws the same .md line for the same reason.
assert_exit "a .md inside Patches/ is skipped" 0 "AlRunner/Patches/README.md" ""
assert_exit "a .md inside Patches/ among other .md files is skipped" 0 \
  "$(printf 'AlRunner/Patches/NOTES.md\ndocs/scope.md\n')" ""

# --- The trailer: accepted forms ---------------------------------------------

assert_exit "a well-formed Corpus-PR URL passes" 0 \
  "AlRunner/Patches/MockTestPage.cs" "Closes #1

Corpus-PR: $CORPUS_URL"
assert_exit "Corpus-NA with a real reason passes" 0 \
  "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-NA: precompiled-dependency path; a corpus test source-compiles and would pass"
assert_exit "the trailer keyword is case-insensitive" 0 \
  "AlRunner/Patches/MockTestPage.cs" "corpus-na: the reason, spelled out properly"
assert_exit "leading whitespace before the trailer is tolerated" 0 \
  "AlRunner/Patches/MockTestPage.cs" "   Corpus-PR: $CORPUS_URL"
assert_exit "a trailing period after the URL is tolerated" 0 \
  "AlRunner/Patches/MockTestPage.cs" "Corpus-PR: ${CORPUS_URL}."
assert_exit "http scheme is tolerated" 0 \
  "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-PR: http://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/211"
assert_exit "both trailers together pass" 0 \
  "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-PR: $CORPUS_URL
Corpus-NA: and here is why the other half is not needed"
assert_exit "a trailer among other body prose passes" 0 \
  "AlRunner/Patches/MockTestPage.cs" \
  "This fixes the thing.

Corpus-PR: $CORPUS_URL

More prose after it."

# --- The trailer: rejected forms ---------------------------------------------

assert_exit "no trailer at all fails" 1 "AlRunner/Patches/MockTestPage.cs" \
  "This changes how TestPage resolves a field. Verified locally."
assert_exit "an empty body fails when in scope" 1 "AlRunner/Patches/MockTestPage.cs" ""

# The mandatory-reason idiom, same as check_closing_reference.sh's escape hatch:
# a bare opt-out marker would get pasted in reflexively.
assert_exit "Corpus-NA with no reason fails" 1 "AlRunner/Patches/MockTestPage.cs" "Corpus-NA:"
assert_exit "Corpus-NA with only whitespace fails" 1 "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-NA:      "
assert_exit "Corpus-NA: n/a is a placeholder, not a reason" 1 \
  "AlRunner/Patches/MockTestPage.cs" "Corpus-NA: n/a"
assert_exit "Corpus-NA: none is a placeholder" 1 \
  "AlRunner/Patches/MockTestPage.cs" "Corpus-NA: none"
assert_exit "Corpus-NA: TBD is a placeholder" 1 \
  "AlRunner/Patches/MockTestPage.cs" "Corpus-NA: TBD"
assert_exit "Corpus-NA: - is a placeholder" 1 "AlRunner/Patches/MockTestPage.cs" "Corpus-NA: -"

# Malformed URLs. Getting these wrong costs the author one edit; accepting them
# means the advisory half has nothing real to resolve.
assert_exit "Corpus-PR without a URL fails" 1 "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-PR: none"
assert_exit "Corpus-PR with no PR number fails" 1 "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/"
assert_exit "Corpus-PR naming the RUNNER repo fails" 1 "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/211"
assert_exit "Corpus-PR pointing at an ISSUE rather than a pull fails" 1 \
  "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/issues/211"
assert_exit "a bare #N is not a corpus PR reference" 1 "AlRunner/Patches/MockTestPage.cs" \
  "Corpus-PR: #211"

# The canonical-line requirement, same reasoning as check_closing_reference.sh:
# a declaration has to stand on its own line where a reviewer reads it, not be
# buried mid-sentence.
assert_exit "a trailer buried in a sentence does not count" 1 \
  "AlRunner/Patches/MockTestPage.cs" \
  "I think Corpus-PR: $CORPUS_URL would be the right one to open eventually."
assert_exit "prose merely mentioning the corpus does not count" 1 \
  "AlRunner/Patches/MockTestPage.cs" \
  "This matches what the corpus asserts upstream, see the al-language tests."

# --- Usage -------------------------------------------------------------------

rc=0
CHANGED_FILES="AlRunner/Patches/MockTestPage.cs" "$SCRIPT" >/dev/null 2>&1 || rc=$?
if [ "$rc" = "2" ]; then
  echo "ok   - a missing PR_BODY is a usage error, not a pass"
  pass=$((pass + 1))
else
  echo "FAIL - a missing PR_BODY should exit 2, got $rc"
  fail=$((fail + 1))
fi

rc=0
PR_BODY="x" "$SCRIPT" >/dev/null 2>&1 || rc=$?
if [ "$rc" = "2" ]; then
  echo "ok   - a missing CHANGED_FILES is a usage error, not a pass"
  pass=$((pass + 1))
else
  echo "FAIL - a missing CHANGED_FILES should exit 2, got $rc"
  fail=$((fail + 1))
fi

echo
echo "passed: $pass, failed: $fail"
[ "$fail" -eq 0 ]
