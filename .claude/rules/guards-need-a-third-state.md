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

- **`tools/ci-wait.py`, exit 3** — copy `rollup_is_final`, which returns
  `True`/`False`/**`None`**: an unknown must never be resolved toward GREEN (#2807).
- **`.github/scripts/resolve_corpus_ref.sh`, exit 3** — **no** `Corpus-PR:` line resolves
  `master` and passes; a **malformed** line, **two** lines, an unreadable corpus PR, or one
  closed unmerged refuses. Every wrong answer would otherwise have been `master`, the right
  answer for the ordinary case, so nothing would say which happened (#3737).
- **`tools/corpus-pass-count.py`, `classify()`** — `ran` / `failed` / `not-run` / `no-suite`,
  because a zero has three meanings and a bare grep gives all three the same answer.
- **`TestArtifacts.SkipIfMissingIn`** — missing artifacts are an honest skip on a dev box and
  impossible by construction on a CI leg, so there it **fails**: if provisioning moved, every
  test would skip visibly with an accurate reason and the leg would still be green. Scoped to
  that gate rather than a blanket `Skipped: 0`, which would fail every local run.

## The worked example: three-way discrimination (#3299, #3681, PR #3683, #3737)

| the thing being read | verdict | why |
|---|---|---|
| genuinely **absent** | **0 — pass** | nothing declared is a legitimate state, not a broken measurement |
| present, but **naming nothing that exists** | **3**, naming what it *does* say | a typo is visible in the message rather than inferred from an absence |
| present but **unreadable** | **3**, deliberately *not* folded into row 1 | an absent thing is the legitimate pass; an unreadable one is a broken measurement |

**Folding the third row into the first puts the broken case back on the exit-0 path.** The
origin (two gate scripts whose hardcoded path matched nothing and reported success) is in
`docs/incidents/guards-need-a-third-state.md`; the live instance is `resolve_corpus_ref.sh`.

## The constraint that stops the fix trading one defect for another

**A genuinely absent thing must stay a pass. Only an *unmeasurable* one becomes the third
state.** A fix that hard-errors a submodule-free repository has swapped a false green for a
false red.

**But "genuinely absent" can depend on where you are running** — `SkipIfMissingIn` above. Ask
which environments the condition can legitimately occur in before deciding it is a pass.

**Split by what remedy the message must send the reader to**, not by how uncertain the check
feels. `tools/agent_self_freshness.py` splits "could not establish" into **detached** (the
caller extracted the file — the module's own remedy, so it must not refuse), **identical**
(no merge base, but byte-identical to `origin/main`'s blob) and **unvouched** — only the last
refuses (#3296). A missing *local* `origin/main` ref is not `unvouched`: CI never has one.

### A reflection bind that answers null is unmeasurable, not absent

**A `null` from a reflection lookup means "I could not find it", never "it is not needed" — so
it refuses**; a fallback that proceeds without it is the success state wearing the third
state's clothes, indistinguishable from the correct answer at every call site (#4147, PR #4192:
a static extension method bound against the wrong type, every row passed through unfiltered,
nothing threw).

**The two null-producing causes are indistinguishable at the bind and have opposite
remedies** — *BC renamed it* (refuse, `BcShapeGapException`) and *I looked in the wrong place*
(fix the lookup); neither is "carry on without it". Bind BC members through
`BcShape.FindMethod`, make the bind **required**, and read the member's real signature — a
decompiled call site shows extension methods as instance calls, and `MethodInfo.Invoke` does not
apply C# parameter defaults.

## Writing one

1. **Enumerate the ways the measurement can fail to happen**, separately from the ways the
   subject can be broken: missing input, unreachable network, a pattern matching nothing, a
   file absent, a key absent, a subprocess that failed rather than answering.
2. **Give each a verdict that is not the success state**, and a message naming what could not
   be established and what would fix it.
3. **Check it before the work, not after** — `resolve_corpus_ref.sh` refuses an UNSET
   `PR_BODY` before parsing, because a resolver handed nothing answers `master` for everything.
4. **Keep the genuinely-absent case a pass**, per the constraint above.
5. **Prove the third state fires.** A refusal path with no test is indistinguishable from a
   never-fire path, which is the defect itself. `pr-gate.yml` discovers `test_*.sh` and
   `tools/test_*.py` siblings by glob, so a correctly-named test gates the day it lands (#3683).

**A guard that is safe only by accident of a neighbour is not safe.** A neighbour is evidence
only when you have executed the guard on the input you are claiming it covers: #3856 found a
backstop three readers had credited that did not cover the truncated-summary case, and the
guard returned PASS on it.

## The same shape one level down

At the *tool-use* level this class is documented in `CLAUDE.md` (`grep -E`, `rg`) and
`verify-execution-not-the-tick.md`. **A zero from a pattern you chose is not evidence**, there
and in anything you write.

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
