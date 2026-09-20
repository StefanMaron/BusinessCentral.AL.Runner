#!/usr/bin/env python3
"""Unit tests for the two BLOCKING PreToolUse hooks (#3707).

Both hooks live in `.claude/hooks/`, which `pr-gate.yml`'s tools-tests job does
not glob -- it runs `tools/test_*.py` only. So the tests for them live here,
where that job discovers them directly, and drive the real scripts as
subprocesses with a JSON PreToolUse payload on stdin, the only interface the
harness uses.

What "blocking" means, and why the assertions are on exit code 2 specifically:
a PreToolUse hook that exits 2 has its stderr fed back to the model and the tool
call is refused; exit 1 is a non-blocking error the model never sees. Asserting
"non-zero" would pass on a hook that does not actually block.

Run: python3 tools/test_agent_workflow_hooks.py
"""
from __future__ import annotations

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
HOOKS = os.path.join(ROOT, ".claude", "hooks")
REFUSE = os.path.join(HOOKS, "refuse-stash-and-ci-waits.py")
NAV = os.path.join(HOOKS, "prefer-code-navigation.py")

# Synthetic, never this checkout: the suite itself runs from an agent worktree
# during development and from the main checkout in CI, so deriving either cwd
# from __file__ would make the coordinator cases pass or fail by location.
MAIN_CHECKOUT = "/home/runner/work/BusinessCentral.AL.Runner/BusinessCentral.AL.Runner"
WORKTREE = MAIN_CHECKOUT + "/.claude/worktrees/fbk-9-issue-1234"

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def fire(hook: str, command: str, *, background: bool = False, cwd: str = "",
         tool: str = "Bash", env_extra: dict | None = None,
         agent_type: str = "") -> subprocess.CompletedProcess:
    tool_input: dict = {"command": command}
    if background:
        tool_input["run_in_background"] = True
    payload = {"tool_name": tool, "tool_input": tool_input}
    if cwd:
        payload["cwd"] = cwd
    if agent_type:
        payload["agent_type"] = agent_type
    env = dict(os.environ)
    # The invoking session's own identity must never leak into a case asserting
    # what happens WITHOUT one.
    for k in ("AL_RUNNER_AGENT_ID", "CLAUDE_AGENT_ID", "AL_RUNNER_HOOK_CONTEXT"):
        env.pop(k, None)
    env.update(env_extra or {})
    return subprocess.run([sys.executable, hook], input=json.dumps(payload),
                          capture_output=True, text=True, env=env)


def blocks(hook: str, command: str, must_say: str, **kw):
    r = fire(hook, command, **kw)
    ok = r.returncode == 2 and must_say in r.stderr
    return ok, f"exit={r.returncode} stderr={r.stderr.strip()[:160]!r}"


def allows(hook: str, command: str, **kw):
    r = fire(hook, command, **kw)
    return r.returncode == 0, f"exit={r.returncode} stderr={r.stderr.strip()[:160]!r}"


print("git stash -- blocked in every form, in every context")
STASH_BLOCKED = [
    ("bare git stash", "git stash"),
    ("git stash push with a pathspec", "git stash push -- AlRunner/Program.cs"),
    ("git stash pop", "git stash pop"),
    ("git stash apply", "git stash apply stash@{0}"),
    ("git stash drop", "git stash drop"),
    ("git stash list (a stack shared with every other loop)", "git stash list"),
    ("git -C <worktree> stash", "git -C .claude/worktrees/fbk-1-issue-1 stash"),
    ("stash after &&", "git add -A && git stash"),
    ("stash in a later segment", "echo hi; git stash save wip"),
]
for name, cmd in STASH_BLOCKED:
    ok, d = blocks(REFUSE, cmd, "refs/stash")
    check(name, ok, d)

r = fire(REFUSE, "git stash")
check("the stash refusal names the patch alternative", "git diff HEAD >" in r.stderr,
      r.stderr[:200])
check("the stash refusal cites the rule",
      "no-git-stash-with-worktrees" in r.stderr, r.stderr[:200])

