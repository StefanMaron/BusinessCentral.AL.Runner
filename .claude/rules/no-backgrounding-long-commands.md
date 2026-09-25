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
- **The harness backgrounding it FOR you**, with a message saying you will be notified — a
  promise that does not hold inside your own turn, and not optional: any foreground `Bash` call
  is backgrounded at a hard **600s** cap that its own `timeout` field does not raise, with
  `run_in_background` unset (#4288). The notification then reports the **wrapper's** exit
  status — `ci-verdicts.md` §0 owns what that does to a verdict.

`.claude/hooks/refuse-stash-and-ci-waits.py` refuses a CI wait on the **duration it asks for** —
`gh run watch`, `gh pr checks --watch`, a sleep loop polling CI, or `ci-wait.py` with no
`--timeout` or one at/above the cap — and allows `--timeout 0` and local background runs
(#3707, #4288). Trap: gating it on `run_in_background` made it refuse nothing.

**If you are about to end a turn while local work you launched is still running, that is the
bug.** Correct shapes, in order of preference: run it in the foreground; push first so the loss
is survivable; or genuinely abandon it and say so. "Start it, end the turn, and wait" is not on
the list.

## Sister rules

- `ci-verdicts.md` — driving a PR to merge, and why CI is read rather than waited for
- `no-git-stash-with-worktrees.md` — why a hand-rolled polling loop matches itself

History: docs/incidents/no-backgrounding-long-commands.md
