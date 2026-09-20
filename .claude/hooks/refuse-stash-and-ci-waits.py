#!/usr/bin/env python3
"""PreToolUse REFUSAL: `git stash` in any form, and a CI wait long enough that
the harness will background it.

Both are absolute rules with no legitimate exception, which is what makes them
safe to block rather than advise: `refs/stash` is shared by every worktree of
this repository (`.claude/rules/no-git-stash-with-worktrees.md`), and a
backgrounded child process dies when the turn ends
(`.claude/rules/no-backgrounding-long-commands.md`). Neither refusal is scoped
to an agent context, because neither shape is correct in any session.

The CI-wait refusal keys on the DURATION a command asks for, never on
`run_in_background` -- because the agent does not decide whether a wait runs in
the background. The harness moves any FOREGROUND Bash call to the background at
a hard 600s cap, which the call's own `timeout` field does not raise, so the
flag is unset precisely when the backgrounding happens: across every transcript
on this box, 95 CI waits were auto-backgrounded and the flag was unset on 100%
of them, so gating on it refused 0 of the 95 (#4288). The completion
notification then reports the WRAPPER's exit status, so `ci-wait.py` exiting 2
("STILL RUNNING ... This is NOT a verdict") arrives as "completed (exit code
0)" -- `.claude/rules/ci-verdicts.md` section 0 owns what that does to a verdict.

What is deliberately NOT refused, so the coordinator keeps working: a CI read
that cannot be backgrounded -- `--timeout 0`, or a `--timeout` genuinely under
the cap -- including a poll loop built from such reads; and any backgrounded
command that is not a CI wait, such as a detached runner, build or test sweep,
which stays the agent's own judgement.

Neither refusal reads a DOCUMENT the command writes: a heredoc body, or a
quoted argument spanning a newline, is prose rather than a command line, and
refusing an agent for writing ABOUT the tool was 62% of everything this hook's
CI-wait arm blocked (#4402). `command_text()` blanks those regions in a single
left-to-right scan before anything is split into segments; what remains is
judged exactly as before.

Exit 2 is what blocks a PreToolUse call and feeds stderr back to the model;
exit 0 allows it. There is no third state here: this hook reads the command
string it was handed, so it cannot fail to measure. A payload it cannot parse
allows the call rather than blocking on a guess.

Tested by tools/test_agent_workflow_hooks.py (tools/ is what pr-gate.yml globs).
"""
import json
import re
import sys

BLOCK = 2
ALLOW = 0

# A command line is a sequence of segments. Only what a segment *starts with*
# decides anything, so `command grep -rn "git stash" .claude/rules` -- a search
# of the docs -- is not read as running it.
SEGMENT_SPLIT = re.compile(r'\|\||&&|[;|\n&()`]|\$\(')

# ...but `\n` is in that split, and a DOCUMENT the command writes is split on
# newlines too, so a prose line that happens to BEGIN with a refused tool's name
# becomes a segment starting with it (#4402). The safety property above and that
# defect are the same character class, so the fix is not to narrow the split but
# to blank the regions bash never parses as commands before splitting at all --
# in ONE interleaved scan, because neither order of two separate passes is safe
# (see command_text).
#
# Measured 2026-09-20 by replaying this project's transcripts through the hook:
# of 234 commands it refused as CI waits, 145 (62%) were refusals of PROSE --
# heredoc bodies and multi-line `--body` arguments -- and 89 were real waits.
# The population grows with every session, so re-derive rather than quoting the
# absolute counts; the ratio is what the fix was judged on.
HEREDOC_OPEN = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")
# Leading noise a real invocation carries: keywords, env assignments, wrappers.
# `timeout <dur>` and `setsid`/`stdbuf` carry an argument or none; stripping them is
# what lets `timeout 2700 tools/ci-wait.py ...` be seen as the ci-wait call it is
# (#4288 -- that spelling occurs in the measured transcript population).
# `env` sits here with `nohup` and `setsid` for the same reason, and it was
# leaking on its own before #4418: `env git stash` was allowed. Its options are
# spelled out rather than swallowed by a generic `-\S*`, because `env -i` and
# `env --ignore-environment` take no argument while `env -u VAR` and `-S` do,
# and treating the VAR as an option would strip a word that is not one. The
# `\w+=\S*` alternative already covers `env FOO=1 <cmd>`.
LEADING_NOISE = re.compile(
    r'^(?:\s*(?:then|else|elif|do|done|fi|if|while|until|for|!|time|exec|nohup|'
    r'setsid|stdbuf|timeout\s+-?[\d.]+[smhd]?|timeout|'
    r'env(?:\s+(?:-[iv0]+|--ignore-environment|--null|'
    r'-[uSC]\s*\S+|--(?:unset|split-string|chdir)(?:=\S+|\s+\S+)))*|'
    r'sudo|command|builtin|\w+=\S*)\s+)+')