print("\ngit stash -- shapes that must NOT be blocked")
STASH_ALLOWED = [
    ("grepping the docs for the phrase", "command grep -rn 'git stash' .claude/rules"),
    ("rg for the phrase", "rg --hidden 'git stash' ."),
    ("echoing a sentence about it", "echo 'never run git stash here'"),
    ("an ordinary git command", "git status --short"),
    ("a branch whose name contains stash", "git checkout -b agent/fbk-1/stash-docs"),
]
for name, cmd in STASH_ALLOWED:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

print("\nbackgrounded CI waits -- blocked")
CI_WAIT_BLOCKED = [
    ("gh run watch", "gh run watch 12345"),
    ("gh pr checks --watch", "gh pr checks 3707 --watch"),
    ("ci-wait.py with a positive timeout", "tools/ci-wait.py 3707 --timeout 900"),
    ("ci-wait.py with no timeout at all", "python3 tools/ci-wait.py 3707"),
    ("a sleep loop polling gh run view",
     "while true; do gh run view 123 --json status; sleep 30; done"),
    ("a sleep loop polling gh pr checks",
     "for i in 1 2 3; do gh pr checks 3707; sleep 60; done"),
]
for name, cmd in CI_WAIT_BLOCKED:
    ok, d = blocks(REFUSE, cmd, "--timeout 0", background=True)
    check(name, ok, d)

r = fire(REFUSE, "gh run watch 1", background=True)
check("the CI-wait refusal explains that a backgrounded child dies with the turn",
      "dies with the turn" in r.stderr, r.stderr[:200])
check("the CI-wait refusal cites the rule",
      "no-backgrounding-long-commands" in r.stderr, r.stderr[:200])

print("\nbackgrounded work that is NOT a CI wait -- allowed (detached runner runs)")
BACKGROUND_ALLOWED = [
    ("a detached runner run", "al-runner run --bundle app.json --out results.trx"),
    ("a detached dotnet run", "dotnet run --project AlRunner -c Release -- run --bundle x.json"),
    ("a detached test sweep", "dotnet test AlRunner.Tests --filter FullyQualifiedName~Foo"),
    ("gh run view as a single read", "gh run view 123 --json conclusion"),
    ("ci-wait.py --timeout 0 is one pass, not a wait", "tools/ci-wait.py 3707 --timeout 0"),
]
for name, cmd in BACKGROUND_ALLOWED:
    ok, d = allows(REFUSE, cmd, background=True)
    check(name, ok, d)

# #4288: the flag is not what decides whether a wait runs in the background. The
# harness moves a FOREGROUND command to the background at a hard 600s cap -- measured
# across all 828 transcripts on this box: 95 CI waits were auto-backgrounded and all 95
# had run_in_background unset, so a refusal gated on that flag refused none of them. The
# declared `timeout` does not raise the cap: all 52 calls declaring above it, spanning
# 660000 to 3600000 ms, reported `within its 600s timeout`.
HARNESS_BACKGROUND_CAP_S = 600

print("\nCI waits the HARNESS will background -- refused whatever the flag says (#4288)")
CI_WAIT_OVER_CAP = [
    ("ci-wait.py --timeout 1500, the shape measured on PR #4286",
     "tools/ci-wait.py 4286 --timeout 1500 > ci.txt 2>&1; echo \"ci-wait exit=$?\""),
    ("ci-wait.py --timeout 2400, the commonest shape in the transcripts",
     "python3 tools/ci-wait.py 3110 --timeout 2400 2>&1 | tail -40"),
    ("ci-wait.py with no --timeout at all defaults above the cap",
     "tools/ci-wait.py 3707"),
    ("--timeout=1500 in the equals spelling",
     "tools/ci-wait.py 4286 --timeout=1500"),
    ("a `timeout` wrapper does not make it a read",
     "timeout 2700 tools/ci-wait.py 3023 --timeout 3000"),
    ("gh run watch blocks with no deadline of its own",
     "gh run watch 33966349085 --repo o/r --exit-status --interval 30"),
    ("gh pr checks --watch likewise", "gh pr checks 3707 --watch"),
]
for name, cmd in CI_WAIT_OVER_CAP:
    ok, d = blocks(REFUSE, cmd, "--timeout 0")
    check(name, ok, d)

