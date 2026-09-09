# Incidents behind .claude/rules/ask-the-corpus-before-claiming-bc-behavior.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## When the Linux tier is the thing in doubt, ask Windows — do not reason about it

**The fourth row is the one that will bite, and it bit on this rule's first use.** A run that
dies before executing a test still reports `conclusion: failure`. Read that as a verdict and
you land on row 1 — *change the corpus assertion* — which is precisely what the last paragraph
of this section forbids, arrived at by following the table. So the conclusion is not the thing
to read:

That refusal is the signal. **A `failure` with zero tests parsed is not Windows disagreeing
with you; it is Windows not having been asked.** Measured 2026-09-08: two dispatches against
corpus PRs #272 and #273 both died in the nightly's tenant-encryption-key step, before any test
ran (corpus #288). Three runs earlier the same day had succeeded, so this is a thing that
happens to a working workflow, not a permanent state — which is exactly why it has to be
recognised rather than assumed away.

### The cost of not reaching for this first

Measured 2026-09-08. Two corpus PRs (#272, #273) sat red on their cloud legs. A Linux tier
defect had just been fixed upstream (`2b0d91f8`, forcing `CommunicationBroker.Async = false`
and so disabling BC's own notification coalescing), and the failures matched its shape
closely — one of them read `Expected:<1> Actual:<2>`, a message delivered twice, which is
exactly what disabled coalescing produces.

The inference was written up on both PRs as a hypothesis, with single-leg re-runs attached.
Both re-runs failed **identically** on the fixed tier, and the hypothesis was retracted.

The reasoning was sound and the conclusion was wrong: **a symptom matching a mechanism is not
evidence that mechanism produced it.** What made it recoverable was attaching the check to the
claim rather than publishing a finding. What would have avoided it entirely was dispatching
Windows first — one command, against a documented authority, instead of an argument about
which tier to believe.

## The incidents this rule is made of

**#2144 — the container differential lost, and a self-inflicted failure got classified
instead of reverted.** The differential said `TestIsolation = Codeunit` rolls the database
back per test; Microsoft's documentation said per codeunit; the corpus test agreed with the
documentation, and the container measurement was an artifact of a harness that invoked tests
one at a time and could not tell a platform rollback from a new transaction. The same change
cited a codeunit 130452 "Test Runner - Isol. Test" that does not exist — 130452 is "Test
Runner - Get Methods". A name is not evidence. Its 20 `expect-fail-known-gap` entries existed
only because that same PR had changed the default isolation mode in a way real BC does not;
reverting the change made all 20 pass. **Revert a self-inflicted failure — do not classify it.**

**#2170 — "the corpus contradicts itself" was falsified by the corpus CI.** Three tests
looked identical (uncommitted `Insert`, unrelated `asserterror`, then a read) and were read
as contradictory; all three pass on BC 27.5 and 28.3. If two corpus tests look like they
assert opposite things about the same AL shape and both pass upstream, the shape is not the
same and you have not found the distinction yet — a fact about your reading, not about the
corpus. **An entry whose `Note` asserts something about BC that no service tier has confirmed
is a guess wearing a schema.**

So: **never propose inverting an upstream assertion that is green on a service tier** — a PR
into the corpus that flips a passing test is asking a service tier to disagree with itself.
Name the mechanism you found, not the symptom you could not explain.

## What an `expect-fail-known-gap` entry may rest on

Every test in the corpus passes on real BC by construction, so *every* known-gap entry is for
a test green upstream. The mode means exactly: the surface is in scope, real BC does it, the
runner does not do it yet, and `Issue` tracks the work. (An earlier version of this rule said
never to declare a known gap for a test green upstream; that was wrong and contradicted
`docs/expectations.md`.) An entry is honest when it says that. It is dishonest when it
converts a live question, or a self-inflicted regression, into settled classification.

## The tier is patched, so check before quoting it on a UI surface

The corpus CI boots a Linux BC image that installs ~30 numbered patches into BC's own
assemblies at startup. Most are faithful. One that is not turns a corpus result on that
surface into a measurement of the patch, and the green direction is the one nobody notices —
a test asserting "nothing happens" records the patch as BC behaviour. That is not
hypothetical: Patch #21 no-opped `NavOpenTaskPageAction.ShowForm` and blinded every route
that opens a page through an action (#2986). It has since been fixed and the surface
re-measured open on all eight legs, so it is a verdict again.
