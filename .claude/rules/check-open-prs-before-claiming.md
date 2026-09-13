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

A draft is abandoned when its newest commit (`gh pr view <N> --json commits --jq
'.commits[-1].committedDate'`) is older than 24 hours and the loop that opened it has
returned. Only the coordinator that dispatched that loop releases it: comment on the draft
with those two facts, close it, remove that loop's `agent:` label from the issue, and return
the issue to `status: ready`; only then is the issue free. A draft opened by a loop you did
not dispatch stays where it is (`autonomous-cycle`: a foreign `agent:` label is never yours
to clear).

**A coordinator dispatching several agents builds the map once per cycle**, not once per
issue — one call, then check every candidate against it:

```bash
gh pr list --repo StefanMaron/BusinessCentral.AL.Runner --state open --limit 100 \
  --json number,isDraft,closingIssuesReferences,labels
```

`closingIssuesReferences` is GitHub's own parse of the PR, so it reflects what will actually
close on merge — not a grep of the body.

**But the parse LAGS the PR's creation, so a fresh PR can read as claiming nothing.** Measured
on PR #4119: created through the REST endpoint, `closingIssuesReferences` came back **empty**
and resolved to `[4111]` about **twelve seconds** later, with a correct `Closes #4111` in the
body throughout. Nothing reports the pending state — an empty array is what a PR closing no
issue also returns.

That matters here specifically, because this rule's whole purpose is deciding whether an issue
is taken: an empty read on a PR opened seconds ago is **"not parsed yet"**, not "free", and
treating it as free is how two agents claim one issue. On a zero result for an issue you are
about to claim, confirm it a second way before believing it — the body carries the declaration
immediately even when the parse has not caught up:

```bash
gh pr list --repo <owner>/<repo> --state open --limit 100 --json number,body \
  --jq '.[] | select(.body | test("(?i)\\b(closes|fixes|resolves) +#<N>\\b")) | .number'
```

Same shape as every other trap in this repository: the call succeeds, the answer is
well-formed, and it is about a moment rather than about the question you asked.

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