r = fire(REFUSE, "tools/ci-wait.py 4286 --timeout 1500")
check("the over-cap refusal says the harness backgrounds it, not the agent",
      "harness" in r.stderr.lower(), r.stderr[:240])
check("the over-cap refusal names the 600s cap",
      str(HARNESS_BACKGROUND_CAP_S) in r.stderr, r.stderr[:240])
check("the over-cap refusal warns the notification's exit code is the wrapper's",
      "wrapper" in r.stderr.lower(), r.stderr[:240])

# GREEN CONTROL. A hook that refuses every CI wait would pass every arm above and
# would also break the one form the rules mandate. These must stay allowed, and they
# are what distinguishes a correct hook from an over-refusing one.
print("\nCI reads UNDER the cap -- still allowed, in the foreground (the green control)")
CI_WAIT_UNDER_CAP = [
    ("--timeout 0 is one pass, the mandated form", "tools/ci-wait.py 3707 --timeout 0"),
    ("--timeout 0 with a redirect and an exit echo",
     "tools/ci-wait.py 4286 --timeout 0 > ci.txt 2>&1; echo \"ci-wait exit=$?\"; cat ci.txt"),
    ("--timeout=0 in the equals spelling", "tools/ci-wait.py 3707 --timeout=0"),
    ("--timeout 120, comfortably under the cap", "tools/ci-wait.py 3707 --timeout 120"),
    ("--timeout 599, the last value under the cap", "tools/ci-wait.py 3707 --timeout 599"),
    ("a single gh run view read", "gh run view 123 --json conclusion"),
    ("gh pr checks without --watch", "gh pr checks 3707"),
    ("a poll loop of --timeout 0 reads is the documented shape",
     "for i in 1 2 3; do tools/ci-wait.py 4286 --timeout 0 && break; command sleep 30; done"),
]
for name, cmd in CI_WAIT_UNDER_CAP:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

# The boundary itself: 600 is at the cap and must be refused, 599 allowed above.
ok, d = blocks(REFUSE, "tools/ci-wait.py 3707 --timeout 600", "--timeout 0")
check("--timeout 600 is AT the cap and refused", ok, d)

# ---------------------------------------------------------------------------
# #4402: text inside a HEREDOC BODY or a MULTI-LINE QUOTED ARGUMENT is a
# document, not a command line. Measured over this project's 10 transcripts --
# 15,035 unique Bash commands, 234 refused as CI waits -- 145 of those 234
# (62%) were refusals of prose: 141 heredoc bodies and 4 multi-line `--body`
# arguments. The mechanism is that SEGMENT_SPLIT splits on `\n`, so a prose
# line that happens to BEGIN with the tool's name becomes a segment starting
# with it, and the refusal judges only what a segment starts with.
#
# The direction of the fix matters more than its reach (#4402's own warning):
# today's failure is loud and costs one retry, while a mis-parsed heredoc that
# hides a real wait is SILENT. So every arm below that asserts ALLOW is paired
# with an arm asserting a genuine wait in the same command still BLOCKS.
print("\n#4402 prose in a heredoc body is a document, not a command -- allowed")
HEREDOC_PROSE_ALLOWED = [
    ("a quoted-delimiter heredoc naming the CI-wait tool",
     "cat > body.md <<'MDEOF'\n"
     "tools/ci-wait.py <PR> --timeout 0 is the read, one pass, one answer.\n"
     "MDEOF\n"
     "gh pr comment 4402 --body-file body.md"),
    ("a heredoc whose prose line starts with the tool name",
     "cat > note.md <<'EOF'\n"
     "ci-wait.py 4286 --timeout 1500 would be backgrounded by the harness.\n"
     "EOF"),
    ("an UNQUOTED-delimiter heredoc",
     "cat > note.md <<EOF\n"
     "tools/ci-wait.py 1 --timeout 2700 is the shape the hook refuses.\n"
     "EOF"),
    ("a <<- heredoc with tab stripping",
     "cat > note.md <<-EOF\n"
     "\ttools/ci-wait.py 1 --timeout 900 is refused.\n"
     "\tEOF"),
    ("a python program heredoc mentioning the tool in a string literal",
     "python3 - <<'PYEOF'\n"
     "p = 'tools/ci-wait.py'\n"
     "print(open(p).read()[:10])\n"
     "PYEOF"),
    ("prose naming gh run watch",
     "cat > body.md <<'MDEOF'\n"
     "gh run watch blocks until the run finishes, with no deadline of its own.\n"
     "MDEOF"),
    ("prose naming gh pr checks --watch",
     "cat > body.md <<'MDEOF'\n"
     "gh pr checks 4402 --watch blocks until the checks finish.\n"
     "MDEOF"),
    ("a heredoc body that would otherwise read as a sleep-poll loop",
     "cat > body.md <<'MDEOF'\n"
     "sleep 30 between reads, then gh run view 123 --json status, is a hand-rolled wait.\n"
     "MDEOF"),
    ("two heredocs in one command, prose in both",
     "cat > a.md <<'A'\n"
     "tools/ci-wait.py 1 --timeout 900\n"
     "A\n"
     "cat > b.md <<'B'\n"
     "gh run watch 5\n"
     "B"),
    ("a heredoc body containing a line that merely RESEMBLES its delimiter",
     "cat > body.md <<'MDEOF'\n"
     "  MDEOF appears here indented, which does not end the document.\n"
     "tools/ci-wait.py 1 --timeout 2700\n"
     "MDEOF"),
]
for name, cmd in HEREDOC_PROSE_ALLOWED:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

