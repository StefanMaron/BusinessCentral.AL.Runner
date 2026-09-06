# Scan the open-issue queue before implementing, and fold in what lands in the same file

`.claude/agents/impl-agent.md`'s "Fix the shape, not just the reported line" already tells you to look for
siblings **in the code** in front of you, and it works. This rule is the other half: look at
the **open-issue queue** for the area you are about to touch, before you start. Nothing told
anyone to, and measurably nobody did.

## The rule

1. **Before implementing, scan the open issues for the area you are about to touch.** Search
   for the symbol, the file and the subsystem your fix will land in. It is one query.
2. **Fold in an issue if its fix lands in the same file, or the same tight cluster of files,
   as the change you are already making.** Same *code*, not same subsystem.
3. **Every issue you close gets its own RED → GREEN in the same PR.** No proving test, no
   `Closes`.
4. **Stop when the diff stops being one coherent change a reviewer can hold in their head** —
   not at a fixed count. If you are folding in issues that need materially different reasoning
   to review, that is the signal to split, however few there are.
5. **Link what you did not fold, and why.** "These four share the area; I fixed one; here is
   how the other three differ" belongs in the PR body. Do this **even when nothing folds** — it
   is often the more valuable output, because it turns a long queue into a map.

**One honest caveat.** For a thin issue, "same file" is only knowable *after* diagnosis. So the
scan is cheap and early; the fold decision comes once you know where the fix goes — which is
still before you write the test.

## Why same-file and not same-subsystem

Splitting fixes that touch one file does not merely cost extra CI cycles and extra reviews. It
**manufactures a rebase treadmill**, and that is measured, not predicted:

- PRs #3197 and #3180 both edit the same loop in
  `AlRunner/Patches/BcAppSymbolCache.TableExtensions.cs`, so they need a forced merge order.
- Three separate PRs collided on `tests/expectations/count-baseline/history.md`.
- A standing finding: every virtual-table PR conflicts at the same if-chain in
  `RecordPatches.cs`.

**"The same subsystem" was considered and rejected** — it is the failure mode this rule exists
to prevent. "All page issues" spans dozens of files across `MockTestPage.cs`, the
`RecordPatches.*` partials, the metadata registries and the corpus. An agent that pulls all of
them in produces an unreviewable PR that conflicts with everything. Same-file is the test
precisely because that set cannot pass it.

**The narrower draft was also rejected**: "fold only if the same unmodified change fixes both"
catches only the repeated-call-site case, and would split three fixes landing in one file
merely because each needs a slightly different edit — the case where splitting is most wasteful
and most conflict-prone.

## Why there is no cap, and why you should not add one

An earlier draft of this rule stopped at three closed issues per PR. That was arbitrary and is
deliberately gone. **The right number is a property of how finely the issues were filed, not a
constant.** If ten open issues each say "this virtual table column answers BC's default instead
of the real value" and all ten land in one file, fixing all ten in one PR is obviously correct
and splitting them would be absurd.

The two limits that are not arbitrary are already above, and both are self-enforcing:

- **A proving test per closed issue** (point 3). Nobody writes ten proving tests to pad a PR,
  and an agent that *can* write ten has demonstrated the fold was genuine.
- **One coherent change** (point 4). This is a property of the diff, which a reviewer can judge
  directly; a count is not.

**What to watch, because this is an experiment with a stated failure signal.** If PRs start
arriving that reviewers cannot hold, or the conflict and rebase rate on large PRs climbs, that
is the evidence for adding a limit — and it should then be a number derived from that data, not
guessed again.

## This is still one PR — reconciling with `branch-and-pr.md`

`branch-and-pr.md` says **"One open PR per impl agent"** and **"Do not claim a second issue
while a PR is open."** Both stand, unchanged. They bound **concurrency** — how many PRs and
branches one agent has in flight — and this rule bounds **content**: what a single PR is allowed
to close.

So: one agent, one branch, one open PR, which may carry `Closes #A`, `Closes #B` and `Closes #C`
when each has its own proving test. What is still forbidden is claiming an issue and starting
*separate* work on it while your PR is open. Claim the batch together, before the PR exists.

## The behaviour already exists; it just had no name

Agents batch correctly when they trip over a sibling in the code:

- On #3069 an agent found **seven** `SetReferenceTarget` call sites needing the same edit and
  fixed them together — "one shape repeated seven times, not seven bugs".
- On #3015 an agent found the same defect at `InsertAllObjRow` and a sibling at
  `InsertCompanyRow`, resolved all of it through one `SeededRowColumns` ledger, and still filed
  **#3187 separately** because the latch-before-work pattern was "a different shape". Point 5,
  done right, before it was written down.

What none of them did was read the queue first. Open while this was written: #3080 and #3063
are both Page Metadata; #2381, #2983 and #2363 are all the User system table.

## Sister rules

- `check-open-prs-before-claiming.md` — the other pre-claim queue read: an open PR carrying
  `Closes #N` means N is taken, whatever its labels say. Run both scans at the same moment.
- `branch-and-pr.md` — "one open PR per impl agent", reconciled above
- `tdd.md` — point 3 is `tdd.md` applied per closed issue; folding never buys an exemption
- `file-issues-for-gaps.md` — what you do not fold, you link or file; never silently drop
- `no-assumption-fixes.md` — a thin issue is not foldable until it is diagnosed
