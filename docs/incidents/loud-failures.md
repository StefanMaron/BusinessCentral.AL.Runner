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
