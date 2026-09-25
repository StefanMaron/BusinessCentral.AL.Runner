# Never background a long-running command and end your turn

A backgrounded process is killed when the turn ends: no completion notification arrives, the
work sits uncommitted, and you wait forever on something already dead. This applies to **any**
long command **that runs on this box** — corpus runs, repeat-iteration flake loops,
`dotnet test` sweeps, provisioning, artifact downloads. Run it in the **foreground** with a
correspondingly generous timeout, and never chain short sleeps to fake a wait.

**Commit and push before you start anything long.** A push is the only thing that makes your
work survive a turn ending unexpectedly, and it gets CI working in parallel with you.

A cold full-corpus run (build + AL emit + C# compile + execute every test) is not a
few-seconds operation — budget several minutes, or use a compile cache where one is available.

**CI is the one thing you never wait for, in the foreground or anywhere else** — a workflow run
is not your child process; `ci-verdicts.md` §0 owns how and when to read its verdict. This rule
governs work running locally.

## Nothing earns you a wake-up on a `Bash` call you started

One mechanism — a child process of your turn dies with your turn — in three shapes:

- **"Don't poll, wait for the notification"** is written for subagents dispatched with the
  `Agent` tool, which genuinely notify. A background `Bash` task you started inside your own
  turn is not one.
- **`run_in_background: true`** makes the process a detached child, not a subscription. No
  flag, wrapper or phrasing of a `Bash` call earns you a wake-up.
- **The harness backgrounding it FOR you**, with a message saying you will be notified. That
  promise does not hold for anything started inside your own turn — and this shape is not
  optional, so asking for the foreground does not avoid it. The harness moves any foreground
  `Bash` call to the background at a hard **600s** cap, which the call's own `timeout` field
  does not raise. Measured in #4288: **every** auto-backgrounded CI wait had
  `run_in_background` unset. The notification you
  then get reports the **wrapper's** exit status, not the tool's — `ci-verdicts.md` §0 owns
  what that does to a verdict.

A `PreToolUse` hook refuses a CI wait on the **duration it asks for**, not on the flag —
`.claude/hooks/refuse-stash-and-ci-waits.py`, which reads `gh run watch`,
`gh pr checks --watch`, `ci-wait.py` whose `--timeout` is absent or at/above the 600s cap,
and a sleep loop polling CI as that shape. A `--timeout 0` read, a value under the cap, and a
backgrounded local run that is not a CI wait all stay allowed (#3707, #4288). Trap: gating that
refusal on `run_in_background` is what made it refuse none of them.

**If you are about to end a turn while local work you launched is still running, that is the
bug.** Correct shapes, in order of preference: run it in the foreground; push first so the loss
is survivable; or genuinely abandon it and say so. "Start it, end the turn, and wait" is not on
the list.

## Sister rules

- `ci-verdicts.md` — driving a PR to merge, and why CI is read rather than waited for
- `no-git-stash-with-worktrees.md` — why a hand-rolled polling loop matches itself

History: docs/incidents/no-backgrounding-long-commands.md
