# Incidents behind .claude/rules/guards-need-a-third-state.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## The three that get this right — copy one of them

  Count them with care: the definition line matches too, so a bare `grep -c die_undetermined`
  answers **eight**. The first draft of this file said five and listed four; the correction said
  five when #3683 had just made it seven. Both slips were the same one — trusting a count over
  the enumeration.

## The worked example: three-way discrimination (#3299, #3681, PR #3683)

The pattern to copy, because it shows the part that is easy to get wrong. Both gate scripts
hardcoded a submodule path that nothing tied to what `.gitmodules` declares. A rename or a typo
would make `SUBMODULE_PATH` match nothing — and both scripts reported that as their success
state, so every pull request would get a green tick forever with nothing behind it.


## The instances, and their states as of 2026-09-09

The class was visible only because six issues were reviewed in one pass; each had been filed
separately as its own defect.

| issue | the "could not tell" case | was reported as | state |
|---|---|---|---|
| #3296 | `agent_self_freshness` cannot establish provenance | full GREEN, exit 0 | **closed**, fixed |
| #3351 | `ci-wait.py --timeout 0` — no poll could occur | exit 2, a verdict-shaped non-verdict | **closed**, fixed |
| #3299 | `SUBMODULE_PATH` matches nothing | exit 0, identical output to a real forward-bump | **open**, fixed in PR #3683 |
| #3681 | `check_count_baseline_history.sh`'s `PIN_PATH` matches nothing | exit 0, "does not move the pin", on every PR forever | fixed in PR #3683; the script itself went with the corpus pin at #3737 |
| #3361 (part 2) | a leg summary lost its `fail` key | zero failures — a **live** false green, not the unreachable one three sources recorded | fixed in PR #3856 |

**#3681 is the argument for writing this down.** `check_count_baseline_history.sh` landed on
`main` the morning of 2026-09-09 (#3666) as a guard against a corpus pin bump that silently
writes no history entry — and shipped with a silent never-fire path of its own, filed the same
day at 11:03Z. A guard authored to catch a silent omission reproduced the shape it was built to
catch, two days after two instances of it had been fixed. Naming the class is what makes the
fifth instance a lookup instead of a rediscovery.

**#3361 part 2 was the outstanding one, and the qualifier attached to it was false** — fixed and
corrected by #3856.

Three sources recorded it as unreachable: the issue body, the reviewer's comment on it, and this
rule. All three credited the same neighbour, a `summary.get("pass")` comparison a few lines below
the defect, said to fail loudly on a `None`. Two errors compounded:

- the comparison is against **`counted`**, the sum of the per-bundle PASS lines — not a `want`
  from a baseline, which is what the phrasing implied and what would have made a `None` fail;
- it never sees a `None` in the shape that occurs. `parse_corpus_run` seeds the summary from
  `Tests: N total` and adds a key only when its line appears, so a block truncated after
  `pass:` yields `{"total", "pass"}`: `fail` absent, `pass` **present and agreeing**.

Both guards therefore pass cleanly. Executed on `main`, in order, with the guards as written:

```
healthy run              -> PASS -- baseline reproduced
real failures            -> FAIL: tests failed
TRUNCATED after 'pass:'  -> PASS -- baseline reproduced      <-- the false green
```

`check_corpus` reporting `the corpus ran clean on this box: 15 tests passed across 3 app(s)` for
a run that never said whether anything failed — in the check gating every unattended cycle. The
issue had ranked it the lowest blast radius of six; it was a live false green.

**The lesson is the one the rule already taught, sharpened.** "Safe only by accident of a
neighbour" was recorded as a real-but-acceptable state; it is not. A neighbour that does not
cover the case is indistinguishable from one that does, until something executes the guard on
that input — which is the same false-zero class the rule is about, applied to a reader instead of
a check. Three readers is the measurement: the count is what makes it a property of the reasoning
rather than one person's slip.

## The instances (status moved from the rule, #3728 review round 2)

#3361 part 2 was open when this rule was written, and this section carried the same "not
reachable as a false pass **today**" qualifier the rule did. #3856 measured it and it was false —
the correction, the mechanism and the executed evidence are above, under the fifth instance.
Stated once rather than twice: a claim repeated in two places is a claim that can be corrected in
one of them.

## Counting `die_undetermined` (moved from the rule, #3728 review round 3)

Count them with care: the definition line matches too, so a bare `grep -c die_undetermined`
answers **eight**. The first draft of this file said five and listed four; the correction said
five when #3683 had just made it seven. Both slips were the same one — trusting a count over the
enumeration.
