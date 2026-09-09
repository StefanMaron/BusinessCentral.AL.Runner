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

## The three that get this right — copy one of them

- **`tools/ci-wait.py`, exit 3** — auth, network, no checks reported, a required-context set
  that could not be established without narrowing it, or the running file being behind
  `origin/main`. Two internals are the models: `rollup_is_final` returns
  `True`/`False`/**`None`**, with `None` "deliberately distinct from False: an unknown must
  never be resolved toward GREEN" (#2807); and a ruleset read **narrower** than the built-in
  floor is refused as `degraded` rather than judged on the smaller set, because a partial read
  and a deliberate removal look identical from there (#3002).
- **`.github/scripts/check_corpus_pin_forward.sh`, exit 3** via `die_undetermined`, at seven
  call sites (#3683): `.gitmodules` present but declaring no readable submodule path;
  `SUBMODULE_PATH` naming no submodule the repository declares; the submodule present at one
  endpoint and absent at the other — **adding or removing the corpus submodule is not a pin
  bump, and only a human reviewer can judge it**; an unchecked-out submodule; a corpus commit
  absent from the clone; a shallow clone, where the measurement is unreliable; and a
  `merge-base` that failed rather than answering. They share no common cause — what they share
  is that **none resolves toward success**. Enumerate those call sites rather than counting
  them — the definition line matches too, so `grep -c die_undetermined` over-answers by one.
- **`tools/corpus-pass-count.py`, `classify()`** — `ran` / `failed` / `not-run` / `no-suite`,
  so "not in this leg's suite" and "this leg never reached the test phase" cannot be read as
  "your tests did not run". A zero has three meanings and a bare grep gives all three the same
  answer.

## The worked example: three-way discrimination (#3299, #3681, PR #3683)

Two gate scripts hardcoded a submodule path tied to nothing in `.gitmodules`, so a rename or a
typo made `SUBMODULE_PATH` match nothing — reported as the **success** state, a green tick
forever with nothing behind it. The fix is not an unconditional assertion but a discrimination
over three cases:

| at the endpoint commits | verdict | why |
|---|---|---|
| `.gitmodules` **absent** at both | **0 — pass** | a repository that genuinely declares no submodule has nothing to un-pin |
| present, declaring paths, **none matching** the configured one | **3**, naming what it *does* declare | a typo is visible in the message rather than inferred from an absence |
| present but **unreadable** | **3**, deliberately *not* folded into row 1 | an absent file is the legitimate pass; an unreadable one is a broken measurement |

**Folding the third row into the first puts the broken case back on the exit-0 path** the
change exists to take it off.

Read `.gitmodules` at the **endpoint commits**, never the working tree, for the same reason the
pins are read there (#3261): under `actions/checkout` the working tree is `refs/pull/N/merge`.

## The constraint that stops the fix trading one defect for another

**A genuinely absent thing must stay a pass. Only an *unmeasurable* one becomes the third
state.** A fix that hard-errors a submodule-free repository has swapped a false green for a
false red.

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
   be established and what would fix it. `check_corpus_pin_forward.sh`'s messages are the
   model: each names its own cause, which sends the reader to the right remedy.
3. **Check it before the work, not after** — `check_count_baseline_history.sh` puts its
   `PIN_PATH` check *ahead of the changed-file scan* (#3681), because a `PIN_PATH` that names
   nothing has already made every verdict the script could reach meaningless, including the ones
   that look like passes.
4. **Keep the genuinely-absent case a pass**, per the constraint above.
5. **Prove the third state fires.** A refusal path with no test is indistinguishable from a
   never-fire path, which is the defect itself. `pr-gate.yml` discovers `test_*.sh` and
   `tools/test_*.py` siblings by glob, so a correctly-named test gates the day it lands (#3683).

**A guard that is safe only by accident of a neighbour is still on this list** — #3361 part 2
is one, where a leg summary that lost its `fail` key reads as zero failures. It is not reachable
as a false pass today, because a `summary.get("pass") != want` comparison a few lines down fails
loudly on a `None`; it is still written the opposite way from every neighbour in that function.

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
