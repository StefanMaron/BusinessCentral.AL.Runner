# Before implementing, search for other issues reporting the SAME defect

`batch-sibling-issues-by-file.md` tells you to scan the queue for issues whose fix lands in the
**same file**. This rule is the other axis: **the same defect, filed more than once, in different
words.** Two issues can describe one root cause and share no file, no symbol and no vocabulary — so
a file-keyed scan cannot find them, and neither can a title search using the words *you* would have
chosen.

## The rule

**Before you implement, search the open queue for the defect you are about to fix — by its
mechanism, not by its title.** Then say what you found in the PR body, including when you found
nothing.

Search on at least three of these, because any one of them alone misses:

- **The symbol or member** the defect lives on (`NCLMetaTable`, `SymbolReference`, `MetaField.Editable`).
- **The observable** — what AL or a caller actually sees wrongly (`Editable` answers true,
  `TableNo = 0`, a key count of 2 instead of 3).
- **The mechanism** — the shape, not the surface: "swallowed into a cached null", "latched before the
  work", "a failed lookup reads as absent".
- **The measurement** — a distinctive count is the single best search key a repository like this
  has. `71 members`, `1,018 tests`, `356 failures`: whoever filed the sibling almost certainly quoted
  their number too.

When you find one, decide and **record the decision on the issue**: fold it in (with its own
RED→GREEN, per `batch-sibling-issues-by-file.md`), or state why it is genuinely distinct. Both
outcomes are useful; a silent overlap is not.

## Why this is not covered by the labels

A label is only as good as the sweep that applied it, and sweeps are built from whatever the
sweeper happened to key on. Measured on this repository: `area: metadata-conversion` had been
applied from the `blocked-by: metadata-emitter` label, so it inherited that label's blind spot
and **three issues on the same route carried no area label at all** — #3568, #3590 and #3491,
three of twenty-three, and #3568 was the same measurement programme as the issue it was missing
from. Each would have been found by a *different* one of the four keys above, which is why the
rule asks for three. Derivation: docs/incidents/search-for-the-same-defect-first.md.

So: **the labels are a starting point, never the answer to "has this been reported?"** Search the
text.

## The two failure modes this prevents

- **Two agents fixing one defect from opposite ends.** Both PRs green, both correct, and they
  conflict — or worse, they do not, and the second's proving test passes for free because the
  first already fixed it. A test that passes for the wrong reason is what `tdd.md` calls noise.
- **A partial fix that closes the wrong issue.** A describes the symptom, B the root cause.
  Fixing A and closing it leaves B open with no owner and no record that the cause is known — or
  closes B by association when only A's surface was tested.

## Say so even when you find nothing

"I searched for X, Y and Z and found nothing" is a real result and belongs in the PR body: it
says which stones were turned, and it is the difference between *no duplicate exists* and
*nobody looked*. An absence you did not measure is not a finding (`no-assumption-fixes.md`).

**Confirm every empty result a second way.** `grep` here is a shell function that rejects flags
and exits 0 with no output; `rg` skips dot-directories without `--hidden`; and `gh issue list
--search` matches differently from a `--jq` filter over titles. A zero from one query is not
evidence (`verify-execution-not-the-tick.md`).

## Sister rules

- `batch-sibling-issues-by-file.md` — the same scan keyed on **file**; run both, they find
  different things
- `check-open-prs-before-claiming.md` — an open PR carrying `Closes #N` means the issue is taken,
  whatever its labels say
- `no-assumption-fixes.md` — understand the defect before fixing it; a thin issue is not foldable
  until it is diagnosed
- `verify-execution-not-the-tick.md` — why a zero from a single query is not an answer

History: docs/incidents/search-for-the-same-defect-first.md
