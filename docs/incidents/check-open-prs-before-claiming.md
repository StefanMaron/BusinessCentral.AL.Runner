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
