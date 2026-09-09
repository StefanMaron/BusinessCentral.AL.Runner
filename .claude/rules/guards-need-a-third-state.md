# Every guard needs a third state, and it must not be spelled as its success state

A check has three answers, not two: *the thing is fine*, *the thing is broken*, and **I could
not tell**. Collapsing the third into the first produces a confident wrong verdict instead of a
loud one — a green tick asserting something nobody measured.

**This is not a missing pattern here.** The repository already has the vocabulary in three
places, and this rule exists because it is applied inconsistently, so each new instance gets
filed as an isolated bug instead of as the class it belongs to.

| the answer | the code |
|---|---|
| measured, and fine | 0 |
| measured, and broken | 1 |
| **could not measure** | **3** |

The number is a convention, not the point; `tools/corpus-pass-count.py` spells the third state
as a per-leg label rather than an exit code. What is not negotiable is that it is **distinct
from the success state**, and that the message says which of the three it is.

## The three that get this right — copy one of them

- **`tools/ci-wait.py`, exit 3.** "Could not determine state (auth, network, no checks
  reported, the required-context set could not be established without narrowing it, or THIS
  FILE is behind `origin/main`)." Two of its internals are worth reading as models:
  `rollup_is_final` returns `True`/`False`/**`None`**, with `None` documented as "deliberately
  distinct from False: an unknown must never be resolved toward GREEN (#2807)"; and a ruleset
  read that comes back **narrower** than the built-in floor is refused as `degraded` rather than
  judged on the smaller set, because "a partial read and a context genuinely removed by a person
  look identical here" (#3002).
- **`.github/scripts/check_corpus_pin_forward.sh`, exit 3** via `die_undetermined`, used at seven
  call sites — `.gitmodules` present but declaring no readable submodule path, `SUBMODULE_PATH`
  naming no submodule the repository declares, the submodule present at one endpoint and absent
  at the other, an unchecked-out submodule, a corpus commit absent from the clone, a shallow
  clone, and a `merge-base` that failed rather than answering. What they share is not a common
  cause: two name a checkout problem, one a change the guard has no basis to judge ("Adding or
  removing the corpus submodule is not a pin bump… A human reviewer should"), one a measurement
  the clone depth makes unreliable, three a broken measurement. What they share is that **none
  resolves toward success** — `die_undetermined` unconditionally exits 3.

  Count them with care: the definition line matches too, so a bare `grep -c die_undetermined`
  answers **eight**. The first draft of this file said five and listed four; the correction said
  five when #3683 had just made it seven. Both slips were the same one — trusting a count over
  the enumeration.
- **`tools/corpus-pass-count.py`, `classify()`** — `ran` / `failed` / `not-run` / `no-suite`, so
  "the codeunit is not in this leg's suite" and "this leg never reached the test phase" cannot
  be read as "your tests did not run". A zero has three meanings and a bare grep gives all three
  the same answer.

## The worked example: three-way discrimination (#3299, #3681, PR #3683)

The pattern to copy, because it shows the part that is easy to get wrong. Both gate scripts
hardcoded a submodule path that nothing tied to what `.gitmodules` declares. A rename or a typo
would make `SUBMODULE_PATH` match nothing — and both scripts reported that as their success
state, so every pull request would get a green tick forever with nothing behind it.

The fix is not an unconditional assertion. It is a discrimination over three cases:

| at the endpoint commits | verdict | why |
|---|---|---|
| `.gitmodules` **absent** at both | **0 — pass** | a repository that genuinely declares no submodule has nothing to un-pin |
| present, declaring paths, **none matching** the configured one | **3**, naming what it *does* declare | a typo is visible in the message rather than inferred from an absence |
| present but **unreadable** | **3**, deliberately *not* folded into row 1 | an absent file is the legitimate pass; an unreadable one is a broken measurement |

**That third row is the whole rule in one place.** Folding it into the first would put the
broken case back on the exit-0 path the change exists to take it off.

The `.gitmodules` read is against the **endpoint commits**, never the working tree, for the same
reason the pins are (#3261) — under `actions/checkout` the working tree is `refs/pull/N/merge`.

## The constraint that stops the fix trading one defect for another

**A genuinely absent thing must stay a pass. Only an *unmeasurable* one becomes the third
state.** Row 1 above is not a concession; it is the constraint, and a fix that hard-errors a
submodule-free repository has swapped a false green for a false red.

The same constraint has a second, sharper form in `tools/agent_self_freshness.py`, which splits
"could not establish" into three (#3296) and refuses only one of them:

- **detached** — the file is not in a git repository, because the caller extracted it there.
  That extraction is the remedy the module prints, and "a remedy that refuses itself is not a
  remedy."
- **identical** — no merge base (shallow clone), but the file is byte-identical to
  `origin/main`'s blob. Identity is provenance.
- **unvouched** — everything else. Only this refuses.

So the split is by **what remedy the message must send the reader to**, not by how uncertain the
check feels. A missing *local* `origin/main` ref is deliberately excluded from `unvouched`: CI
checks out with `fetch-depth 1`, so that ref never exists on any run while the remote answers
fine, and conflating the two refused every CI run in the first version of that fix.

## The instances, and their states as of 2026-09-09

The class was visible only because six issues were reviewed in one pass; each had been filed
separately as its own defect.

| issue | the "could not tell" case | was reported as | state |
|---|---|---|---|
| #3296 | `agent_self_freshness` cannot establish provenance | full GREEN, exit 0 | **closed**, fixed |
| #3351 | `ci-wait.py --timeout 0` — no poll could occur | exit 2, a verdict-shaped non-verdict | **closed**, fixed |
| #3299 | `SUBMODULE_PATH` matches nothing | exit 0, identical output to a real forward-bump | **open**, fixed in PR #3683 |
| #3681 | `check_count_baseline_history.sh`'s `PIN_PATH` matches nothing | exit 0, "does not move the pin", on every PR forever | **open**, fixed in PR #3683 |
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

## Writing one

1. **Enumerate the ways the measurement can fail to happen**, separately from the ways the
   subject can be broken. Missing input, unreachable network, a pattern matching nothing, a
   file absent, a key absent, a subprocess that failed rather than answering.
2. **Give each a verdict that is not the success state**, and a message naming what could not be
   established and what would fix it. `check_corpus_pin_forward.sh`'s messages are the model:
   each names its own cause — a checkout problem, a clone depth, a change only a human can judge —
   rather than a generic refusal, which sends the reader to the right remedy instead of blaming
   the author.
3. **Check it before the work, not after.** #3681's guard puts the `PIN_PATH` check ahead of the
   changed-file scan, "because a `PIN_PATH` that names nothing has already made every verdict
   this script could reach meaningless — including the ones that look like passes."
4. **Keep the genuinely-absent case a pass**, per the constraint above.
5. **Prove the third state fires.** A refusal path with no test is indistinguishable from a
   never-fire path, which is the defect itself. Both scripts in PR #3683 gained cases in their
   `test_*.sh` siblings; `pr-gate.yml` discovers those by glob, so a correctly-named test gates
   the day it lands.

## The same shape one level down

At the *tool-use* level this class is already documented, which is why the guards are the gap:
`CLAUDE.md` has `grep -E` exiting 0 after rejecting a flag, and `rg` skipping dot-directories;
`verify-execution-not-the-tick.md` has five more, including `gh api .../logs` writing nothing
and exiting 0 on a log carrying ANSI colour. **A zero from a pattern you chose is not
evidence** — there, and in anything you write.

## Sister rules

- `ci-verdicts.md` — exit 3 in practice: what it means when `ci-wait.py` returns it, and why
  a narrowed ruleset read is refused rather than judged
- `verify-execution-not-the-tick.md` — five false zeros in the corpus-execution check, the
  tool-use half of this class
- `loud-failures.md` — the runtime companion: a surface the runner cannot support throws
  loudly rather than returning a default
- `no-assumption-fixes.md` — a zero you cannot attribute is not a diagnosis
- `file-issues-for-gaps.md` — a sixth instance gets filed, not silently worked around
