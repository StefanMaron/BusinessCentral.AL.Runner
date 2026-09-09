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
| #3361 (part 2) | a leg summary lost its `fail` key | zero failures | **open** |

**#3681 is the argument for writing this down.** `check_count_baseline_history.sh` landed on
`main` the morning of 2026-09-09 (#3666) as a guard against a corpus pin bump that silently
writes no history entry — and shipped with a silent never-fire path of its own, filed the same
day at 11:03Z. A guard authored to catch a silent omission reproduced the shape it was built to
catch, two days after two instances of it had been fixed. Naming the class is what makes the
fifth instance a lookup instead of a rediscovery.

**#3361 part 2 is the outstanding one**, and its own body records the honest qualifier: that
spot is currently backstopped by a `summary.get("pass") != want` comparison a few lines down
where a `None` fails loudly, so it is not reachable as a false pass **today**. It is still
written the opposite way from every neighbour in that function, where an uncomputable value is
an explicit refusal rather than a silent zero. A guard that is safe only by accident of a
neighbour is on this list.

## The instances (status moved from the rule, #3728 review round 2)

#3361 part 2 was open when this rule was written, and its own body records the honest qualifier:
that spot is currently backstopped by a `summary.get("pass") != want` comparison a few lines down
where a `None` fails loudly, so it is not reachable as a false pass **today**. It is still written
the opposite way from every neighbour in that function, where an uncomputable value is an explicit
refusal rather than a silent zero.

## Counting `die_undetermined` (moved from the rule, #3728 review round 3)

Count them with care: the definition line matches too, so a bare `grep -c die_undetermined`
answers **eight**. The first draft of this file said five and listed four; the correction said
five when #3683 had just made it seven. Both slips were the same one — trusting a count over the
enumeration.
