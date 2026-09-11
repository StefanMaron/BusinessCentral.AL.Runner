# Incidents behind .claude/rules/loud-failures.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the measurement that produced them.

### The justification is a claim plus a citation, not the derivation behind it

What this section bounds is the *form* of that statement, because the requirement was being
discharged by writing the whole investigation into the file.

Measured over `AlRunner/Patches/` (155 files, 30,655 comment lines, 48.4% of non-blank lines),
attributing each comment block by its vocabulary:

| | share of comment mass in `Patches/` |
|---|---|
| blocks stating an equivalence/scope claim only | 8.6% |
| blocks that are pure derivation — corpus narration, measurements, rejected attempts | 25.1% |
| blocks mixing a claim with its derivation | 18.8% |
| blocks doing neither (ordinary explanation) | 47.5% |

85% of that pure-derivation mass sits in blocks longer than ten lines. So the obligation
accounts for well under a tenth of what is in these files, and the prose it is embedded in
accounts for several times more — which is the evidence that the *form*, not the requirement,
is what needs bounding.

## The account is what gets checked (2026-09-11, #3399)

Four rounds of correction on one claim, none of which moved the finding.

An implementation agent measured six codeunit ids against Microsoft's shipped `.app` files and
found four of six rows in `_knownDependencyCodeunits` wrong. That result was correct in round
one and never changed. What changed four times was the stated method.

| round | claim about how the ids were reached | status |
|---|---|---|
| 1 | "the runtime platform apps ship without a `SymbolReference.json`" | false — all 113 carry one |
| 2 | coordinator: "the file is present but its array is empty" | false — true of 5 apps, none holding the missing ids |
| 3 | agent: "recurse into nested `.app`; symbols then answer all six" | right mechanism, and the figure was right |
| 4 | coordinator: "symbols answer only three of six" | false — the coordinator's reader lacked the `Namespaces` walk |

The resolution is two **independent** recursion axes — nested `.app` files, and the `Namespaces`
tree inside a symbol file. Id 310 needs both, so it is invisible to either single-axis reader,
and a reader who fixes one axis finds five of six and looks complete.

Each party named the axis they had missed and had in fact implemented the other. Neither could
have found the pair alone: the observation was available only because two parties disagreed with
different instruments in hand.

Two things this measured, both now in the rule:

- **A right answer with a wrong account is indistinguishable from a wrong answer.** The agent
  held the correct result for three rounds and could not defend it, because a reviewer can only
  check the account. The reviewer was behaving correctly at every step.
- **Re-measuring is what made it converge.** Every round re-ran a scan rather than restating a
  position. A disagreement kept attached to something measurable converges; one that is not
  becomes two positions and hardens.

The four wrong rows, the two false-message sites, and the mutation results never moved across
any round.