# `sh -c '<command>'` puts a real command where nothing ever judged it: the
# refusal reads only what a SEGMENT STARTS WITH, and the inner command is an
# argument (#4418). Note how narrow the hole actually was -- SEGMENT_SPLIT
# already cuts through a quoted argument, so `bash -c 'echo hi; git stash'` was
# refused all along; only the FIRST inner command sat behind the prefix.
#
# So this strips the wrapper and the quote that opens the string, letting that
# first command start a segment. It deliberately does NOT re-parse the string as
# a nested command line: the rest of it already segments, and a second parser
# would be a second thing to get wrong.
#
# `\S*sh` matches `bash`, `sh`, `zsh` and `/bin/bash`; the trailing `\b` after
# the flag bundle is what keeps `engine-test-bootstrap.sh -c Debug` out -- `-c`
# there is a CONFIG flag of a script, and that shape is 35 of the 41 `sh -c`-like
# commands in this project's measured transcript population, against 0 real
# waits. Over-blocking it would break every bootstrapped test run in the repo.
SHELL_DASH_C = re.compile(
    r'^(?:\S*/)?(?:ba|z|k|da|a)?sh(?:\s+-[A-Za-z]+)*\s+-[A-Za-z]*c\s+[\'"]?')

# Two more places a command genuinely STARTS without starting a segment: the
# argv `xargs` and `find -exec` build. Both are narrow -- only `xargs`' own
# options may sit between, and only up to the shell name -- so neither widens
# what the strip above can reach; they only let it reach the same wrapper one
# word later. Measured: 1 real `xargs ... sh -c` and 0 `find -exec sh -c` in
# this project's transcripts, so the point is coverage of the shape, not volume.
ARGV_INTRO = re.compile(
    r'^(?:xargs(?:\s+(?:-[A-Za-z]\s*\S*|--[\w-]+(?:=\S+)?))*'
    r'|(?:\S+\s+)*?-exec(?:dir)?)\s+')

GIT_OPTS = r'(?:\s+(?:-[A-Za-z]\s+\S+|-[A-Za-z]\S*|--[\w-]+(?:=\S+)?))*'
GIT_STASH = re.compile(r'^git\b' + GIT_OPTS + r'\s+stash\b')

CI_WAIT_RUN_WATCH = re.compile(r'^(?:python3?\s+)?gh\b.*\brun\s+watch\b')
CI_WAIT_PR_CHECKS = re.compile(r'^(?:python3?\s+)?gh\b.*\bpr\s+checks\b.*--watch\b')
CI_WAIT_TOOL = re.compile(r'^(?:python3?\s+)?\S*ci-wait\.py\b')
TIMEOUT_VALUE = re.compile(r'--timeout(?:=|\s+)(\d+)\b')

# The harness moves a FOREGROUND Bash call to the background once it has run this
# long, and the tool call's own `timeout` field does not raise the ceiling: declared
# values of 600000 .. 3600000 ms all reported `within its 600s timeout` (#4288).
HARNESS_BACKGROUND_CAP_S = 600
SLEEPS = re.compile(r'^sleep\b')
POLLS_CI = re.compile(
    r'^(?:python3?\s+)?gh\b.*(?:\brun\s+(?:view|list)\b|\bpr\s+checks\b'
    r'|\bapi\b.*actions/runs)')

STASH_MESSAGE = (
    "BLOCKED: `git stash` (.claude/rules/no-git-stash-with-worktrees.md).\n"
    "WHY: refs/stash belongs to the REPOSITORY, not to a worktree. Every\n"
    ".claude/worktrees/<id>-issue-<N> directory is a worktree of this same repository, so\n"
    "your push and another loop's pop share one stack -- including `git stash list`, whose\n"
    "output interleaves entries you did not create and whose stash@{0} shifts under you.\n"
    "There is no per-worktree stash and no pathspec or -m form that makes one.\n"
    "USE INSTEAD:\n"
    "  git commit ...                          # on your own branch: free, and nobody can pop it\n"
    "  git diff HEAD > /tmp/mine.patch         # set aside; `git diff` alone captures nothing\n"
    "  git apply /tmp/mine.patch               #   once staged. Untracked files: copy by hand.\n"
    "  git checkout <rev> -- <path>            # RED baseline -- COMMIT FIRST; it writes the\n"
    "                                          #   index too, so re-read `git diff --cached`."
)

