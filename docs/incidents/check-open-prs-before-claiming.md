# Incidents behind .claude/rules/check-open-prs-before-claiming.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## The three collisions this rule is made of

All within about four hours on 2026-09-05, all under one account.

1. **Duplicate dispatch onto #2780.** A coordinator checked the assignee, saw the account's own
   name, and dispatched. PR #2863 (`agent: impl-7`) had been open with `Closes #2780` the whole
   time. Caught by the repo owner, not by the protocol.
2. **Duplicate dispatch onto #2755.** Same check, same result. PR #2873 (`agent: impl-2`) had
   been open with `Closes #2755` since 18:37. Two agents worked one defect; one produced a
   branch that will never become a PR.
3. **A foreign `agent:` label removed as stale.** Claiming #2755, an agent found `agent: impl-2`
   already there, read it as left behind by a finished loop — a *different* agent had posted a
   release comment on the thread, and the assignee did not discriminate — and replaced it in a
   single `gh issue edit`. It was live.

Instance 3 is the one to worry about: recovery depended on the agent remembering its own edit
and repairing it. A crashed session would have erased another loop's claim silently, and
nothing in the protocol would have noticed.

## What the protocol got right

Every one of these was a **read that was too narrow, not a write that raced**. The
compare-and-swap on claiming — assign, then re-read, release if someone else's claim appeared —
worked exactly as specified, in a design whose whole point is loops that share no state and
never talk to each other. That design held. This rule widens the read; it does not replace the
lock.

## `closingIssuesReferences` lags PR creation (2026-09-13, PR #4119)

An implementation agent created PR #4119 through the REST endpoint
(`gh api .../pulls -X POST`, after `gh pr create` failed twice with a bare GraphQL 500 during
the Actions outage of #4110). Its handback check read `closingIssuesReferences` immediately and
got `[]`, despite a correct `Closes #4111` in the body. The field resolved to `[4111]` roughly
**twelve seconds** later.

Nothing distinguishes the pending state from the settled one: a PR that genuinely closes no
issue returns the same empty array, and no field says "not parsed yet".

The consequence is specific to this rule rather than cosmetic. The claiming check keys on
exactly that field — a non-empty result means the issue is taken. An empty read on a
seconds-old PR therefore reports **free** for an issue that was just claimed, and the
compare-and-swap this rule sits inside cannot save you, because both agents read the same
empty answer.

Whether the lag is specific to REST-created pull requests or applies to `gh pr create` too was
not established; the sample is one PR, created by the REST route because the GraphQL one was
failing. The remedy in the rule does not depend on the answer: the body carries the declaration
immediately either way, so a zero result confirmed against the body is correct under both.

## The review-side signal: 31% of PRs reviewed twice on the identical head (#4284)

The rule's three signals decide implementation ownership. Nothing answered the same question
for review, and the cost was invisible because nothing collides and nothing errors: every
sweeping coordinator sees the same `status: review-ready` PR and dispatches at it.

Filed after four independent reviewer passes landed on #4281 — one pass cost **215,560 tokens
over 63 tool calls and about 15 minutes** on a PR already adjudicated. The filing agent then hit
it twice more itself, on #4290 (four prior verdicts) and #4291, and its own issue comments record
the correction that shaped the remedy: a `status: reviewed` label applied at completion "fixes
the wrong half", because three of its five instances were near-simultaneous *starts*.

### What the population says, against the five cited instances

The five instances were a coordinator's own dispatches, so they sample one loop's behaviour.
Scanning the 60 most recent pull requests on 2026-09-19 (59 carry at least one `Verdict:`
comment) and counting only repeats on the **identical head SHA** — where nothing about the PR
changed between the two passes, so the second is redundant by construction:

| measure | value |
|---|---|
| PRs with ≥2 verdicts on one head | **18 of 59 (31%)** — 19 such pairs |
| pairs under 15 min apart | **17 of 19**, median **5.5 min** |
| the two outliers | 28.5 and 29.1 min |
| measured review duration | ~15.6 min/PR (`orchestrating-a-session`) |

`>1 verdict` alone is **46 of 59** and mostly legitimate: a FIX-FIRST the author has addressed
needs re-reviewing at the new head. Keying on the same-head repeat is what separates the defect
from healthy re-review, and getting that wrong would have produced a signal firing on three
quarters of all PRs.

The 17-of-19 figure is what decided the design. A review takes ~15 minutes; the median gap
between redundant passes is 5.5. So the second reviewer usually started while the first was
still running, and any signal written when a review *finishes* is written too late. #4306 is the
clean instance: two verdicts on `4cfe233e`, **123 seconds** apart, each reviewer having checked
and found nothing, both correct at the moment they looked.

### Why it reports rather than blocks

The issue is explicit that the duplicate passes were not worthless. #4281's fourth pass
re-derived a 44-vs-41 discrepancy that three prior passes had described as one number being
stale, and showed both were right at their times; #4306's second reviewer picked mutations
overlapping neither the first's nor the author's ten. `tdd.md` says the same thing from the other
end — re-running the author's mutation is the weakest check a reviewer can make, so an
independent second pass picking its own is worth something.

The defect is therefore that the duplication is **unchosen and unbounded**, not that it happens.
`tools/review-claim.py` has no `--force` because nothing is in the way, and `--post` will write a
second claim beside an existing one without complaint.

### The guard arm that caught its own rationale

The first version of `test_review_claim.py` pinned "no `--force`" with `"--force" not in src`.
That arm failed on the honest tool: the docstring explains *why* there is no such flag, so the
substring test found its own rationale and reported it as the defect. Replaced with a call
passing `--force` and requiring argparse to reject it — the property, not the spelling. It is the
`a-substring-test-is-not-a-pin` shape, and it fired within a minute of being written.
