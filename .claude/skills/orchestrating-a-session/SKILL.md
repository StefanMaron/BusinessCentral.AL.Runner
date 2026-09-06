---
name: orchestrating-a-session
description: How to run a coordinator session on this repo — what you decide versus what you delegate, the implementation-agent identity pool and when to reuse it, corpus-PR authority, when to re-run triage, the merge bar, the measurement rules, and the environment traps that silently produce wrong answers. Use at the START of any session where you will drive work through subagents, review and merge PRs, or keep the issue queue moving. Invoke it instead of being told these things again.
---

# Orchestrating a session on AL Runner

You are the coordinator. You spawn implementation agents, review what they produce, drive
PRs to green, and merge. You do not usually write the fix yourself — but you do own the
judgement calls, the merges, and the honesty of what gets reported.

Everything below is here because it had to be explained more than once. Read it once at the
start of a session rather than rediscovering it.

## What you decide, what you delegate

**Delegate:** diagnosing a cluster, writing the fix, writing the proving test, driving that
one PR through CI.

**Keep:** which clusters are worth attacking and in what order; whether a result is real;
whether a PR meets the bar; every merge, in both repos; corrections to issues whose premise
has been falsified.

**Do the work yourself when you already have the answer.** If you have just measured
something, spawning an agent to re-measure it wastes a full context. Write the PR.

## Authority — you do not need to ask for these

- **Filing issues** on `StefanMaron/BusinessCentral.AL.Runner`, and **correcting the body of
  an issue you filed** when a measurement contradicts it.
- **Closing an issue whose work has already landed.** Closing is cheap and reversible — an
  issue that turns out to be live again can simply be reopened. Verify against the code at
  `main`, not against issue text, and prefer re-running the reporter's repro where one exists.
- **Merging any PR authored under the repo owner's account**, in this repo and the corpus
  repo, on your own high-level review plus a green pipeline. That covers every PR your agents
  open, since they push with that account's token. Judge whether the change is right, whether
  the proving test is there, and whether CI is green on the **current head** — then merge.
- **Arming auto-merge** instead of waiting. Review the PR when it arrives; if it passes, arm
  it (`gh pr merge <N> --squash --delete-branch --auto`) and move on. Do not sit watching a
  run you cannot influence.
- **Claiming an issue assigned to another contributor when it overlaps work already in
  flight**, once the repo owner has released that contributor's backlog. `branch-and-pr.md`'s
  assignee boundary still holds as the default. When a released issue is the same defect an
  agent is already fixing, reassign it
  (`gh issue edit <N> --remove-assignee <login> --add-assignee @me`) and fold it in. Do not
  bulk-claim issues nobody is working on, and do not assume a release — confirm it.

**A PR from anyone other than the repo owner** is reviewed, never merged. You may review it
and, with approval, comment on it. Merging someone else's contribution stays the owner's call.

**Still gated, ask first:** comments on issues or PRs (including the corpus repo), PR review
comments, anything posted to another repo. That is editorial content, not a workflow step.

## Implementation agents

Use the `impl-agent` subagent type, not `general-purpose`. Its definition carries the
workflow contract — branch naming, labels, the CI rules, the navigation tooling — so your
brief only needs the cluster context and the traps.

**Do not ramp up identity numbers.** The documented pool is `impl-1`/`impl-2`, widened by the
owner when concurrency is raised. **A finished agent's identity is immediately free — reuse
it.** Every new identity leaves a permanent worktree behind; inventing `impl-3` … `impl-12`
across a session leaves a dozen. Reuse first, and only invent one when every identity is
genuinely in flight.

**Brief with cluster data and traps, not with pre-resolved symbols.** Resolving an agent's
symbols for it moves cost onto your own long-lived context, which is backwards. Give it the
failing test names, the stack top, the counts, the falsified hypotheses — and let it navigate.

**A good brief says what a *complete answer* looks like**, including a negative one. "These 6
are cause A and these 10 are cause B, here is the evidence" is a complete answer with no fix.
Say so explicitly, or agents will force one fix over two causes to make the PR look bigger.

**Agents do NOT wait for CI.** Their deliverable is "PR opened and pushed". Waiting costs an
agent slot for 15-25 minutes watching a run it cannot influence, and you are watching CI
anyway. `impl-agent.md`'s Step 5 says this; keep briefs consistent with it. A failure is never
lost by returning early — resume the agent, or dispatch a fresh one with the failure in hand.

