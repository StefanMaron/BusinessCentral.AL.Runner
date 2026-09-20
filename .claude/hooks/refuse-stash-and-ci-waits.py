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
CI-wait arm blocked (#4402). `command_text()` blanks those regions before
anything is split into segments; what remains is judged exactly as before.

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
# to blank the regions bash never parses as commands before splitting at all.
#
# Measured over this project's 10 transcripts -- 15,035 unique Bash commands,
# 234 refused as CI waits -- 145 (62%) were refusals of prose: 141 heredoc
# bodies, 4 multi-line `--body` arguments.
HEREDOC_OPEN = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")
# Leading noise a real invocation carries: keywords, env assignments, wrappers.
# `timeout <dur>` and `setsid`/`stdbuf` carry an argument or none; stripping them is
# what lets `timeout 2700 tools/ci-wait.py ...` be seen as the ci-wait call it is
# (#4288 -- that spelling occurs in the measured transcript population).
LEADING_NOISE = re.compile(
    r'^(?:\s*(?:then|else|elif|do|done|fi|if|while|until|for|!|time|exec|nohup|'
    r'setsid|stdbuf|timeout\s+-?[\d.]+[smhd]?|timeout|'
    r'sudo|command|builtin|\w+=\S*)\s+)+')

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


def _blank_heredoc_bodies(cmd: str) -> str:
    """Replace every heredoc body, and its terminator line, with blanks.

    Newlines are preserved so the result segments identically to the input
    everywhere outside a document.

    A body ends at the first line equal to its delimiter, which is what bash
    does: an INDENTED occurrence of the delimiter does not close a `<<` heredoc,
    and `.strip()` here would wrongly say it does -- except that `<<-` strips
    leading TABS from the terminator, so tabs must be ignored and spaces must
    not. An UNTERMINATED heredoc runs to end-of-command, which is also what bash
    does, and is the loud direction: everything after it is blanked, so a wait
    hidden there is not reached -- but neither is it allowed, because the
    refusal still sees every segment BEFORE the heredoc opened.
    """
    lines = cmd.split("\n")
    kept = list(lines)
    i = 0
    while i < len(lines):
        opens = HEREDOC_OPEN.findall(lines[i])
        if not opens:
            i += 1
            continue
        # Several heredocs may open on one line; bash consumes their bodies in
        # order, so each delimiter ends only its own body.
        j = i + 1
        for _quote, delim in opens:
            while j < len(lines):
                closed = lines[j].lstrip("\t").rstrip() == delim
                kept[j] = ""
                j += 1
                if closed:
                    break
        i = j
    return "\n".join(kept)


def _blank_multiline_quotes(cmd: str) -> str:
    """Blank the inside of any quoted string that SPANS A NEWLINE.

    A multi-line quoted argument is a document -- `gh pr comment --body "..."`
    -- and its newlines are what let SEGMENT_SPLIT cut prose into segments.

    Single-line quoting is deliberately left intact: bash still parses the rest
    of that line as a command, and blanking it would erase a poll URL POLLS_CI
    must match (`gh api "...actions/runs..."`) or the `--timeout` a duration
    judgement is read from. Both are pinned as controls in
    tools/test_agent_workflow_hooks.py.
    """
    out = list(cmd)
    k, n, quote, start = 0, len(cmd), None, 0
    while k < n:
        ch = cmd[k]
        if quote is None:
            if ch in "'\"":
                quote, start = ch, k
            elif ch == "\\":
                k += 1
        elif ch == quote:
            if "\n" in cmd[start:k]:
                for x in range(start, k + 1):
                    if out[x] != "\n":
                        out[x] = " "
            quote = None
        elif ch == "\\" and quote == '"':
            k += 1
        k += 1
    if quote is not None and "\n" in cmd[start:]:
        for x in range(start, n):
            if out[x] != "\n":
                out[x] = " "
    return "".join(out)


def command_text(cmd: str) -> str:
    """`cmd` with every region bash does not parse as a command blanked out.

    Heredocs first and completely, then multi-line quotes in what remains: a
    `python3 - <<'PY'` body carries its own single-line quotes, and scanning
    quote state across a body that has not been removed yet leaves them behind.
    """
    return _blank_multiline_quotes(_blank_heredoc_bodies(cmd))


def segments(cmd: str) -> list:
    out = []
    for raw in SEGMENT_SPLIT.split(command_text(cmd)):
        seg = LEADING_NOISE.sub("", raw.strip()).strip()
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