CI_WAIT_MESSAGE = (
    "BLOCKED: a CI wait long enough that the harness will background it\n"
    "(.claude/rules/no-backgrounding-long-commands.md, .claude/rules/ci-verdicts.md §0).\n"
    "WHY: you do not choose whether this runs in the background. The HARNESS moves any\n"
    "foreground Bash call to the background at a hard 600s cap, and the call's own\n"
    "`timeout` field does not raise it -- so leaving run_in_background unset changes\n"
    "nothing. A backgrounded command is a child of this turn and dies with the turn.\n"
    "AND THE NUMBER YOU GET BACK IS NOT THE TOOL'S: the completion notification reports\n"
    "the WRAPPER's exit status, so `ci-wait.py` exiting 2 (STILL RUNNING -- not a verdict)\n"
    "arrives as \"completed (exit code 0)\". Read as a verdict, that inverts the answer.\n"
    "Waiting on CI at all is the wrong shape besides: a workflow run completes whether or\n"
    "not anyone is watching, so push, open the PR, and read the verdict on your next pass.\n"
    "USE INSTEAD:\n"
    "  tools/ci-wait.py <PR> --timeout 0       # one pass, one answer, about a second\n"
    "                                          #   exit 2 = not reported yet; read again later\n"
    "If you must poll, loop over `--timeout 0` reads and read the TOOL's exit code each\n"
    "time. Local work that really is long runs in the FOREGROUND, and you push before\n"
    "starting it. Backgrounding a detached runner or build is not refused here."
)


def command_text(cmd: str) -> str:
    """`cmd` with every region bash does not parse as a command blanked out.

    ONE left-to-right scan, not two passes. Two passes cannot be ordered safely
    because each reads the other's region as text, and both orders leak
    SILENTLY (#4402, PR #4417 review):

      * heredocs first -- a `<<WORD` sitting inside quoted PROSE is read as a
        real opener, so an unterminated "heredoc" swallows the rest of the
        command and a wait after it is blanked away;
      * quotes first -- an apostrophe inside a heredoc body (`it's`, ordinary
        English) opens a quote that never closes, so the heredoc opener is
        blanked before it is seen, and again the rest is swallowed.

    Scanning once removes the question: a `<<` inside an open quote is not an
    opener, and a quote inside an open heredoc body is not a quote.

    Both halves are asserted, so do NOT refactor this back into two passes:
    tools/test_agent_workflow_hooks.py's "a heredoc opener inside quoted prose"
    and "an apostrophe inside a heredoc body" blocks fail for heredocs-first
    and quotes-first respectively, and no ordering passes both. The whole
    measured population cannot tell the three implementations apart -- it
    contains neither shape -- so the arms are the only thing that does.

    Newlines are preserved throughout, so text outside a document segments
    byte-identically to the unblanked command.
    """
    out = list(cmd)
    n = len(cmd)
    i = 0
    quote = None          # "'" or '"' while inside a quoted string
    quote_start = 0
    pending = []          # heredocs opened on this line, not yet consumed

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        ch = cmd[i]

        if quote is not None:
            # Inside a quoted string: nothing opens a heredoc here.
            if ch == "\\" and quote == '"' and i + 1 < n:
                i += 2
                continue
            if ch == quote:
                # Blank it only if it spanned a newline: a single-line quoted
                # argument is still parsed as part of its command line, and
                # blanking it would erase a poll URL or a `--timeout` value.
                if "\n" in cmd[quote_start:i]:
                    blank(quote_start, i + 1)
                quote = None
            i += 1
            continue

        if ch == "\\" and i + 1 < n:
            i += 2
            continue

        if ch in "'\"":
            quote, quote_start = ch, i
            i += 1
            continue

        if ch == "<" and cmd.startswith("<<", i):
            m = HEREDOC_OPEN.match(cmd, i)
            if m:
                pending.append(m.group(2))
                i = m.end()
                continue
            i += 2
            continue

        if ch == "\n" and pending:
            # Consume every body opened on the line just ended, in order.
            j = i + 1
            for delim in pending:
                while j < n:
                    eol = cmd.find("\n", j)
                    if eol == -1:
                        eol = n
                    line = cmd[j:eol]
                    blank(j, eol)
                    j = eol + 1
                    if line.lstrip("\t").rstrip() == delim:
                        break
                    if eol == n:
                        break
            pending = []
            # An unterminated body runs to end-of-command, exactly as bash
            # does. That is the LOUD direction: everything after it is blanked,
            # but every segment BEFORE the opener has already been scanned, so
            # a wait cannot be hidden by opening a heredoc after it.
            i = min(j, n)
            continue

        i += 1

    if quote is not None and "\n" in cmd[quote_start:]:
        blank(quote_start, n)
    return "".join(out)


