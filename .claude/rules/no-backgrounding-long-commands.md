# Never background a long-running command and end your turn

A backgrounded process is killed when the turn ends: no completion notification arrives,
the work sits uncommitted, and you wait forever on something already dead. This applies to
**any** long command — corpus runs, repeat-iteration flake loops, `dotnet test` sweeps,
provisioning, artifact downloads, long `gh`/API polling. Run it in the **foreground** with a
correspondingly generous timeout. Do not chain short sleeps to fake a wait — either wait on
the foreground command or truly move on.

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
  agent correctly ran `gh run watch` in the foreground, the harness backgrounded it and
  promised a notification, and the agent ended its turn waiting for one that could never arrive.

If you catch yourself about to end a turn while something you launched is still running, that
is the bug, not patience. Three agents lost their work this way in a single day, each having
reported "CI is running, I'll confirm." Re-check directly and keep checking in the foreground:

```bash
gh run view <run-id> --json status,conclusion
```

Anything other than `completed` means "not yet reported", never "green".

Correct shapes, in order of preference: run it in the foreground; or push first so the loss is
survivable and let CI be the verdict; or genuinely abandon it and say so. "End the turn and
wait" is not on the list.

For CI specifically, `tools/ci-wait.py <PR>` does the whole poll inside one tool call and
returns a single verdict — see `ci-verdicts.md` for its exit codes.

## Sister rules

- `ci-verdicts.md` — driving a PR to merge; `tools/ci-wait.py` and its exit codes
- `no-git-stash-with-worktrees.md` — why a hand-rolled polling loop matches itself
