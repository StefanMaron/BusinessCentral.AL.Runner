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
