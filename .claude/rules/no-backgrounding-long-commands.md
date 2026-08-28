# Never background a long-running command and end your turn

A backgrounded process is killed when the turn ends, no completion
notification arrives, and the work sits uncommitted. You will then wait
forever on something that is already dead. This applies to **any** long
command — corpus runs, repeat-iteration flake loops, `dotnet test` sweeps,
provisioning, artifact downloads, long `gh`/API polling. Run it in the
**foreground** with a correspondingly generous timeout. Do not chain short
sleeps to fake a wait either — either wait on the foreground command or truly
move on.

A cold full-corpus run (build + AL emit + C# compile + execute ~2000 tests)
is not a few-seconds operation — budget several minutes, or use a compile
cache to skip recompilation on repeat runs where one is available.

**Commit and push before you start anything long.** A push is the only thing
that makes your work survive a turn ending unexpectedly, and it gets CI
working in parallel with you instead of after you.

**"Don't poll, wait for the notification" does not apply to a `Bash` call you
started.** That guidance is written for an orchestrator waiting on subagents
it dispatched with the `Agent` tool — those genuinely notify. A background
`Bash` task you started inside your own turn is your child: it dies with your
turn, and no notification will ever arrive. Multiple agents have stalled
mid-task reasoning "I'll stop polling and resume when the notification
comes." It will not come. If you catch yourself about to end a turn while
something you launched is still running, that is the bug, not patience.

**`run_in_background: true` on a `Bash` call does not change this.** That flag
makes the process a detached child of your turn; it is not a subscription to
anything. The notifying kind of background work is the `Agent` tool, which
you may not have. There is no flag, wrapper, or phrasing of a `Bash` call that
earns you a wake-up.

**Nor does the harness backgrounding it FOR you.** A long foreground command
can be moved to the background out from under you, together with a message
saying you will be notified when it completes. That promise does not hold for
a process started inside your own turn — whatever produced it. This has already
cost a stall: an agent correctly ran `gh run watch` in the foreground, the
harness backgrounded it and promised a notification, and the agent ended its
turn waiting for one that could never arrive.

When that happens, do not end your turn on the strength of the promise. Re-check
the thing directly and keep checking in the foreground:

```bash
gh run view <run-id> --json status,conclusion
```

Anything other than `completed` means "not yet reported", never "green".

The correct shapes, in order of preference: run it in the foreground; or push
first so the loss is survivable and let CI be the verdict; or genuinely
abandon it and say so. "End the turn and wait" is not on the list. Of every
documented stall this caused, the ones that cost real work were the ones with
an unpushed worktree — an agent that had pushed lost a turn; an agent that
had not lost the change.
