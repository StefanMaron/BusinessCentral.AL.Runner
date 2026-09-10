---
name: driving-an-issue-to-merge
description: The single-operator loop for taking one issue from the queue to a merged PR on this repo — claim, diagnose, RED, fix, mutation-check, attribute the failures that are not yours, get an external adversarial review through pi, open the PR, act on GitHub's automatic review, and hand a green PR to the merge. Use when you are working an issue yourself in the main session rather than dispatching an impl agent, and you intend to see it merged rather than hand it back.
---

# Driving one issue to merge

You are the operator: you read the issue, write the fix, prove it, get it reviewed, and drive
the PR to green. This is not the `impl-agent` loop, which opens a PR and hands back without
reading CI, and it is not `orchestrating-a-session`, which delegates and merges other agents'
work. It is the third shape — one person, one issue, end to end.

Every step below is here because skipping it produced a wrong answer in a real session. The
steps that cost the most when skipped are 5 (mutation-check), 6 (attribute), and 8 (act on
the review honestly).

## 0. Before anything: two queue reads, in the same moment

```bash
R=StefanMaron/BusinessCentral.AL.Runner
gh pr list --repo $R --state open --limit 100 --json number,isDraft,closingIssuesReferences,labels
gh issue list --repo $R --state open --limit 200 --json number,title,labels,assignees
```

The first is `check-open-prs-before-claiming.md`: an open PR carrying `Closes #N` means N is
taken, whatever the labels say. The second is `batch-sibling-issues-by-file.md`: read the
queue for the area you are about to touch **before** you start.

**`gh issue list --search` can leak results from other repositories.** Measured: a search with
`in:body,title` returned issues from a dozen unrelated repos alongside this one's. Filter by
number or use `--json` and check, rather than trusting the row list.

Claim: `gh issue edit <N> --add-assignee @me --add-label "status: in-progress" --add-label "agent: <id>"`.

## 1. Diagnose before you decide what the PR is

`no-assumption-fixes.md` in full. Read the issue, then read the code it names, then decide.

**The fold decision comes after diagnosis, not before.** "Same file" is only knowable once you
know where the fix goes. In one session #2193 turned out to be literally the same edit as one
third of #3794 — the same function, the same line — and folding it cost nothing; #2232 and
#2661 shared the area and needed different reasoning, so they were linked in the PR body
instead. Both outcomes are correct; what makes them correct is that the call came after the
diagnosis.

## 2. RED first, and let it be ugly

`tdd.md`. Write every fact before the production change and watch each fail.

**A compile error is a legitimate RED for a new API.** A fact asserting
`scan.EdgeRequirements` cannot run before that member exists; `error CS1061` is the failure,
and it is as good as an assertion failure. Do not invent a weaker test that compiles.

## 3. Fix, and keep the diff one idea

Stop when the diff stops being one change a reviewer can hold (`batch-sibling-issues-by-file.md`
point 4). A defect the review will find later is cheaper than a redesign nobody asked for: file
the redesign instead (step 8).

## 4. Run the targeted classes, not the suite

`local-test-scope.md`. On this box, always pass the BC version — a bare build fails:

```bash
dotnet build AlRunner.slnx -c Release -p:_BCVersion=<a provisioned build>
dotnet test AlRunner.Tests/AlRunner.Tests.csproj -c Release --no-build \
  -p:_BCVersion=<same> --filter "FullyQualifiedName~<Class>|FullyQualifiedName~<Class>"
```

## 5. Mutation-check every new fact — this is the step that makes the rest worth anything

**Remove the production change and re-run. A fact that still passes proves nothing.**
`tdd.md` asks whether a test would pass against a default-returning implementation; this is
how you find out instead of judging by eye.

Batch it: revert one coherent slice of the fix, note which facts fail, restore, repeat. Three
batches over one PR took minutes and confirmed 10 of 18 facts caught their own defect directly.
It also finds facts that are *not* load-bearing, which is information you want before a
reviewer tells you.

