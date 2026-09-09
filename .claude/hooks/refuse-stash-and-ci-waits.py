#!/usr/bin/env python3
"""PreToolUse REFUSAL: `git stash` in any form, and a backgrounded CI wait.

Both are absolute rules with no legitimate exception, which is what makes them
safe to block rather than advise: `refs/stash` is shared by every worktree of
this repository (`.claude/rules/no-git-stash-with-worktrees.md`), and a
backgrounded child process dies when the turn ends
(`.claude/rules/no-backgrounding-long-commands.md`). Neither refusal is scoped
to an agent context, because neither shape is correct in any session.

What is deliberately NOT refused, so the coordinator keeps working: a
backgrounded command that is not a CI wait -- a detached runner, build or test
sweep -- and any CI-wait shape run in the FOREGROUND. Only the pairing of
`run_in_background` with a CI wait is refused.

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
# Leading noise a real invocation carries: keywords, env assignments, wrappers.
LEADING_NOISE = re.compile(
    r'^(?:\s*(?:then|else|elif|do|done|fi|if|while|until|for|!|time|exec|nohup|'
    r'sudo|command|builtin|\w+=\S*)\s+)+')

GIT_OPTS = r'(?:\s+(?:-[A-Za-z]\s+\S+|-[A-Za-z]\S*|--[\w-]+(?:=\S+)?))*'
GIT_STASH = re.compile(r'^git\b' + GIT_OPTS + r'\s+stash\b')

CI_WAIT_RUN_WATCH = re.compile(r'^(?:python3?\s+)?gh\b.*\brun\s+watch\b')
CI_WAIT_PR_CHECKS = re.compile(r'^(?:python3?\s+)?gh\b.*\bpr\s+checks\b.*--watch\b')
CI_WAIT_TOOL = re.compile(r'^(?:python3?\s+)?\S*ci-wait\.py\b')
TIMEOUT_ZERO = re.compile(r'--timeout(?:=|\s+)0(?:\s|$)')
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
    "BLOCKED: a CI wait with run_in_background\n"
    "(.claude/rules/no-backgrounding-long-commands.md, .claude/rules/ci-verdicts.md §0).\n"
    "WHY: a backgrounded command is a child of this turn and dies with the turn -- no\n"
    "notification arrives, and nothing you were waiting for is ever reported. Waiting on CI\n"
    "at all is the wrong shape besides: a workflow run completes whether or not anyone is\n"
    "watching, so push, open the PR, and read the verdict on your next pass.\n"
    "USE INSTEAD:\n"
    "  tools/ci-wait.py <PR> --timeout 0       # one pass, one answer, about a second\n"
    "                                          #   exit 2 = not reported yet; read again later\n"
    "Local work that really is long runs in the FOREGROUND with a generous timeout, and you\n"
    "push before starting it. Backgrounding a detached runner or build is not refused here."
)


def segments(cmd: str) -> list:
    out = []
    for raw in SEGMENT_SPLIT.split(cmd):
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
    """The CI-wait shape in these segments, or None."""
    sleeping = any(SLEEPS.match(s) for s in segs)
    polling = any(POLLS_CI.match(s) for s in segs)
    for seg in segs:
        if CI_WAIT_RUN_WATCH.match(seg):
            return "`gh run watch` blocks until the run finishes"
        if CI_WAIT_PR_CHECKS.match(seg):
            return "`gh pr checks --watch` blocks until the checks finish"
        if CI_WAIT_TOOL.match(seg) and not TIMEOUT_ZERO.search(seg):
            return "ci-wait.py without `--timeout 0` polls until its deadline"
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

    if tool_input.get("run_in_background"):
        reason = ci_wait_reason(segs)
        if reason:
            print(CI_WAIT_MESSAGE, file=sys.stderr)
            print(f"Refused because {reason}.", file=sys.stderr)
            return BLOCK
    return ALLOW


if __name__ == "__main__":
    sys.exit(main())