print("\n#4402 the same widening applied to the git stash arm")
STASH_PROSE_ALLOWED = [
    ("a heredoc naming git stash in prose",
     "cat > body.md <<'MDEOF'\n"
     "git stash is refused because refs/stash is shared by every worktree.\n"
     "MDEOF\n"
     "gh pr comment 4402 --body-file body.md"),
    ("a python heredoc with git stash in a string literal",
     "python3 - <<'PYEOF'\n"
     "CMD = 'git stash list'\n"
     "print(CMD)\n"
     "PYEOF"),
]
for name, cmd in STASH_PROSE_ALLOWED:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

# THE DANGEROUS DIRECTION. Each of these carries a REAL wait outside the
# document region, so a parser that mis-detects where a heredoc ends -- and
# swallows the rest of the command -- turns these green. They are the arms that
# fail if the refusal is weakened, and they are why the heredoc scan must end a
# document at its terminator rather than at end-of-command.
print("\n#4402 a real CI wait AFTER a heredoc must still be refused (the dangerous direction)")
WAIT_AFTER_HEREDOC = [
    ("a wait on the line after a quoted heredoc closes",
     "cat > body.md <<'MDEOF'\n"
     "prose about merge order\n"
     "MDEOF\n"
     "tools/ci-wait.py 4402 --timeout 2700"),
    ("a wait after an UNQUOTED heredoc closes",
     "cat > body.md <<EOF\n"
     "prose\n"
     "EOF\n"
     "gh run watch 12345"),
    ("a wait after a <<- heredoc closes",
     "cat > body.md <<-EOF\n"
     "\tprose\n"
     "\tEOF\n"
     "tools/ci-wait.py 1 --timeout 900"),
    ("a wait after a python program heredoc closes",
     "python3 - <<'PYEOF'\n"
     "print('hello')\n"
     "PYEOF\n"
     "tools/ci-wait.py 4402 --timeout 1500"),
    ("a wait after TWO heredocs close",
     "cat > a.md <<'A'\nx\nA\ncat > b.md <<'B'\ny\nB\ntools/ci-wait.py 1 --timeout 900"),
    ("a wait BEFORE a heredoc opens",
     "tools/ci-wait.py 4402 --timeout 2700\n"
     "cat > body.md <<'MDEOF'\n"
     "prose\n"
     "MDEOF"),
    ("a wait between two heredocs",
     "cat > a.md <<'A'\nx\nA\n"
     "tools/ci-wait.py 1 --timeout 2700\n"
     "cat > b.md <<'B'\ny\nB"),
    ("a wait after a heredoc whose body contains its delimiter INDENTED",
     "cat > body.md <<'MDEOF'\n"
     "  MDEOF indented does not close it\n"
     "MDEOF\n"
     "tools/ci-wait.py 1 --timeout 2700"),
    ("a real git stash after a heredoc closes",
     "cat > body.md <<'MDEOF'\n"
     "prose about the shared stash\n"
     "MDEOF\n"
     "git stash"),
]
for name, cmd in WAIT_AFTER_HEREDOC:
    want = "refs/stash" if cmd.rstrip().endswith("git stash") else "--timeout 0"
    ok, d = blocks(REFUSE, cmd, want)
    check(name, ok, d)

