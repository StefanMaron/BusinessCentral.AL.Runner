# Before claiming or dispatching an issue, look for an open PR that closes it

**An open PR carrying `Closes #N` means issue N is in progress. Do not claim it, do not
dispatch an agent onto it, whatever the assignee and the labels say.**

The claiming protocol says the assignee locks and the `agent:` label discriminates. On this
repository that pair cannot decide ownership, because **every loop pushes under one GitHub
account**. Three signals, and only one of them answers the question:

| signal | what it tells you | what it cannot |
|---|---|---|
| assignee | that *somebody* claimed it | **who** — every loop is the same login |
| `agent: <tag>` label | which loop *last wrote a label* | whether that loop is live, finished, or dead |
| open PR with `Closes #N` | that work exists **and is real** | — |

## The check

Per issue, at claim time:

```bash
gh pr list --repo StefanMaron/BusinessCentral.AL.Runner --state open --limit 100 \
  --json number,closingIssuesReferences,labels \
  --jq '.[] | select(.closingIssuesReferences[]?.number == <N>) | {number, labels: [.labels[].name]}'
```

Non-empty → in progress. Pick something else.

**A coordinator dispatching several agents builds the map once per cycle**, not once per
issue — one call, then check every candidate against it:

```bash
gh pr list --repo StefanMaron/BusinessCentral.AL.Runner --state open --limit 100 \
  --json number,closingIssuesReferences,labels
```

`closingIssuesReferences` is GitHub's own parse of the PR, so it reflects what will actually
close on merge — not a grep of the body.

**No `gh` in web/remote sessions** (`github-access.md`). There, list open PRs with
`mcp__github__list_pull_requests` and read each one's linked issues; the rule is the same, the
transport is not.

## Two corollaries, same root

- **Never remove an assignee to release a claim.** The account is shared, so the assignee you
  would remove may be another loop's lock, not yours. Release by removing **your own** `agent:`
  label.
- **Never remove or overwrite a foreign `agent:` label.** You cannot tell stale from live —
  that judgement is not available to you, and the open-PR lookup is what makes it unnecessary.
  If a foreign label is present and no open PR closes the issue, say so on the issue and take
  something else, or surface it. Leave the label alone. The same applies to a worktree carrying
  another identity's branch: the `agent:` label marks *the pool*, not a session.

## The claim signals, and what each is worth

This rule owns the three signals, so any other document that needs them points here rather than
restating them. **It widens the read; it does not replace the lock** — the compare-and-swap on
claiming (assign, re-read, release if someone else's claim appeared) stands unchanged, because
every collision behind this rule was a read that was too narrow, not a write that raced (#2780,
#2755).

## Sister rules

- `branch-and-pr.md` — branch naming, `Closes #N` in the body, the assignee boundary
- `github-access.md` — `gh` vs `mcp__github__*`; never assume `gh` exists
- `no-git-stash-with-worktrees.md` — the other place where "shared by default" bites, and
  why one agent's cleanup lands in another's work
- `public-posting-approval.md` — commenting on the issue to surface a collision is ungated
  on this repository, and carries its reasoning

History: docs/incidents/check-open-prs-before-claiming.md