def unwrap(seg: str) -> str:
    """`seg` with leading noise and any `sh -c` wrappers removed.

    Looped rather than applied once because each strip can expose the next:
    `timeout 30 bash -c 'sh -c "git stash"'` is noise, wrapper, wrapper. It
    terminates because every branch consumes at least one character.
    """
    while True:
        stripped = LEADING_NOISE.sub("", seg).strip()
        if SHELL_DASH_C.match(stripped) is None:
            # Only consumed when it actually exposes a wrapper: `xargs` and
            # `-exec` run ARBITRARY commands, and stripping them unconditionally
            # would make `xargs git stash` -- which stashes nothing without an
            # argument list this hook cannot see -- indistinguishable from the
            # real thing. Restricting the strip to a shell wrapper keeps the
            # judged position exactly as narrow as it was.
            exposed = ARGV_INTRO.sub("", stripped, count=1).strip()
            if SHELL_DASH_C.match(exposed):
                stripped = exposed
        stripped = SHELL_DASH_C.sub("", stripped, count=1).strip()
        if stripped == seg:
            return seg
        seg = stripped


def segments(cmd: str) -> list:
    out = []
    for raw in SEGMENT_SPLIT.split(command_text(cmd)):
        seg = unwrap(raw.strip())
        if seg:
            out.append(seg)
    return out


def stash_segment(segs: list):
    for seg in segs:
        if GIT_STASH.match(seg):
            return seg
    return None


def ci_wait_reason(segs: list):
    """The CI-wait shape in these segments, or None.

    A CI wait is refused on the duration it ASKS FOR, never on whether the caller
    set `run_in_background`. Measured across all 828 transcripts on this box: 95
    CI waits were moved to the background by the harness and all 95 had the flag
    unset, so a refusal gated on the flag refused none of them (#4288).
    """
    sleeping = any(SLEEPS.match(s) for s in segs)
    polling = any(POLLS_CI.match(s) for s in segs)
    for seg in segs:
        if CI_WAIT_RUN_WATCH.match(seg):
            return "`gh run watch` blocks until the run finishes, with no deadline of its own"
        if CI_WAIT_PR_CHECKS.match(seg):
            return "`gh pr checks --watch` blocks until the checks finish"
        if CI_WAIT_TOOL.match(seg):
            m = TIMEOUT_VALUE.search(seg)
            if m is None:
                return ("ci-wait.py with no `--timeout` polls until its default deadline, "
                        f"which is above the harness's {HARNESS_BACKGROUND_CAP_S}s cap")
            secs = int(m.group(1))
            if secs >= HARNESS_BACKGROUND_CAP_S:
                return (f"`--timeout {secs}` is at or above the harness's "
                        f"{HARNESS_BACKGROUND_CAP_S}s cap, so this call is backgrounded "
                        "whatever the flag says")
    if sleeping and polling:
        return "a sleep loop polling CI is a hand-rolled wait"
    return None


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return ALLOW
    if payload.get("tool_name") != "Bash":
        return ALLOW
    tool_input = payload.get("tool_input") or {}
    cmd = tool_input.get("command", "") or ""
    segs = segments(cmd)

    seg = stash_segment(segs)
    if seg:
        print(STASH_MESSAGE, file=sys.stderr)
        print(f"Refused segment: {seg}", file=sys.stderr)
        return BLOCK

    reason = ci_wait_reason(segs)
    if reason:
        print(CI_WAIT_MESSAGE, file=sys.stderr)
        print(f"Refused because {reason}.", file=sys.stderr)
        return BLOCK
    return ALLOW


if __name__ == "__main__":
    sys.exit(main())
