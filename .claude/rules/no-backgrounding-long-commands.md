# Never background a long-running command and end your turn

A backgrounded process is killed when the turn ends: no completion notification arrives,
the work sits uncommitted, and you wait forever on something already dead. This applies to
**any** long command **that runs on this box** — corpus runs, repeat-iteration flake loops,
`dotnet test` sweeps, provisioning, artifact downloads. Run it in the **foreground** with a
correspondingly generous timeout. Do not chain short sleeps to fake a wait — either wait on
the foreground command or truly move on.

**CI is the one thing you never wait for, in the foreground or anywhere else.** A workflow run
is not your child process: it completes whether or not this turn is alive, and its verdict is
there to read whenever you come back. So push, open the PR, and move on — then read the result
later with `tools/ci-wait.py <PR> --timeout 0`. `ci-verdicts.md` §0 is the rule; this one
governs work running locally.

A cold full-corpus run (build + AL emit + C# compile + execute ~2000 tests) is not a
few-seconds operation — budget several minutes, or use a compile cache to skip recompilation
on repeat runs where one is available.

**Commit and push before you start anything long.** A push is the only thing that makes your
work survive a turn ending unexpectedly, and it gets CI working in parallel with you. Of
every documented stall this caused, the ones that cost real work were the ones with an
unpushed worktree — an agent that had pushed lost a turn; an agent that had not lost the
change.

## Nothing earns you a wake-up on a `Bash` call you started

Three shapes of the same mistake, one mechanism — a child process of your turn dies with your
turn:

- **"Don't poll, wait for the notification"** is written for an orchestrator waiting on
  subagents dispatched with the `Agent` tool; those genuinely notify. A background `Bash` task
  you started inside your own turn is not one. Agents have stalled reasoning "I'll stop polling
  and resume when the notification comes." It will not come.
- **`run_in_background: true`** makes the process a detached child, not a subscription. No
  flag, wrapper, or phrasing of a `Bash` call earns you a wake-up.
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

Correct shapes for local work, in order of preference: run it in the foreground; or push first
so the loss is survivable; or genuinely abandon it and say so. "Start it, end the turn, and
wait" is not on the list.

For a pull request, the correct shape is different and simpler: push, open it, hand back. Read
the verdict on a later pass with `tools/ci-wait.py <PR> --timeout 0`, which answers at once and
refuses to call a still-running check a result — see `ci-verdicts.md` for the exit codes.

## Sister rules

- `ci-verdicts.md` — driving a PR to merge, and why CI is read rather than waited for
- `no-git-stash-with-worktrees.md` — why a hand-rolled polling loop matches itself
