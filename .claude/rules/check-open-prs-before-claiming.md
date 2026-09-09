# Before claiming or dispatching an issue, look for an open PR that closes it

**An open PR carrying `Closes #N` means issue N is in progress. Do not claim it, and dispatch
an agent onto it only to repair that PR by name (`.claude/agents/impl-agent.md`, Step 1),
whatever the assignee and the labels say.**

The claiming protocol says the assignee locks and the `agent:` label discriminates. On this
repository that pair cannot decide ownership, because **every loop pushes under one GitHub
account**. Three signals, and only one of them answers the question:

| signal | what it tells you | what it cannot |
|---|---|---|
| assignee | that *somebody* claimed it | **who** — every loop is the same login |
| `agent: <tag>` label | which loop *last wrote a label* | whether that loop is live, finished, or dead |
| open **draft** PR with `Closes #N` | that a loop claimed it, minutes after it claimed | whether the fix is written yet |
| open **ready** PR with `Closes #N` | that work exists **and is real** | — |

The draft is what makes a claim visible that early: an implementation agent opens one as the
last act of claiming (`.claude/agents/impl-agent.md`, Step 2).

## The check

Per issue, at claim time:

```bash
gh pr list --repo StefanMaron/BusinessCentral.AL.Runner --state open --limit 100 \
  --json number,isDraft,closingIssuesReferences,labels \
  --jq '.[] | select(.closingIssuesReferences[]?.number == <N>) | {number, isDraft, labels: [.labels[].name]}'
```

Non-empty → in progress, draft or ready alike. Pick something else. `--state open` returns
both kinds; `isDraft` tells you which you found.

A draft is abandoned when its branch has had no push for 24 hours and no agent holds its
issue (`gh issue view <N> --json labels` shows no `agent:` label, or the label's loop has
reported it finished). The coordinator, never a claimant, comments on the draft with those
two facts, closes it, and returns the issue to `status: ready`; only then is the issue free.

**A coordinator dispatching several agents builds the map once per cycle**, not once per
issue — one call, then check every candidate against it:

```bash
gh pr list --repo StefanMaron/BusinessCentral.AL.Runner --state open --limit 100 \
  --json number,isDraft,closingIssuesReferences,labels
```

`closingIssuesReferences` is GitHub's own parse of the PR, so it reflects what will actually
close on merge — not a grep of the body.

**No `gh` in web/remote sessions** (`github-access.md`). There, list open PRs with
`mcp__github__list_pull_requests` and read each one's linked issues; the rule is the same, the
transport is not.

## Two corollaries, same root

- **Never remove an assignee to release a claim.** The account is shared, so the assignee you
  would remove may be another loop's lock, not yours. Release by removing **your own** `agent:`
  label. (An agent asked to "remove the assignee" as cleanup refused for exactly this reason,
  and was right to.)
- **Never remove or overwrite a foreign `agent:` label.** You cannot tell stale from live —
  that judgement is not available to you, and the open-PR lookup is what makes it unnecessary.
  If a foreign label is present and no open PR closes the issue, say so on the issue and take
  something else, or surface it. Leave the label alone. The same applies to a worktree carrying
  another identity's branch: the `agent:` label marks *the pool*, not a session.

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

## Sister rules

- `branch-and-pr.md` — branch naming, `Closes #N` in the body, the assignee boundary
- `github-access.md` — `gh` vs `mcp__github__*`; never assume `gh` exists
- `no-git-stash-with-worktrees.md` — the other place where "shared by default" bites, and
  why one agent's cleanup lands in another's work
- `public-posting-approval.md` — commenting on the issue to surface a collision is ungated
  on this repository, and carries its reasoning
