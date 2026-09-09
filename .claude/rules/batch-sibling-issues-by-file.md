# Scan the open-issue queue before implementing, and fold in what lands in the same file

`.claude/agents/impl-agent.md`'s "Fix the shape, not just the reported line" covers siblings
**in the code** in front of you. This rule is the other half: read the **open-issue queue** for
the area you are about to touch, before you start.

## The rule

1. **Before implementing, scan the open issues for the area you are about to touch.** Search
   for the symbol, the file and the subsystem your fix will land in. It is one query.
2. **Fold in an issue if its fix lands in the same file, or the same tight cluster of files,
   as the change you are already making.** Same *code*, not same subsystem.
3. **Every issue you close gets its own RED → GREEN in the same PR.** No proving test, no
   `Closes`.
4. **Stop when the diff stops being one coherent change a reviewer can hold in their head** —
   not at a fixed count. Issues needing materially different reasoning to review are the signal
   to split, however few there are.
5. **Link what you did not fold, and why.** "These four share the area; I fixed one; here is
   how the other three differ" belongs in the PR body. Do this **even when nothing folds** — it
   turns a long queue into a map.

**One honest caveat.** For a thin issue, "same file" is only knowable *after* diagnosis. So the
scan is cheap and early; the fold decision comes once you know where the fix goes, which is
still before you write the test.

## Why same-file and not same-subsystem

Splitting fixes that touch one file manufactures a rebase treadmill: two PRs editing one loop
need a forced merge order (#3197, #3180), and every virtual-table PR conflicts at the same
if-chain in `RecordPatches.cs`.

**Same file, not same subsystem, and not "the identical edit fixes both".** A subsystem spans
dozens of files and folds into an unreviewable PR that conflicts with everything; the narrower
test splits fixes that land in one file merely because each needs a slightly different edit,
which is where splitting is most wasteful.

## There is no cap, and adding one needs data

**The right number is a property of how finely the issues were filed, not a constant** — ten
issues that all land in one file are one PR, not ten. The two limits that are not arbitrary are
already in the rule and both are self-enforcing: a proving test per closed issue (point 3) and
one coherent change (point 4), which a reviewer judges from the diff. The evidence that would
justify a cap is reviewers unable to hold arriving PRs, or a climbing conflict rate on large
ones — and it should then be a number derived from that data.

## This is still one PR — reconciling with `branch-and-pr.md`

`branch-and-pr.md` owns how many PRs and branches one agent may have in flight; this rule bounds
only **content** — one PR may carry `Closes #A`, `Closes #B` and `Closes #C` when each has its
own proving test. So claim the batch together, before the PR exists, rather than starting
*separate* work on a second issue afterwards.

## Sister rules

- `check-open-prs-before-claiming.md` — the other pre-claim queue read: an open PR carrying
  `Closes #N` means N is taken, whatever its labels say. Run both scans at the same moment.
- `branch-and-pr.md` — "one open PR per impl agent", reconciled above
- `tdd.md` — point 3 is `tdd.md` applied per closed issue; folding never buys an exemption
- `file-issues-for-gaps.md` — what you do not fold, you link or file; never silently drop
- `no-assumption-fixes.md` — a thin issue is not foldable until it is diagnosed

History: docs/incidents/batch-sibling-issues-by-file.md