```bash
git commit ...                                   # commit the green state FIRST
python3 - <<'PY'                                 # mutate one slice
...
PY
dotnet build ... && dotnet test ... --filter ...
git show HEAD:<path> > <path>                    # restore — see the traps below
```

**Commit before mutating.** The restore is `git show HEAD:<path> > <path>`, and it only
restores what `HEAD` holds.

## 6. Attribute the failures that are not yours — with a baseline, not a claim

A wider sweep will surface failures that have nothing to do with you. `ci-verdicts.md` §5 sets
the evidence bar, and the cheapest way to clear it locally is a pristine worktree:

```bash
git worktree add --detach .claude/worktrees/baseline-<N> origin/main
cd .claude/worktrees/baseline-<N>
dotnet build AlRunner.slnx -c Release -p:_BCVersion=<same>
dotnet test ... --filter "<the same filter>"     # then compare the failing SETS
```

Measured once: the branch failed 13, the pristine baseline failed 15 — a strict superset — so
none of the 13 was caused by the change, and the PR body could say so with a number behind it.
Reasoning about which look network-dependent would have produced the same conclusion with no
evidence, and the same reasoning has been wrong before.

Clean up afterwards; see the traps below, because both obvious removal commands fail here.

## 7. External adversarial review, before the PR is anyone else's problem

```
mcp__pi__pi_ask  model: gpt-5.6-sol  thinking: high
                 output_file: <scratchpad>/review-<PR>-gpt56sol.md
```

It takes longer than 120 s and gets backgrounded; you are notified. `output_file` keeps a
2,000-word review out of your context until you read it.

**The prompt is what decides whether the review is worth anything.** Five things earned their
place:

- **Absolute paths to the worktree**, and the branch plus base SHA. The sub-agent runs from its
  own directory and can read files but not run a shell.
- **What the project is**, in four sentences. It has no repo context.
- **What you already checked**, explicitly, so it does not spend its effort re-deriving your
  mutation results.
