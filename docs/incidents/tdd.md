# Incidents behind `tdd.md`

## Two tests that passed against implementations doing nothing (2026-09-11)

`tdd.md` has always carried the right question — *would this test still pass if the
implementation always returned a default value?* — as something to **ask**. In one session
the honest answer was **yes** twice, and in neither case did asking surface it. Both were
found by executing the mutation: remove the implementation, rebuild, observe that the tests
stay green.

### Instance 1 — PR #3819 (#3504): the fixture constructed the relationship under test

The change translated a declaration read from `SymbolReference.json` into the key BC's live
binding table uses, by matching the raw AL identifier against each registered expression's
`Name`.

The unit fixtures were built as `FakeExpression("p790p790PageEditable", "PageEditable")` —
that is, they *asserted* that `Name` holds the raw AL identifier and the key holds the mangled
`Id`. Instrumenting page 790's live binding table on 28.1 showed otherwise:

```
[rv] key='p790p790PageEditable'  name='p790p790PageEditable'
[rv] key='Control159866013'      name='GLAccTotaling'
```

`Name` holds the mangled `Id` for exactly the population the fix targeted. The join therefore
fell through to `return declared;` for **all 7,173 expression-bound declarations** — a no-op.
The four accompanying AL tests all declared *literals*, which short-circuit before the lookup
runs, so they could not have exercised it either.

Three rounds of discussion treated the join's **correctness** without anyone noticing it never
executed. What settled it was dumping the live table, reproduced 3/3 and twice more on a
pristine uninstrumented rebuild.

The commit that removed the join (`eef47c8a`) states it directly: *"every test passed anyway
because the unit fake CONSTRUCTED the relationship it was meant to prove … tdd.md's question
answered the wrong way: the tests did pass against an implementation that did nothing."*

A second measurement from the same commit is why no repair was attempted: across the 92
`<Expression>` entries in 2,272 captured page-metadata documents, 59 needed a join, and page
60265 registers **two** keys for one identifier (`Control144826568` and `p60265p60265HideIt`,
both `SourceExpression='HideIt'`). The relation is one-to-many, so no mangling rule
reconstructs it.

### Instance 2 — PR #3882 (#2247): one of two guards was unproven at review

The PR adds a loud failure on a partial dependency emit at two call sites.

| guard | mutation result |
|---|---|
| `DependencyLoader.LoadOne` (`EMIT-EXCLUDED`) | `Failed: 1, Passed: 7` — sound |
| `DependencyMetadataProducer.Ensure` (`METADATA-EMIT-EXCLUDED`) | **all 8 green**, across two runs |

Confirmed a second, differently-shaped way: the only test references to the second stage name
assert the **naming convention** (`Assert.True(DependencyLoader.IsMetadataStage("METADATA-EMIT-EXCLUDED"))`)
or pass it as routing data. Nothing drives the throw.

The contrast is what makes the case: the same PR, the same author, the same session, one guard
proven and one not — and the difference was invisible until the mutation ran.

This one was caught in review and fixed before merge, so it never reached `main`. That is the
outcome the step exists to produce, and it is why the instance is worth recording: nothing
other than the mutation distinguished the two guards.

### Why asking is weaker than doing

The question is answered by reasoning about code you have just written while believing it
works, which is when introspection is least reliable. Executing the mutation is one rebuild and
is not a judgement call: the test goes red or it does not.

Note the asymmetry in cost. A test that fails when it should pass is found immediately, by CI.
A test that passes when it should fail is found only if someone looks — and it then protects
nothing for as long as it exists, while *reading* like coverage. #3819's fixtures would have
gone on asserting a false relationship indefinitely.

### Scope deliberately not taken

No mutation-testing framework, no tooling, no new CI job. The change is to which check is
**required** and that its result is **reported**, so a reviewer can see a number instead of
re-deriving it. Both instances were caught by a human-directed mutation taking one rebuild.


## The mutation that does not land (2026-09-11, #3895)

Requiring an executed mutation (above) created a second-order failure the first rule did not
anticipate: **a mutation that silently fails to apply leaves the test green**, and green after a
mutation is indistinguishable from "my test does not catch this" without looking at the file.

Two instances in the session that introduced the requirement, by different mechanisms:

**The edit never reached the file.** A reviewer mutating a backslash in `AlRunner.csproj` got a
passing test twice and nearly reported a working parity test as broken. In Python source `'\\'`
*is* the single-backslash string, and a shell heredoc collapsed it again, so the "mutated" file
was byte-identical. The third attempt landed and gave the real answer:

```
MUTATED_RC=1
Assert.Equal() Failure: Strings differ
Expected: ...Replace('\\','/')...
Actual:   ...Replace('\','/')...
```

**The build never reran.** Disabling the analyzer target with
`-p:EnableAspNetCoreAnalyzers=false` reported `AD0001=0`, reading as "the target is a no-op".
The build was incremental and had skipped `CoreCompile`; a forced clean rebuild showed the
analyzers present. The same session also produced two *invalid* mutations of a third kind:
renaming a target carrying `BeforeTargets="CoreCompile"` does not disable it, and wiping `obj/`
wipes the restore.

So three distinct ways to not-mutate, all presenting as one green test.

**Why this is not the existing false-zero class.** `CLAUDE.md` and
`verify-execution-not-the-tick.md` cover a zero from a **query**. Here the instrument is an
**edit**, and the direction is worse: a failed search returns nothing and looks like a finding,
while a failed mutation returns green and looks like the system working correctly.

Both were caught by the person running them noticing the result was too clean, and both were
reported rather than quietly fixed — which is the only reason there are two instances to write
down instead of one.
