# Incidents behind .claude/rules/check-open-prs-before-claiming.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## The cross-account near-miss that split the rule in two (#3275)

The three collisions below were all same-account, and the rule generalised from them to a
premise it stated in its own opening: *"every loop pushes under one GitHub account."* By
2026-09 that was false. A second account, `FBakkensen`, ran an `fbk-*` pool against this
repository.

The near-miss: a coordinator built its candidate list from a queue snapshot, then dispatched an
agent onto #3263. Between the snapshot and the dispatch, `FBakkensen`'s agent filed #3263,
assigned it, and labelled it `agent: fbk-2`. The dispatched agent stopped rather than claiming
— and was right to, but it stopped by reading `branch-and-pr.md`'s "skip any issue whose
assignee is a user other than `@me`", **not** by following this rule, which had actively argued
the assignee could not answer the question. The open-PR check the rule offers as the reliable
substitute did not cover it either: `fbk-2` had claimed the issue but had no PR yet. Nothing
but the assignee distinguished it.

So the two rules pointed in opposite directions depending on who the other claimant was, and
the rule that is *about* claiming collisions gave the wrong steer. The fix was not a rewrite —
the same-account reasoning is sound and all three collisions below really were same-account —
but a discriminator at the top, so a reader knows which case they are in before they rate a
signal.

Re-measured 2026-09-22 while fixing it: five remote `agent/fbk-*/…` branches
(`issue-2444`, two on `issue-2345`, `issue-2201`, `issue-3178`), three `agent: fbk-*` labels,
and 99 of the last 100 pull requests authored by `StefanMaron` with the newest `FBakkensen` PR
dated 2026-09-12. Real, and quiet — which is the awkward state to write a rule for, because
the case is invisible on any given day and costs an issue when it is not.

A second cost the issue recorded: #3178 and #3263 were the same three lines of
`BuildMetaCalcFormula` with different absent-field sets. Under `batch-sibling-issues-by-file.md`
they would fold into one PR; across accounts that fold needs coordination nobody has, and
landing them separately conflicts by construction.

### What pins it

`tools/test_claim_signals_account_scope.py` parses the rule's claim table and asserts over the
ROWS, not over any sentence: both account cases must be rated, on *different* rows, with
*opposite* verdicts about the assignee, and the discriminator must appear before the first
per-signal rating. A substring test would pass for a rename and for the name in a comment.

Two defects that guard caught in its own first revision, both worth recording because each
looked like the rule being wrong rather than the instrument:

- the `DIFF` regex matched *"another loop on the **same** account"*, because the adjective
  `another` modified `loop` while `account` sat later in the cell — so the same-account row read
  as cross-account and the two-distinct-rows check failed;
- the markdown table parser dropped the **last data row of every table**, holding each row back
  to see whether a separator followed and discarding it on a non-table line instead of flushing
  it. The cross-account row is the last row of its table, so the property under test was the one
  systematically invisible.

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

### Valid JSON of the wrong shape, and the two answers that pointed opposite ways

Found in review of PR #4390. The first version checked that the comments payload *parsed* and
never that it was a comment **list**, so `scan()` met whatever the JSON happened to contain.
Reachable only through `--stdin` — `fetch()` tests the return code first, and `gh api` exits 1 on
a 404 — which is exactly the path every session without `gh` uses (`github-access.md`).

The reviewer quoted the 404 body. Sweeping the shapes found a second, worse one:

| input | before | why |
|---|---|---|
| `{"message":"Not Found"}` — the real 404 body | **exit 1 = CLAIMED** | iterating a dict yields its KEYS, so the scan met a `str`; the uncaught `AttributeError` exits 1, colliding with this tool's own CLAIMED |
| `{}` | **exit 0 = FREE** | no keys, so the loop body never ran; `scan()` returned empty and the tool printed a well-formed FREE |

`{}` is the dangerous one and it is the one a single-literal arm would have missed. CLAIMED merely
sends a reviewer away; FREE **invites** a second reviewer onto a PR nobody measured, which is the
defect this tool exists to prevent, produced by the tool itself.

The fix is a `shape_error()` predicate applied on **both** payload routes rather than only the one
that was reported: a 200 whose body is not a list reaches `fetch()` just as easily. Pinned by eight
table-driven arms over shapes that occur, plus two controls — an empty list is a real measurement,
and a well-shaped list still answers CLAIMED — because a reporter answering 3 for everything would
pass all eight.

Same shape as the `--force` arm earlier on the same PR: keying a check on one spelling rather than
on the property. Twice on one pull request is the reason the arms here are table-driven.
