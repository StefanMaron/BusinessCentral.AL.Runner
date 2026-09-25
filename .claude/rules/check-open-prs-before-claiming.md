# Before claiming or dispatching an issue, look for an open PR that closes it

**An open PR carrying `Closes #N` means issue N is in progress. Do not claim it, and dispatch
an agent onto it only to repair that PR by name (`.claude/agents/impl-agent.md`, Step 1),
whatever the assignee and the labels say.**

## Read the assignee's ACCOUNT first — it decides which half of this rule applies

More than one GitHub account runs agent loops here, so **establish whether the other claimant
is on your own account before you rate any signal**. `gh api user --jq .login` is your side;
the assignee's login is theirs. The answer changes what the assignee is worth:

| the other claimant is | what the assignee tells you | what to do |
|---|---|---|
| another loop on the **same** account | **nothing** — same login, so it cannot say *which* loop | read on — the open-PR check below is what resolves it |
| a loop on a **different** account | **everything** — a real boundary, and it is authoritative | **stop**, per `branch-and-pr.md`'s assignee boundary; nothing below waives it |

Measured 2026-09-22: remote `agent/fbk-*/…` branches and `agent: fbk-*` labels from a second
agent-running account — so the cross-account case is real, not hypothetical.

**The branch prefix is the same discriminator, and it is the stronger one**: `agent/fbk-2/…`
versus `agent/stma-auto-1/…` cannot be rewritten by another loop, where a label can.
`orchestrating-a-session` already arms on it ("Check the **branch prefix**, never the author
field") for this exact reason — one mechanism, two rules.

**Trap: it is the ACCOUNT that discriminates, never whether the claimant looks like a bot.** A
`fbk-*` label is an agent pool, a human maintainer is a person, and both are "not you"; the
boundary is the login, and an agent may not waive it in either case.

## The rest of this rule is the SAME-account case

Here the assignee locks and the `agent:` label discriminates — except that neither can decide
ownership, because every loop on one account pushes as that one login. Three signals, and only
one of them answers the question:

| signal | what it tells you | what it cannot |
|---|---|---|
| assignee | that *somebody on your account* claimed it | **which loop** — they share the login |
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
and resolved to `[4111]` only **seconds** later, with a correct closing declaration for
that issue in the
body throughout. Nothing reports the pending state — an empty array is what a PR closing no
issue also returns.

That matters here specifically, because this rule's whole purpose is deciding whether an issue
is taken: an empty read on a PR opened seconds ago is **"not parsed yet"**, not "free", and
treating it as free is how two agents claim one issue. On a zero result for an issue you are
about to claim, confirm it a second way before believing it — the body carries the declaration
immediately even when the parse has not caught up:

<!-- Recipe-pinned-by: tools/test_closing_ref_confirm_recipe.py -->
```bash
gh pr list --repo <owner>/<repo> --state open --limit 100 --json number,body \
  --jq '.[] | select(.body | test("(?i)closes +#<N>\\b")) | .number'
```

**Substitute the real issue number**; a pattern left generic matches any closing keyword
anywhere in the text, including a body that merely *documents* one. Even substituted it can
over-report, because a PR quoting this rule carries the keyword too — which is why the check is
a **confirmation of a zero**, never a claim on its own. It fails safe in that role: over-reporting
"taken" costs a second look, while the false *negative* it exists to catch costs two agents one
issue. Measured while adding this
section: the generic form answered `true` on a pull request whose only declaration was
`Part of #4059`, because the body quoted this very recipe.

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

Both get **stronger** across accounts, never weaker: a label from another account belongs to a
pool whose liveness you cannot observe at all, and the assignee there is a boundary rather than
a lock.

## The same question about REVIEW, where the three signals say nothing

The signals above decide who is **implementing** an issue. None of them answers who is
**reviewing** a pull request: `status: review-ready` means *ready for review* and is never
rewritten while a review is in flight, so a PR reads identically whether nobody has looked at it
or three agents already have.

**Read the signal before dispatching a reviewer**, from the comment stream the verdict already
lives in:

```bash
tools/review-claim.py --pr <N>     # 0 free, 1 claimed or already reviewed, 3 unreadable
```

**And post a claim before you start reviewing**, because the duplication is concentrated in the
window a completion-time signal cannot cover:

```bash
tools/review-claim.py --pr <N> --post --agent-id <YOUR-ID>
```

Measured over recent pull requests on 2026-09-19, counting only repeats on the **identical
head**, where nothing about the PR changed between the passes: **two or more verdicts on one head
were common**, and **most such pairs landed closer together than one review takes**. So the second
reviewer usually started while the first was still running — #4306 has two verdicts on
`4cfe233e` minutes apart — and a `status: reviewed` label written when a review
*finishes* would have been too late for nearly all of them (#4284).

**Trap: this reports, it never blocks, and that is deliberate.** A second pass is sometimes
right — a later pass on #4281 produced findings the earlier ones did not. What was missing is
not a lock but a signal, so that spending a second review is a decision someone made rather than
an accident. The tool has no flag to route around, because nothing is in the way.

**Second trap: a claim is about a HEAD, and it expires.** A claim naming a superseded head, or
one older than `tools/review-claim.py`'s `--max-age`, is reported and does not hold — a reviewer that died mid-pass must not
lock a PR forever, which is the same judgement the abandoned-draft clause above makes. And exit 3
is not exit 0: "nobody is reviewing this" and "I could not find out" send a coordinator to
opposite actions.

## The claim signals, and what each is worth

This rule owns the three signals, so any other document that needs them points here rather than
restating them. **It widens the read; it does not replace the lock** — the compare-and-swap on
claiming (assign, re-read, release if someone else's claim appeared) stands unchanged, because
every collision behind this rule was a read that was too narrow, not a write that raced (#2780,
#2755).

## Sister rules

- `branch-and-pr.md` — branch naming, `Closes #N` in the body, and the assignee boundary this
  rule now defers to rather than argues against
- `github-access.md` — `gh` vs `mcp__github__*`; never assume `gh` exists
- `no-git-stash-with-worktrees.md` — the other place where "shared by default" bites, and
  why one agent's cleanup lands in another's work
- `tdd.md` — why a second independent pass is worth something: a reviewer re-running the
  author's own mutation is the weakest check available, so duplication is capped, not banned
- `public-posting-approval.md` — commenting on the issue to surface a collision is ungated
  on this repository, and carries its reasoning

History: docs/incidents/check-open-prs-before-claiming.md
