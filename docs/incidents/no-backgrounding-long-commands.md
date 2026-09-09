# Incidents behind .claude/rules/no-backgrounding-long-commands.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## Commit and push before you start anything long

**Commit and push before you start anything long.** A push is the only thing that makes your
work survive a turn ending unexpectedly, and it gets CI working in parallel with you. Of
every documented stall this caused, the ones that cost real work were the ones with an
unpushed worktree — an agent that had pushed lost a turn; an agent that had not lost the
change.

## Nothing earns you a wake-up on a `Bash` call you started

- **The harness backgrounding it FOR you**, with a message saying you will be notified. That
  promise does not hold for anything started inside your own turn. It already cost a stall: an
  agent ran `gh run watch` in the foreground — then the correct move, now superseded by not
  waiting on CI at all — the harness backgrounded it and promised a notification, and the agent
  ended its turn waiting for one that could never arrive. The mechanism is what matters here and
  it is unchanged: it applies to any long local command you background.

If you catch yourself about to end a turn while **local** work you launched is still running,
that is the bug. Three agents lost work this way in a single day, each having reported "CI is
running, I'll confirm" — and note what actually cost them: not that they stopped watching CI,
but that they ended a turn with an **unpushed worktree**. Pushing first is what would have
saved every one of them, and it is the fix here rather than a longer wait.