# An UNTERMINATED heredoc is the one shape where "where does the document end"
# has no answer. It fails LOUD by construction: the body runs to end-of-command,
# so anything after it is swallowed and the whole command is refused if any
# segment before the heredoc opened is a wait. Asserting the refusal here pins
# that the unterminated case is not silently permissive.
# The terminator comparison itself, from both sides. A `<<` body is closed only
# by a line EQUAL to the delimiter: space-indentation does not close it, while
# `<<-` strips leading TABS and does. Both arms are needed -- `.strip()` passes
# the second and fails the first, and "strip nothing" does the reverse.
# Two heredocs opened on ONE line -- the shape #4402 names. bash consumes the
# bodies in delimiter order, so a scan that handles only the first swallows the
# wrong region: it would run body A's scan to the end of body B and blank a
# wait sitting after both, or expose prose in body B. Both directions asserted.
print("\n#4402 two heredocs opened on one line, bodies consumed in order")
ok, d = allows(REFUSE,
               "cat <<'A' <<'B'\n"
               "tools/ci-wait.py 1 --timeout 2700\n"
               "A\n"
               "gh run watch 5\n"
               "B")
check("prose in BOTH bodies of a one-line double heredoc is allowed", ok, d)
ok, d = blocks(REFUSE,
               "cat <<'A' <<'B'\n"
               "prose one\n"
               "A\n"
               "prose two\n"
               "B\n"
               "tools/ci-wait.py 1 --timeout 2700",
               "--timeout 0")
check("...and a wait after BOTH bodies close is still refused", ok, d)

print("\n#4402 what closes a heredoc body: tabs for <<-, never spaces")
ok, d = allows(REFUSE,
               "cat > body.md <<'MDEOF'\n"
               "  MDEOF\n"
               "tools/ci-wait.py 1 --timeout 2700\n"
               "MDEOF")
check("a SPACE-indented delimiter does not close a << body", ok, d)
ok, d = allows(REFUSE,
               "cat > body.md <<-'MDEOF'\n"
               "tools/ci-wait.py 1 --timeout 2700\n"
               "\tMDEOF")
check("a TAB-indented delimiter does close a <<- body", ok, d)
ok, d = blocks(REFUSE,
               "cat > body.md <<-'MDEOF'\n"
               "prose\n"
               "\tMDEOF\n"
               "tools/ci-wait.py 1 --timeout 2700",
               "--timeout 0")
check("...and a wait after that tab-indented terminator is exposed", ok, d)

print("\n#4402 an unterminated heredoc must not become a way to hide a wait")
ok, d = blocks(REFUSE,
               "tools/ci-wait.py 4402 --timeout 2700\n"
               "cat > body.md <<'MDEOF'\n"
               "prose that never closes\n",
               "--timeout 0")
check("a wait before an unterminated heredoc is still refused", ok, d)

print("\n#4402 prose in a MULTI-LINE quoted argument is a document too")
QUOTED_PROSE_ALLOWED = [
    ("gh issue comment --body with the tool named in multi-line prose",
     "gh issue comment 4402 --repo o/r --body \"First line.\n"
     "tools/ci-wait.py 1 --timeout 2700 is what the hook refuses.\n"
     "Last line.\""),
    ("a single-quoted multi-line --body",
     "gh pr comment 4402 --body 'Line one.\n"
     "gh run watch 5 blocks forever.\n"
     "Line three.'"),
]
for name, cmd in QUOTED_PROSE_ALLOWED:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