- **A numbered list of the things you are least sure of.** Name the failure mode you fear most
  and ask for a concrete counterexample. In one session that question ("can the BFS produce a
  floor that is too low?") came back as *no, not for that reason — here is the different reason
  it can*, which is the answer that was worth having.
- **CONFIRMED vs SPECULATIVE per finding, and BLOCKING / SHOULD FIX / NITS**, plus an explicit
  "if there is nothing blocking, say so rather than inventing something."

## 8. Act on the review honestly — three outcomes, not one

For each finding, exactly one of:

- **Fix it.** Anything that is a real defect in what you changed.
- **Split it and file it.** A finding that is right about the code but asks for a redesign of a
  public surface belongs in an issue with the specific gaps enumerated, not grown into this
  diff. **File the issue before you cite its number in a code comment.** Writing `#3811` into a
  comment and filing afterwards worked once by luck; the numbers are not yours to predict.
- **Measure it.** A finding marked SPECULATIVE is an invitation, not a verdict. One review
  guessed a fixture might newly trip a 116 MB download; scanning all 123 fixture manifests and
  every checked-in fixture `.app` answered it in one command, and "zero, measured" reads
  differently from "I do not think so."

Never the fourth thing: arguing a finding away without evidence.

Reply on the PR with the item-by-item response. What you rejected and why is the part a human
reviewer actually needs.

## 9. Open the PR — and check what it claims to close

```bash
PR_BODY="$(cat body.md)" CHANGED_FILES="$(git diff --name-only origin/main...HEAD)" \
  bash .github/scripts/check_corpus_linkage.sh
command grep -inE "(close[sd]?|fix(e[sd])?|resolve[sd]?)[[:space:]]+#[0-9]+" body.md
gh pr create --repo $R --base main --head <branch> --title "..." --body-file body.md
gh pr view <PR> --repo $R --json closingIssuesReferences
```

**That grep is not optional.** GitHub's parser fires on past tense and mid-sentence. A summary
paragraph reading `"...floors, which fixed #<N>"` — an ordinary sentence about an
already-closed issue belonging to another PR — silently added that issue to
`closingIssuesReferences`, which `pr-gate.yml`'s `reject-bad-closing-references` job blocks the
merge over. Read the field back after creating, every time; `branch-and-pr.md` has the rest of
the family.

**And the same sentence is a trap in a commit message and in the PR body that describes it.**
Both are scanned, the squash concatenates the commit messages, and quoting the example with a
real number reproduces the defect while documenting it. Spell it `#<N>`.

Then `gh pr edit <PR> --add-label "status: review-ready"`, and **edit the body only through
`tools/pr-body.py`** (`branch-and-pr.md`).

## 10. GitHub's automatic review arrives on its own — read both halves

Copilot posts a review a few minutes after the PR opens, without being asked. It is labelled
"Review effort level: Lite" and it is worth reading: in one session its single finding was
correct and I had missed it — two tests renamed for a channel they no longer asserted.

**The inline comments and the review body are two different fetches**, and the body is where
*suppressed* comments are listed:

```bash
gh api "repos/$R/pulls/<PR>/comments" --jq '.[]|"\(.path):\(.line // .original_line): \(.body)"'
gh pr view <PR> --repo $R --json reviews --jq '.reviews[-1].body'
```

Reading only the first misses findings the review itself says it suppressed.

## 11. Green, then hand it over — you do not merge your own PR

Read the verdict, never wait for it (`ci-verdicts.md` §0):

```bash
tools/ci-wait.py <PR> --timeout 0
```

Run `origin/main`'s copy from a temp directory when your worktree may be behind — all three
files, `ci-wait.py`, `agent_self_freshness.py`, `agent_stdio.py` (`ci-verdicts.md`).

Green is not merged. This repository squash-merges with `allow_auto_merge=true`, so an approval
plus green lands it; the approval is the coordinator's or the owner's. Measured on two PRs in
one session: both went green, both were merged by the account owner, neither carried an agent
approval. **Your deliverable is a green PR labelled `status: review-ready` with the review
history in the thread.** If a required check goes red after you hand over, it is still yours —
"PR opened is not the deliverable; PR merged is."

## Environment traps this loop walks into

| trap | what happens | do this instead |
|---|---|---|
| `tools/context-pack.py`, `tools/lsp-query.py` | `csharp-ls is not installed`, exit 2 — which means **nothing**, not "no callers" | targeted `command grep` on the file you already know, or the `LSP` tool in a main session |
| A heredoc carrying C# raw strings or regexes | the Bash tool's parser reports `unexpected EOF while looking for matching '` and never runs it | write the patch script to the scratchpad with the `Write` tool, then `python3 <path>` — repeatable, re-runnable, and it survives a failed assertion |
| `git checkout <ref> -- <path>` | refused by the safety net, which suggests `git stash` — forbidden here (`no-git-stash-with-worktrees.md`) | `git show <ref>:<path> > <path>` |
| `git worktree remove --force` | refused by the safety net | `git worktree remove` plain; if the directory is locked by a `dotnet` build server it deregisters anyway, then `rm -rf` the leftover and `git worktree prune` |
| A bare `dotnet build` | fails on a BC version that is not provisioned on this box | always `-p:_BCVersion=<a provisioned build>`; list `~/.local/share/al-runner/artifacts/` |
| `Assert.DoesNotContain` on a captured run | prints ~40 characters around the hit, hiding the errors underneath — a red CI leg that names the symptom and not the cause | `Assert.False(output.Contains(...), $"...\n{output}")` |

## Sister documents

- `.claude/rules/ci-verdicts.md` — reading a verdict, and never waiting for one
- `.claude/rules/tdd.md` — RED → GREEN, and what makes a test prove something
- `.claude/rules/batch-sibling-issues-by-file.md` — what folds into one PR and what does not
- `.claude/rules/check-open-prs-before-claiming.md` — the claim reads in step 0
- `.claude/rules/branch-and-pr.md` — branch naming, closing references, `tools/pr-body.py`
- `.claude/rules/no-git-stash-with-worktrees.md` — why the RED-baseline recipe is shaped the way it is
- skill `orchestrating-a-session` — the delegating shape, and the merge bar you are handing to
- `.claude/agents/impl-agent.md` — the hand-back shape this loop replaces
