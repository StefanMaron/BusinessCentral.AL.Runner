# Incidents behind .claude/rules/batch-sibling-issues-by-file.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

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

## Why same-file and not same-subsystem (chronology moved from the rule, #3728 review round 1)

- PRs #3197 and #3180 both edit the same loop in
  `AlRunner/Patches/BcAppSymbolCache.TableExtensions.cs`, so they need a forced merge order.
- Three separate PRs collided on `tests/expectations/count-baseline/history.md`.
- A standing finding: every virtual-table PR conflicts at the same if-chain in
  `RecordPatches.cs`.

"Same subsystem" was considered and rejected: "all page issues" spans dozens of files across
`MockTestPage.cs`, the `RecordPatches.*` partials, the metadata registries and the corpus. The
narrower draft — "fold only if the same unmodified change fixes both" — was rejected too: it
catches only the repeated-call-site case, and would split three fixes landing in one file merely
because each needs a slightly different edit.

Nobody writes ten proving tests to pad a PR, and an agent that *can* write ten has demonstrated
the fold was genuine.