print("\n#4402 a real wait outside a multi-line quoted argument is still refused")
ok, d = blocks(REFUSE,
               "gh issue comment 4402 --body \"prose\n"
               "more prose\"\n"
               "tools/ci-wait.py 4402 --timeout 2700",
               "--timeout 0")
check("a wait after a multi-line --body closes", ok, d)

# THE QUOTE/HEREDOC INTERACTION, pinned in both directions. These are the two
# arms that decide the implementation must be ONE interleaved scan rather than
# two passes: no ordering of two passes satisfies both, and each failure is
# SILENT-PERMISSIVE -- a real wait allowed (PR #4417 review).
#
#   A  a `<<WORD` inside quoted PROSE is not a heredoc opener.
#      Heredocs-first reads it as one, treats the rest of the command as an
#      unterminated body, and blanks the wait away.
#   D  an apostrophe inside a HEREDOC BODY is not a quote.
#      Quotes-first opens a quote that never closes and blanks the heredoc
#      opener before it is seen, swallowing the wait the same way.
#
# The arm above is deliberately kept as well: it is the same shape WITHOUT the
# opener, so the pair shows the opener is what discriminates.
print("\n#4402 a heredoc opener inside quoted prose is prose, not an opener")
ok, d = blocks(REFUSE,
               "gh pr comment 1 --body \"Notes:\n"
               "write it with cat <<MDEOF like CLAUDE.md says\n"
               "Done.\"\n"
               "tools/ci-wait.py 4417",
               "--timeout 0")
check("A: a wait after prose CONTAINING a heredoc opener is still refused", ok, d)
ok, d = blocks(REFUSE,
               "gh pr comment 1 --body 'single-quoted prose naming <<EOF inline'\n"
               "tools/ci-wait.py 4417 --timeout 2700",
               "--timeout 0")
check("A: ...and on one line, where the quote never spans a newline", ok, d)

print("\n#4402 an apostrophe inside a heredoc body is prose, not a quote")
ok, d = blocks(REFUSE,
               "cat > f.md <<'PY'\n"
               "it's ordinary English prose\n"
               "PY\n"
               "tools/ci-wait.py 1 --timeout 2700",
               "--timeout 0")
check("D: a wait after a body containing an apostrophe is still refused", ok, d)
ok, d = blocks(REFUSE,
               "python3 - <<'PY'\n"
               "print(\"a lone \\\" and an it's in one body\")\n"
               "PY\n"
               "gh run watch 5",
               "--timeout 0")
check("D: ...with both quote characters unbalanced in the body", ok, d)

# And the same interaction must not swing the other way: a document is still a
# document when it contains the other construct's syntax.
ok, d = allows(REFUSE,
               "cat > body.md <<'MDEOF'\n"
               "Write it with gh pr comment --body \"...\" or a heredoc.\n"
               "tools/ci-wait.py 1 --timeout 2700 is refused.\n"
               "MDEOF")
check("prose inside a body may contain quotes and still be a document", ok, d)

# The ORIGINAL green control, restated against the new code path: a single-line
# quoted argument is NOT a document region, because bash still parses the rest
# of that line as a command. `bash -c "..."` is the shape that would break if
# single-line quotes were stripped, and `gh api "...actions/runs..."` is the one
# that would stop matching POLLS_CI.
# The single-line-quoting control. A fix that stripped ALL quoted text, rather
# than only multi-line quoted text, would clear these two -- the first by
# erasing a poll URL POLLS_CI must still match, the second by erasing the
# `--timeout` the duration judgement is read from. Both stay refused, which is
# what says the widening did not reach past documents into arguments.
#
# `bash -c '<a real wait>'` is deliberately NOT asserted here: the shipped hook
# already allows it, on `main` and after this change alike, because `bash` is
# not in LEADING_NOISE and the wait is one argument rather than a segment. That
# is a pre-existing false negative in the dangerous direction, filed separately
# rather than folded in; it does not occur in the measured population -- 0 of
# the 41 `bash -c` commands across 15,035 mention a CI wait at all.
print("\n#4402 single-line quoting is not a document -- these must NOT be cleared")
# Both arms put the DECISIVE text inside a single-line quote, so blanking every
# quoted region -- rather than only multi-line ones -- changes the verdict.
# An earlier pair kept that text outside the quotes and a mutation widening the
# rule was absorbed; these were built by reading command_text()'s output.
ok, d = blocks(REFUSE,
               "while true; do gh api \"repos/o/r/actions/runs?head_sha=abc\" --jq .x; "
               "sleep 30; done",
               "--timeout 0")
