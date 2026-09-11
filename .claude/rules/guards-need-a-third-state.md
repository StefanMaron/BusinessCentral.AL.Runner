# Every guard needs a third state, and it must not be spelled as its success state

A check has three answers, not two: *the thing is fine*, *the thing is broken*, and **I could
not tell**. Collapsing the third into the first produces a confident wrong verdict instead of a
loud one — a green tick asserting something nobody measured.

| the answer | the code |
|---|---|
| measured, and fine | 0 |
| measured, and broken | 1 |
| **could not measure** | **3** |

The number is a convention; `tools/corpus-pass-count.py` spells the third state as a per-leg
label instead. What is not negotiable is that it is **distinct from the success state**, and
that the message says which of the three it is.

## The four that get this right — copy one of them

- **`tools/ci-wait.py`, exit 3** — the model to copy is `rollup_is_final`, which returns
  `True`/`False`/**`None`**, with `None` "deliberately distinct from False: an unknown must
  never be resolved toward GREEN" (#2807). What each of its refusals means, including the
  narrowed-ruleset one, is `ci-verdicts.md`'s to state.
- **`.github/scripts/resolve_corpus_ref.sh`, exit 3** — which corpus a run measures. A body
  with **no** `Corpus-PR:` line resolves `master`, and that stays a pass; a body whose line is
  **malformed**, or which declares **two**, refuses. The distinction is the whole guard: both
  wrong answers would be `master`, which is also the right answer for the ordinary case, so a
  typo and a deliberate omission would be indistinguishable and nothing would say so.
  `.github/actions/resolve-corpus-ref` extends it — a corpus PR that cannot be read, or is
  closed without having merged, refuses rather than falling back to `master` (#3737).
- **`tools/corpus-pass-count.py`, `classify()`** — `ran` / `failed` / `not-run` / `no-suite`,
  so "not in this leg's suite" and "this leg never reached the test phase" cannot be read as
  "your tests did not run". A zero has three meanings and a bare grep gives all three the same
  answer.
- **`TestArtifacts.SkipIfMissingIn`** — the third state where the *same* condition is a
  legitimate pass in one environment and a defect in another. Artifacts missing on a dev box is
  an honest skip; on a CI leg it is impossible by construction, so it **fails** there instead.
  Its own comment says why a visible skip is not enough: *"if the workflow moves where it
  provisions artifacts, `Present` answers false for EVERY test, all of them skip — visibly, with
  an accurate reason — and the leg is still GREEN."* A correct per-test answer, and the run above
  it still asserts nothing. Deliberately scoped to that gate rather than a blanket `Skipped: 0`
  assertion, which would fail every local run for a correct reason.

## The worked example: three-way discrimination (#3299, #3681, PR #3683, #3737)

Two gate scripts hardcoded a submodule path tied to nothing in `.gitmodules`, so a rename or a
typo made `SUBMODULE_PATH` match nothing — reported as the **success** state, a green tick
forever with nothing behind it. (Both scripts went with the corpus pin at #3737; the shape they
taught is what stays.) The fix is not an unconditional assertion but a discrimination over
three cases:

| the thing being read | verdict | why |
|---|---|---|
| genuinely **absent** | **0 — pass** | nothing declared is a legitimate state, not a broken measurement |
| present, but **naming nothing that exists** | **3**, naming what it *does* say | a typo is visible in the message rather than inferred from an absence |
| present but **unreadable** | **3**, deliberately *not* folded into row 1 | an absent thing is the legitimate pass; an unreadable one is a broken measurement |

**Folding the third row into the first puts the broken case back on the exit-0 path** the
change exists to take it off.

The live instance is `resolve_corpus_ref.sh` (above): no `Corpus-PR:` line is row 1, a
malformed one is row 2, and a corpus pull request that cannot be read is row 3 — and all three
would otherwise have produced the same corpus.

## The constraint that stops the fix trading one defect for another

**A genuinely absent thing must stay a pass. Only an *unmeasurable* one becomes the third
state.** A fix that hard-errors a submodule-free repository has swapped a false green for a
false red.

**But "genuinely absent" can depend on where you are running.** `SkipIfMissingIn` above is the
case: the same missing directory is a legitimate absence locally and a provisioning defect on
CI, so one verdict for both would be wrong in one of them. Ask which environments the condition
can legitimately occur in before deciding it is a pass.

`tools/agent_self_freshness.py` has the sharper form, splitting "could not establish" into
three and refusing only one (#3296):

- **detached** — the file is not in a git repository, because the caller extracted it there.
  That extraction is the remedy the module prints, and a remedy that refuses itself is not one.
- **identical** — no merge base (shallow clone), but the file is byte-identical to
  `origin/main`'s blob. Identity is provenance.
- **unvouched** — everything else. Only this refuses.

Split by **what remedy the message must send the reader to**, not by how uncertain the check
feels. A missing *local* `origin/main` ref is deliberately excluded from `unvouched`: CI checks
out with `fetch-depth 1`, so that ref never exists on any run while the remote answers fine,
and conflating the two refused every CI run in the first version of that fix (#3296).

## Writing one

1. **Enumerate the ways the measurement can fail to happen**, separately from the ways the
   subject can be broken: missing input, unreachable network, a pattern matching nothing, a
   file absent, a key absent, a subprocess that failed rather than answering.
2. **Give each a verdict that is not the success state**, and a message naming what could not
   be established and what would fix it. `resolve_corpus_ref.sh`'s messages are the model:
   each names its own cause, which sends the reader to the right remedy.
3. **Check it before the work, not after** — `resolve_corpus_ref.sh` refuses an UNSET
   `PR_BODY` before it parses anything, because a resolver handed nothing would answer
   `master` for every pull request in the repository, and every verdict it could then reach is
   meaningless, including the ones that look like passes (the shape #3681 fixed for the pin
   guards, which have since been removed).
4. **Keep the genuinely-absent case a pass**, per the constraint above.
5. **Prove the third state fires.** A refusal path with no test is indistinguishable from a
   never-fire path, which is the defect itself. `pr-gate.yml` discovers `test_*.sh` and
   `tools/test_*.py` siblings by glob, so a correctly-named test gates the day it lands (#3683).

**A guard that is safe only by accident of a neighbour is not safe** — and this rule said
otherwise about its own outstanding instance until #3856 measured it. #3361 part 2, where
`check_corpus`'s summary lost its `fail` key and read as zero failures, was recorded here as
unreachable "because a `summary.get("pass") != want` comparison a few lines down fails loudly on
a `None`". Both halves were wrong: the comparison is against `counted`, the per-bundle PASS
lines, and on the shape that actually occurs — a summary truncated after `pass:` — `pass` is
present **and agrees**, so both guards pass and `preflight.py` reports a reproduced baseline for
a run that never said whether anything failed. Executed on `main`, the truncated summary returned
PASS (#3856; derivation in the incidents file).

Three independent readers checked that neighbour and all three credited it. **So a neighbour is
evidence only when you have executed the guard on the input you are claiming it covers** — the
backstop was real, and simply did not cover the case, which is indistinguishable from covering it
until something runs it.

## The same shape one level down

At the *tool-use* level this class is already documented — `CLAUDE.md` for `grep -E` and `rg`,
`verify-execution-not-the-tick.md` for five more. **A zero from a pattern you chose is not
evidence**, there and in anything you write.

## Sister rules

- `ci-verdicts.md` — exit 3 in practice: what it means when `ci-wait.py` returns it, and why
  a narrowed ruleset read is refused rather than judged
- `verify-execution-not-the-tick.md` — five false zeros in the corpus-execution check, the
  tool-use half of this class
- `loud-failures.md` — the runtime companion: a surface the runner cannot support throws
  loudly rather than returning a default
- `no-assumption-fixes.md` — a zero you cannot attribute is not a diagnosis
- `file-issues-for-gaps.md` — a sixth instance gets filed, not silently worked around

History: docs/incidents/guards-need-a-third-state.md