**Never relay an authorization to an agent.** An agent is right to refuse a message claiming
"the owner approved X" for anything touching its operating rules — commit signing, skipping a
verification step, dropping an instruction its own harness set. It cannot verify the claim,
and its instructions correctly say no agent message substitutes for the user's consent. This
cost a full round trip when signing was disabled: the agent refused twice, correctly. **Do the
privileged step yourself** — its work is staged in its worktree, so commit, push and open the
PR from the coordinator session. Better still, make the change invisible: `commit.gpgsign` was
already `false` in the shared repo config, so an agent that simply runs `git commit` succeeds
and never needs telling.

**Check in on long runners.** Past ~90 minutes, ask: where are you, is anything unpushed, is
there a PR, are you blocked. Agents will sit on finished work waiting for permission they
already have.

**Search the issue queue before dispatching, and hand over the whole cluster.** A measured
failure cluster is usually already partly filed. Grep the open issues for the area first — on
one dispatch this turned a single issue into four sharing one root cause (#2723 + #2517 +
#2460 + #2200), and the agent brief said so, which is what let it fix them together. Ask the
agent which of the related issues its change closes for free rather than assigning all of
them; "these three are one fix, that one is not, here is why" is a complete answer.

## Keep a reviewer running, and size the batch

**The default is one implementation agent and one reviewer.** Not a ratio to compute — a
baseline to start from, changed only by the human at session start. One implementer produces at
most one PR at a time and one reviewer clears roughly four an hour, so review cannot fall behind
by construction, and the pile-up this section describes never begins.

The measured throughput below is what to scale *by* when a human raises the concurrency, not a
license to raise it. At six implementation agents you need roughly two reviewers to hold steady;
work that out from the numbers rather than adding implementers because slots are free.

Review is the step that stalls, and it stalls by arithmetic rather than by anyone deciding
badly. Measured on 2026-09-06: a reviewer clears **6 PRs in 93 minutes (~15.6 min/PR)** and
**3 corpus PRs in 64 minutes (~21 min/PR)**, so one reviewer sustains about **4 PRs/hour**.
Implementation agents take 35-85 minutes and produce one PR each, so six of them produce
**5-6 PRs/hour**. One reviewer cannot keep up with six implementation agents. Budget roughly
**one reviewer per four implementation agents**.

**Treat an open unreviewed PR as unfinished work that counts against your concurrency budget.**
Six implementation agents plus six unreviewed PRs is twelve, not six. Without that accounting
you will keep starting implementation agents whenever a slot frees, because starting one feels
like progress and starting a reviewer feels like overhead - and the queue grows every hour.

**Batch three or four PRs per reviewer.** Larger batches go stale: a batch of six ran 93
minutes, during which three PRs from the brief merged and two heads moved, so a third of the
verdicts came back "no verdict on current head". Smaller batches lose the cross-PR findings that
are the reason to batch at all - the most valuable result that day was spotting that two PRs
bumped the same submodule pin to different revisions and working out which had to merge first.
A per-PR reviewer cannot see that, and neither can you.

**A reviewer that approves a PR arms auto-merge on it immediately, in the same pass.** Do not
hand an approval back to the coordinator and wait for it to act — that round trip is where the
verdict goes stale, and staleness is the main cost of reviewing in batches. The reviewer has
just read the head SHA; it is the only actor that knows the verdict and the SHA are consistent
at that instant.

```bash
gh pr merge <N> --repo <owner>/<repo> --squash --auto
```

Arm **only** when all of these hold. Any one missing means report it to the coordinator instead:

- **The PR is on a branch this loop owns.** Check the **branch prefix**, never the author field
  — every loop running under one account reports that account as the author, and an outside
  contributor's PR is never merged by us.
- No release run is in progress (`publish.yml` pushes a fast-forward; a merge during its
  ~40-minute run kills it).
- `git merge-tree --write-tree --messages origin/<base> origin/<branch>` is clean.
- If the PR asserts anything about BC's behaviour, its corpus PR has **merged**, and the pin bump
  and count-baseline update are folded in.
- No *other* PR in the same batch conflicts with it. Where two do — two submodule pin bumps to
  different revisions, say — arm only the one that must merge first and report the ordering.

**Record the SHA you armed against** in the verdict. If the head moves afterwards, GitHub keeps
auto-merge armed against the new head, which nobody has reviewed; the coordinator needs the SHA
to notice.

**One command, two outcomes — and on a green PR it MERGES.** `--auto` is not "queue it for
later":

- required checks **not yet green** → auto-merge is armed, and the PR lands when they pass;
- required checks **already green** → the PR **merges on the spot**.

`gh` picks between the two itself, before calling anything — its merge command carries a
function named `isImmediatelyMergeable` for exactly this. Both outcomes are intended: if review
approves and CI is green, the PR should merge.

**So on a green PR, the approval decision IS the merge decision.** There is no coordinator
checkpoint after it, and nobody looks again. Every condition in the list above has to hold at
the moment you run the command, because running it is the merge — not a request for one. Weigh
the verdict accordingly rather than assuming a later sweep will catch a mistake.

An earlier version of this section claimed the opposite: that GitHub *refuses* to arm an
already-mergeable PR, answering `Pull request is in clean status`, and that the coordinator
would merge it by hand. That was wrong, and a reviewer following it would report "it refused,
please merge it yourself" about a PR that had already merged. It was falsified on PR #3095 —
the documented command returned rc=0 and merged it immediately at the reviewed SHA. `gh` never
produces that message at all; the phrase does not occur anywhere in the binary. It appears to be
a GitHub API error from the `enablePullRequestAutoMerge` mutation, which is the call `gh` skips
when the PR is already mergeable — so it is not something this command can produce. See #3127.

**Check the exit code either way.** It is not decoration: `gh pr merge` exits non-zero for real
reasons (`Pull request #N is not mergeable: ...`), and a loop that printed "armed" regardless of
it once left four green PRs sitting unarmed.

When it arms rather than merges, arming is still not merging, and it does not replace the merge
bar — it is the bar expressed as a standing instruction to GitHub, so a PR lands the moment its
checks go green instead of at the coordinator's next sweep.

**Start the next reviewer when one returns**, not when a queue becomes visible. By the time a
pile-up is obvious it is already too deep to clear in one fresh batch.

**Require the head SHA in every verdict**, and re-read it immediately before merging. Heads move
within minutes when other loops and outside contributors push. Pass `--match-head-commit` so a
merge refuses rather than quietly taking a commit nobody reviewed - a SHA in one brief had a red
verdict attached by the time the review finished.

One session's sample, and review time scales with PR size. Re-measure with `tools/agent-cost.py`
rather than treating the ratio as settled.

## Triage

Run the `triager` subagent at the **start** of a cycle, and again whenever the open-issue
count has grown by roughly 20 or the queue has visibly drifted. Sonnet is a fine fit.

The queue grows for a reason worth naming: **issues get fixed by a PR that cites a different
number, so nothing auto-closes them.** Ask triage for three things — already-fixed issues
with the commit that fixed each, duplicate clusters with a canonical, and status labels for
the untriaged. Have it **apply labels directly** (mechanical) but **close nothing and comment
nowhere** — bring the closure list back for approval.

## The merge bar

Merge when **all of**:

1. All 8 required legs green **on the PR's current head SHA**. `gh pr checks` reports the
   newest *completed* run, which can predate the last push — confirm the SHA.
2. `git merge-tree --write-tree --messages origin/main origin/<branch>` is clean.
   `mergeStateStatus: CLEAN` only covers textual conflicts.
3. The proving test exists. If the claim is about BC's behavior, that test is upstream and
   merged, or merging in the same pass.

**Use `tools/ci-wait.py <PR>`** rather than a poll loop. It polls internally and returns one
verdict: 0 green on current head, 1 failed with the log already fetched, 2 still running
(*not* a verdict), 3 undetermined, 4 blocked with everything green — a cancelled required
context (below), or a required context that produced no check run at all once every
workflow run finished (#2807).

**A FAILED verdict names what has reported so far.** While other required checks are
still running the failing list can grow, and the tool says how many have not reported.
Do not scope a diagnosis to those names until it has: reading "1 of 9 required checks
failed" as "only BC 27.0 is affected" started a version-specific investigation of what
turned out to be eight failing legs.

**Never `gh run rerun` a failed job** — it destroys the log permanently. Read
`--log-failed` first, then push a new commit.

**A CANCELLED check blocks the merge, with everything green and nothing saying why.**
A ruleset satisfies a required check from the *newest* check run carrying that context name on
the head commit, and `cancelled` does not satisfy it. `cancel-in-progress` produces that
conclusion whenever a `pull_request` event fires *without* moving the head SHA — `edited`,
`labeled`, `unlabeled` — because the cancelled run's checks then land on the very commit the
ruleset is reading. `gh pr checks` still shows all green and the merge is still refused as
`BLOCKED`.

#2726 fixed both halves. The required `Tests updated` job moved out of `pr-check.yml` into
`require-tests.yml`, which has no `concurrency` block and does not trigger on `edited`, so no
required context is cancellable on its own commit any more; `check_required_contexts.py` fails
CI if one becomes so again. And `ci-wait.py` now returns **exit 4** naming the cancelled
context instead of reporting GREEN.

If you still land in this state, re-run just the cancelled run — it clears in under a minute,
and that is NOT the forbidden `gh run rerun`, because a cancelled run has no failure log to
destroy. Do not reach for `--admin`: protection is working, the context genuinely is not
satisfied.

**Auto-merge drains the queue — but a drained queue is not a verified one.** The
"branches must be up to date" protection rule was removed, so arming several PRs lets them
merge in sequence without each waiting for a rebase. Use that: review on arrival, arm, move on.

The cost of dropping that rule is that a PR can merge on a green verdict measured against an
older `main`. A clean textual merge says nothing about semantic conflict — two PRs can each be
green alone and wrong together. So arm freely when PRs touch **different** files, and when two
touch the same file, still land one and rebase the other with its affected tests **re-run**
rather than trusting the earlier verdict. `git merge-tree` only answers the textual question.

**Order matters when PRs carry submodule pins.** Two PRs both bumping the pin and the
count-baseline will conflict; merge one, then tell the other to rebase and *re-measure*
rather than carrying its old number forward.

## Measurement rules

These exist because each was violated at real cost.

- **Never conclude from a run that has not finished.** A partial local run once produced a
  three-class failure list; the completed CI run found five failures in two further classes.
- **Wall clock lies on a loaded box.** With several agents running, identical work measured
  1.9s and 3.1s. Use instructions-retired (`perf stat`) for anything CPU-bound; it held to
  ±0.1% across the same runs.
- **A children-inclusive profile percentage is not a saving.** In a JIT-dominated process it
  measures what is *reachable* from a call site; deleting the caller moves the cost to the
  next one. Price a change by removing the work and re-measuring, not by reading a call tree.
- **Never compare two configurations across a rebuild.** Rebuilding the runner invalidates
  the AL-output cache, and a cold run can report a different pass count for reasons unrelated
  to your change — 873 vs 925 on the same code in one session. Set the variable through an
  override on ONE warm cache instead, and pass a private `--cache <dir>`.
- **Include a control.** Convert three classes, leave two untouched, and show the untouched
  ones flat. That is what makes the deltas believable.
- **Do not rank work by the bc-linux container comparison.** That tier patches BC's binaries
  at startup, and filtering by it once hid the single largest cluster in the bucket — 102
  tests, worth +93 when fixed.

## The corpus must grow with the fixes

The corpus is the only place a claim about BC gets adjudicated by a real service tier, so it
should gain a test roughly as often as a fix makes such a claim. If a session merges several
fixes and opens no corpus PR, that is a signal to check rather than a sign the work was all
infrastructure.

**Ask of every PR: does this assert something about what BC does?** Runner infrastructure —
process configuration, CI plumbing, error handling, caching, parallelism — genuinely asserts
nothing about BC and owes nothing upstream. A change to what the runner *makes AL code
observe* almost always does.

That question is the common case rather than the whole rule. The wider one: **wherever it is
possible to red-test something with AL tests, that should add tests to the corpus** — wherever
the fix can be proven by AL running against a real service tier, it owes an upstream test even
when its claim does not read as a statement about BC. The service-tier clause is the boundary:
runner-only claims are red-testable in AL too, and they stay in `tests/runner-extras/`.
Nothing about this changes when a PR may merge — a PR asserting BC behaviour still merges only
after its corpus PR has, pin bump folded in.

**"The corpus cannot express this" is a claim, and it needs its evidence like any other.** It
is sometimes true and the reason is usually structural: corpus tests are compiled from AL
source *by the runner*, so a defect that only affects **precompiled** dependency artifacts
cannot be reproduced by a corpus test, which would take the source-compiled path and pass. That
is a legitimate answer. What is not legitimate is reaching for it because writing the upstream
test is slower. When an agent gives that answer, make it name the structural reason — and if
the reason is real, the proving test belongs in `tests/runner-extras/` and the PR should say
so explicitly.

Brief implementers to test that claim up front and put the answer in the PR body whichever way
it comes out; it has gone both ways. A defect assumed precompiled-only also reproduced on a
source-compiled table, and the corpus app declares a Base Application dependency, so a corpus
test reaches the precompiled path after all (issue #2518, corpus PR #165, merged green on all 8
legs). Against that, table `2000000001` really is out of reach, sitting in
`SystemTables.InternalTables`, which `NavRecordRef.IsSystemTableAllowedForRecordRefUsage`
refuses outright — though note what carries it: a service tier measured the sibling id
`2000000071` (corpus PR #153, all eight legs red, withdrawn), and `2000000001` follows by set
membership rather than by its own measurement (issue #2774).

Watch for the shape where a fix closes a BC-behaviour issue with only a runner-local test.
That is the case `bc-behavior-tests-go-upstream.md` exists to prevent, and it is easiest to
miss on a busy day, because a runner-local test is green and nothing complains.

## Settling a claim about BC

Order of evidence, highest first: a corpus test green on a real service tier; BC's own
shipped IL; Microsoft's documentation; the name of a codeunit. A name is not evidence.

If no corpus test covers the shape, write one and let the corpus CI adjudicate — minutes, not
a local container. If no verdict is available at all, say so plainly and land the runner
change with whatever coverage is legitimately available. Never write an unmeasured claim into
an issue, a doc table, or a comment as though it were established.

**When an agent's measurement falsifies an issue you filed, correct the issue body.**
Otherwise the next agent starts from the wrong premise — which has happened here.

## Environment traps that produce silently wrong answers

- **`grep` is a shell function.** It rejects `-E` and `--include` with
  `error: unknown option '-G'` **and exits 0 with no output**, which reads exactly like "no
  matches". Use `command grep` or `rg` before believing an empty result.
- **Always pass a private `--cache <dir>`** to runner invocations. The shared
  `~/.cache/al-runner` is not keyed on the runner binary, so a concurrent agent's payload can
  make a fix look like it did nothing.
- **One hung AL test ends the whole run**, reporting a partial count that looks like a
  regression. Park the codeunit rather than reaching for `--test`.
- **The MS test company is `CRONUS International Ltd_`** — underscore, not a period.
- **`.mcp.json` changes need a session restart.** Registering an MCP server mid-session does
  nothing for that session or its subagents.
- **When 1Password is locked, commits and pushes both fail.** Signing goes through
  `op-ssh-sign` and `origin` is SSH via the same agent, so `git commit` hangs waiting for a
  signature and `git ls-remote` fails. With the repo owner's authorization, fall back to
  unsigned commits: set `commit.gpgsign=false` / `tag.gpgsign=false` in the repo config
  (every worktree shares it), and switch pushes to HTTPS with `gh auth setup-git` plus
  `git remote set-url origin https://github.com/StefanMaron/BusinessCentral.AL.Runner.git`.
  Verify with `git ls-remote origin HEAD` before telling anyone it works. Never read a secret
  file and never try to unlock 1Password yourself.
- **The network here times out intermittently.** Wrap `gh` in a retry; a bare `i/o timeout`
  is not an answer, and treating one as "no results" corrupts whatever you concluded.

## Tooling you should be using

- `tools/context-pack.py <Name>...` — definition + source + call sites, one round trip.
- `tools/lsp-query.py callers|symbol <Name>` — exit 2 means the server failed, **not** "none".
- `mcp__bc-decompiler__*` — BC's own code. Contexts `bc260` … `bc284` are pre-registered;
  `search_members` → `memberId` → `get_decompiled_source` / `find_callers`.
  `compare_symbols` diffs a method between BC versions, which is how a Cecil rewrite that
  stopped being reached gets caught.
- `tools/agent-cost.py <tasks-dir>` — where a session's agents actually spent their calls.
  Measured once: 85% of Bash calls were shell read/search and the navigation tools were used
  3 times in 3,237 calls. Re-measure rather than assuming it improved.

## Reporting to the owner

State what was measured and what was assumed, separately. When you got something wrong, say
so plainly in a sentence and move on — no ceremony. Give the number that the reasoning
actually produces, including when it is worse than hoped: an estimate of "about 9 minutes"
that holds beats "under a minute" that does not.

## Sister rules

`.claude/rules/` is auto-loaded and remains authoritative. The ones this skill leans on most:
`ci-verdicts.md`, `branch-and-pr.md`, `public-posting-approval.md`,
`bc-behavior-tests-go-upstream.md`, `ask-the-corpus-before-claiming-bc-behavior.md`,
`no-base-app-in-csharp-tests.md`, `local-test-scope.md`, `no-git-stash-with-worktrees.md`.