check("a sleep loop whose poll URL is a quoted argument is still refused", ok, d)

# `--watch` lives inside the quoted argument here, and it is the whole reason
# the command is refused, so blanking every quoted region would clear it.
ok, d = blocks(REFUSE, "gh pr checks 4402 \"--watch\"", "--timeout 0")
check("a wait whose quoted argument carries --watch is still refused", ok, d)

# Non-CI background work is untouched by this widening.
print("\nnon-CI work is unaffected by the cap rule")
for name, cmd in [
    ("a long foreground corpus run", "dotnet run --project AlRunner -- run --bundle x.json"),
    ("a long foreground test sweep", "dotnet test AlRunner.Tests"),
    ("a detached runner run", "al-runner run --bundle app.json"),
]:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)
    ok, d = allows(REFUSE, cmd, background=True)
    check(name + " (backgrounded)", ok, d)

print("\nnon-Bash tools are never the business of either hook")
for hook in (REFUSE, NAV):
    r = fire(hook, "git stash", tool="Read")
    check(f"{os.path.basename(hook)} ignores a non-Bash tool", r.returncode == 0,
          f"exit={r.returncode}")

print("\ncode navigation -- BLOCKS in an agent context")
NAV_BLOCKED = [
    ("grep over the C# tree", "command grep -rn 'GetDataAccessForTableCore' AlRunner"),
    ("rg over the C# tree", "rg -n 'IsProcessingOnly' --glob '*.cs' AlRunner"),
    ("sed a range out of a C# file", "sed -n '600,680p' AlRunner/Patches/NavReportSync.cs"),
    ("cat a C# file", "cat AlRunner/Patches/RecordPatches.cs"),
    ("head a C# file", "head -60 AlRunner/BcRuntime.cs"),
]
for name, cmd in NAV_BLOCKED:
    ok, d = blocks(NAV, cmd, "context-pack.py", cwd=WORKTREE)
    check(name, ok, d)

# The signal the payload itself carries, measured on harness 2.1.266: a dispatched
# subagent's payload has agent_type="impl-agent" and a cwd of the PROJECT ROOT, so
# a cwd test alone would never fire for the agents this hook exists for.
for _t in ("impl-agent", "reviewer"):
    ok, d = blocks(NAV, "cat AlRunner/BcRuntime.cs", "context-pack.py",
                   cwd=MAIN_CHECKOUT, agent_type=_t)
    check("agent_type=" + _t + " blocks even from the project root", ok, d)

r = fire(NAV, "cat AlRunner/BcRuntime.cs", cwd=MAIN_CHECKOUT, agent_type="impl-agent",
         env_extra={"AL_RUNNER_HOOK_CONTEXT": "coordinator"})
check("a coordinator env var inherited by a dispatched agent does NOT disarm the block",
      r.returncode == 2, f"exit={r.returncode}")

r = fire(NAV, "cat AlRunner/Program.cs", cwd=MAIN_CHECKOUT, agent_type="orchestrator")
check("agent_type=orchestrator stays advisory", r.returncode == 0, f"exit={r.returncode}")

ok, d = blocks(NAV, "cat AlRunner/BcRuntime.cs", "context-pack.py",
               env_extra={"AL_RUNNER_AGENT_ID": "fbk-9"})
check("AL_RUNNER_AGENT_ID alone puts the hook in blocking mode", ok, d)

ok, d = blocks(NAV, "cat " + WORKTREE + "/AlRunner/BcRuntime.cs", "context-pack.py")
check("a worktree path in the command puts the hook in blocking mode", ok, d)

ok, d = blocks(NAV, "cat AlRunner/BcRuntime.cs", "context-pack.py",
               cwd=WORKTREE.replace("/", "\\"))
check("a Windows-separator cwd is recognised too", ok, d)

