#!/usr/bin/env python3
"""PreToolUse refusal: the coordinator is about to dispatch a subagent while its own
cwd is inside an agent worktree (#4534).

A dispatched agent starts in whatever directory the dispatching session's shell last
held. Four times that was another identity's live worktree (#4340, #4534) -- once the
worktree of the very PR the dispatched reviewer was reviewing, so a missed restore
would have edited the branch under judgement. The dispatched agent can only DETECT
this (`tools/preflight.py --agent-id`, its `branch-ownership` row); the dispatcher is
the only actor that can PREVENT it, and the condition is readable at exactly that
instant from the payload's `cwd`.

Blocks (exit 2) only the MAIN session: a payload carrying `agent_type` is a subagent
dispatching its own helper, and a helper inheriting that agent's own worktree is
correct. Also allowed without the escape: a dispatch requesting its own worktree
(`isolation: "worktree"`), which does not inherit the cwd; and a session LAUNCHED in
that worktree (Claude Code's `--worktree`), recognised by `$CLAUDE_PROJECT_DIR`
naming the same `.claude/worktrees/<name>` -- the tree is the session's own, not
another loop's. The per-call escape is the marker `dispatch:allow-worktree-cwd` in
the prompt, for any other dispatch deliberately meant to work in that tree.

Tested by tools/test_agent_workflow_hooks.py (pr-gate.yml globs `tools/test_*.py`).
"""
import json
import os
import re
import sys

BLOCK = 2
ALLOW = 0

DISPATCH_TOOLS = {"Agent", "Task"}
ESCAPE = "dispatch:allow-worktree-cwd"
# Both separators: the cwd arrives Windows-shaped on a Windows box.
WORKTREE = re.compile(r'^(?P<root>.*?)[\\/]\.claude[\\/]worktrees[\\/](?P<name>[^\\/]+)')


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception as exc:
        # Fail open (a payload-format change must not refuse every dispatch), but say so.
        print(f"refuse-dispatch-from-worktree: could not read the hook payload "
              f"({exc.__class__.__name__}); not checking this dispatch.", file=sys.stderr)
        return ALLOW
    if payload.get("tool_name") not in DISPATCH_TOOLS:
        return ALLOW
    if str(payload.get("agent_type") or "").strip():
        return ALLOW
    tool_input = payload.get("tool_input") or {}
    if ESCAPE in str(tool_input.get("prompt") or ""):
        return ALLOW
    if str(tool_input.get("isolation") or "").strip().lower() == "worktree":
        return ALLOW
    cwd = str(payload.get("cwd") or "") or os.getcwd()
    m = WORKTREE.match(cwd)
    if not m:
        return ALLOW
    launched = WORKTREE.match(os.environ.get("CLAUDE_PROJECT_DIR", ""))
    if launched and launched.group("name") == m.group("name"):
        return ALLOW
    root = m.group("root") or "/"
    print(
        f"Refusing to dispatch a subagent from inside the agent worktree "
        f"`.claude/worktrees/{m.group('name')}` (#4534).\n"
        f"The dispatched agent inherits this cwd, so it would start in that loop's tree: "
        f"a commit made there lands on that loop's branch (#3014), and a reviewer's "
        f"mutate-and-restore edits the branch it is judging.\n"
        f"Fix: `cd {root}` (a neutral checkout), then dispatch again.\n"
        f"If this dispatch is MEANT to work in that tree, put `{ESCAPE}` in the prompt, "
        f"or dispatch with `isolation: \"worktree\"`.",
        file=sys.stderr)
    return BLOCK


if __name__ == "__main__":
    sys.exit(main())