print("\ncode navigation -- ADVISORY outside an agent context, never blocking there")
for name, cmd in [
    ("the coordinator greps the C# tree", "command grep -rn 'Foo' AlRunner"),
    ("the coordinator reads a C# file", "cat AlRunner/Program.cs"),
]:
    r = fire(NAV, cmd, cwd=MAIN_CHECKOUT)
    check(name, r.returncode == 0 and "context-pack.py" in r.stderr,
          f"exit={r.returncode} fired={'context-pack.py' in r.stderr}")

r = fire(NAV, "cat AlRunner/Program.cs", cwd=WORKTREE,
         env_extra={"AL_RUNNER_HOOK_CONTEXT": "coordinator"})
check("AL_RUNNER_HOOK_CONTEXT=coordinator downgrades the block to a reminder",
      r.returncode == 0 and "context-pack.py" in r.stderr,
      f"exit={r.returncode} stderr={r.stderr[:120]!r}")

print("\ncode navigation -- the escape hatch, and what stays allowed in an agent context")
r = fire(NAV, "command grep -rn 'Foo' AlRunner   # hook:allow-grep", cwd=WORKTREE)
check("an explicit # hook:allow-grep marker downgrades the block",
      r.returncode == 0, f"exit={r.returncode}")

NAV_ALLOWED = [
    ("grepping a log", "command grep -n 'error' /tmp/run.log"),
    ("grepping a TRX", "command grep -c 'outcome' results.trx"),
    ("reading a results JSON", "cat scratchpad/results.json"),
    ("searching markdown", "rg --hidden 'clean status' .claude"),
    ("searching AL sources", "command grep -n 'SaveAsXml' tests/al-language/foo.al"),
    ("build output trimmed with tail", "dotnet build AlRunner -c Release | tail -5"),
    # tdd.md asks for the `Total:` line from every mutation run, and the natural
    # way to get it is a grep over `dotnet test` stdout. That greps no file (#3994).
    ("test output filtered with grep",
     "dotnet test AlRunner.Tests --filter X 2>&1 | command grep -E 'Failed:'"),
    ("test output filtered by project path",
     "dotnet test AlRunner.Tests/AlRunner.Tests.csproj --no-build | command grep Total"),
    ("dotnet run output filtered", "dotnet run --project AlRunner | command grep PASS"),
    ("writing a C# file with a heredoc", "cat > AlRunner/New.cs <<EOF\nclass X {}\nEOF"),
    ("git log over the C# tree", "git log --oneline -5 -- AlRunner"),
]
# The dotnet exemption is PIPE-connected only: `dotnet test` appearing anywhere in
# the string would make the literal text an untraceable opt-out (#3994 review).
NAV_BLOCKED_DESPITE_DOTNET = [
    ("dotnet test in a trailing comment",
     "command grep -rn 'Editable' AlRunner/ # after dotnet test"),
    ("a read chained after a build",
     "dotnet build AlRunner; sed -n '1,50p' AlRunner/Program.cs"),
    ("a source grep chained with &&",
     "dotnet test AlRunner.Tests && command grep -n 'Foo' AlRunner/Patches/Bar.cs"),
]
for name, cmd in NAV_ALLOWED:
    ok, d = allows(NAV, cmd, cwd=WORKTREE)
    check(name, ok, d)

for name, cmd in NAV_BLOCKED_DESPITE_DOTNET:
    ok, d = blocks(NAV, cmd, "context-pack.py", cwd=WORKTREE)
    check(name, ok, d)

print("\nboth hooks are actually registered -- a hook nothing invokes blocks nothing")
settings = json.load(open(os.path.join(ROOT, ".claude", "settings.json"), encoding="utf-8"))
registered = [h.get("command", "")
              for entry in settings.get("hooks", {}).get("PreToolUse", [])
              if entry.get("matcher") == "Bash"
              for h in entry.get("hooks", [])]
for script in ("refuse-stash-and-ci-waits.py", "prefer-code-navigation.py"):
    check("settings.json registers " + script,
          any(script in c for c in registered), str(registered))

print()
if FAILURES:
    print(f"{len(FAILURES)} failing check(s)")
    sys.exit(1)
print("all checks passed")
