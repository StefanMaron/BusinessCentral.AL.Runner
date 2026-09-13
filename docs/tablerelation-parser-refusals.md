# The TableRelation parser's two refusal sites (#3326)

`AlRunner/Patches/RecordPatches.AlSourceParser.cs` refuses a `TableRelation` in two places.
Both used to write a line to stderr and return `null`, dropping the whole relation. Since
#3326 both throw.

This file records the measurement behind that choice, so nobody has to re-derive it.

## Why a silent drop was worse here than in the builder

#3306 fixed the same end state one layer down, in `BuildMetaFieldRelations`: a relation whose
target table or field NAME did not resolve was dropped, so `Validate` accepted a value real BC
refuses and `FieldRef.Relation` answered 0. It records the reason and refuses at
`RecordImplementation.EvaluateRelation`.

The parser's sites set `ParsedField.RelationArms = null`, and that never reaches the builder at
all — so #3306's guard cannot see it. Same AL-observable wrong answer, with nothing downstream
able to catch it.

## Two provenances, and they do not get the same answer

The parser has **two** entry points, and conflating them is the trap in this issue. `#3326`
described the source path; the symbol path is the one that decides the design.

| entry point | where the relation text comes from | vetted by the AL compiler? | right answer on an unrepresentable shape |
|---|---|---|---|
| `ParseFieldSyntax` -> `ParseRelationArms` | AL this runner is compiling | **yes** | **throw** — reaching it means the runner's model of AL diverged from the compiler's |
| `TryParseRelationArmsText` -> `ParseRelationArms` | a **string** in a precompiled `.app`'s `SymbolReference.json` | **no** | **return null** — refusing the property is the correct permanent answer |

`ParseRelationArms` and `RelationConditionList` therefore take a `fromCompiledSource` flag, and
the condition-shape site branches on it. The symbol path has two call sites —
`BcAppSymbolCache.cs` and `BcAppSymbolCache.TableExtensions.cs` — and both pass `false`.

Getting this wrong is not theoretical: an unconditional throw broke the five
`TableRelationWhereFieldLinkTests`, which exist for #2518 and deliberately pin the null on the
symbol path. Field 10 of that fixture puts a `field()` link in an `if()` condition precisely
because BC's `MetaCondition` cannot represent it, so refusing that property is BC's own
constraint rather than a runner limitation.

## Why the source path throws instead of recording a note

#3326 offered both routes and asked which the evidence supports. On the source path it is the
throw, because there is no observable state to record a note for: **no AL that reaches it ever
executes.**

### The condition-shape site

Exactly two syntax shapes reach the `default:` arm of `RelationConditionList`, and both are
already compiler errors:

| shape | why it reaches the default | what the compiler says |
|---|---|---|
| `SimpleFieldExpressionSyntax` in an `if()` | `allowFieldLinks: false` — BC models an `if()` as `MetaCondition`, and `NCLMetaFilter.CreateFromMetaCondition` has CONST and FILTER cases only | `error AL0489: The property expression is not valid. A CONST or FILTER expression is expected.` |
| `InvalidPropertyExpressionSyntax` | it is the AL parser's error-recovery node | whatever syntax error produced it (`AL0104`, `AL0292`, …) |

Measured against the runner, which drives Microsoft's own compiler:

- A bundle whose table carries `TableRelation = if ("Kind" = field("Kind")) "Parent";` reports
  `AL0489` and runs **0 tests** (`COMPILE FAIL`). The refusal site *is* reached during that
  failed compile — confirmed with both sites instrumented to print unconditionally — but no AL
  runs, so the silent wrong answer is unobservable. (That is the SOURCE path. The same text
  arriving from a precompiled `.app` is not a compile error and must not throw — see the
  provenance table above.)
- The same table as a **dependency** app is refused before it can be used:
  `source dependency 'DepMix Dep' does not compile (1 error(s)): AL0489`.
- Every condition shape Microsoft's binder accepts is carried, with no refusal:
  `const(...)` and `filter(...)` in both positions, and `field(...)`, `field(filter(...))`,
  `field(upperlimit(...))`, `field(upperlimit(filter(...)))` in `where()`. A bundle declaring
  all of them passes `1P/0F/0E`.

### The related-table-name site

`parts.Count is not (1 or 2)` cannot fire, and that is structural:

- `RelationTargetNameParts` returns `parts.Count <= 2 ? parts : parts.GetRange(parts.Count - 2, 2)`,
  so its result is 0, 1 or 2 — never more. The "3-part name" the old message named has been
  impossible since #2851.
- 0 is unproducible. `NameSyntax` has exactly **three** subclasses in
  `Microsoft.Dynamics.Nav.CodeAnalysis` 28.1 — `SimpleNameSyntax` (abstract),
  `IdentifierNameSyntax` and `QualifiedNameSyntax` — and `NameParts` walks both concrete ones,
  appending at least one part for each. AL's error recovery synthesises a *missing*
  `IdentifierNameSyntax` rather than nothing, so even unparseable input yields 1 part.

Thirty-three probes, each parsed with the AL compiler's own parser, all returned 1 or 2 parts —
fifteen written as AL source, and eighteen more feeding the arbitrary-garbage strings the SYMBOL
path can carry (`""`, `";"`, `"))"`, `"@#$%"`, `"1 = 2"`, an unterminated quote, bare keywords).
This is why the name-parts guard throws unconditionally, with no provenance branch: it is
unreachable on both paths.

| input | parts |
|---|---|
| `Microsoft.Sales.Customer."No."` | 2 (clamped) |
| `A.B.C.D`, `A.B.C.D.E` | 2 (clamped) |
| `"Probe.Tbl"` (quoted name containing a dot) | 1 |
| `""` (empty quoted name) | 1, empty text |
| `.Customer`, `Customer.`, `.` | 2 |
| `18`, `(Customer)`, `'Customer'`, `if`, whitespace, absent | 1, empty text |

An empty target *name* is therefore carried as one part and refused by the **builder**, which is
#3306's business — the parser never had that case to drop.

## Corpus sweep

With both sites instrumented to print unconditionally:

| suite | tests | hits |
|---|---|---|
| `tests/al-language/tests/al-language` | 2978 | 0 |
| `tests/runner-extras` | 361 | 0 |
| `tests/al-language/tests/al-language-internals-fixture` | 0 | 0 |

## The sibling this did not cover

`CalcFormulaFrom`'s `where()` `default:` arm (same file) has the identical shape — it prints
`[CalcFormula] REFUSED ...` and returns `null`, and the CalcFormula note sink from #3279 is in
the builder, so a parser-level drop is invisible there too. Not folded in: it is a different
property with a different set of reaching shapes, and it needs its own reachability measurement
before anyone converts it, and the provenance split above is the trap it has to avoid. Filed as
**#3367**.

## Cold symbol-read cost of the text parse (#4107)

A precompiled field's `TableRelation` reaches the parser as property text from
`SymbolReference.json`. `BcAppSymbolCache` passes it to `RecordPatches.TryParseRelationArmsText`,
which wraps it in a probe table and parses it with BC's parser. That work happens only when the
`bc-symbols` cache misses. A warm read skips it.

**Verdict: too small to change.** On the Base Application the relation parse costs at most
about 2.5 G instructions, measured on its own including parser JIT. That is about 8% of the
30.5 G a whole cold read of the same app costs. It was too small to see
inside a whole cold `BcAppSymbolCache.Parse`, even with instruction counts. The keyed tree cache from #2588 already
removes most repeat parses.

Measured on runner `775e3d02` (Release build), Base Application `28.1.49838.53910`, on a
12-core box under load average 13 to 23. Unless a row says otherwise, it is five fresh processes, each doing one pass, and the table
gives the median.

| what | tree builds (`ParseObjectTextCallCount`) | instructions (user) | wall |
|---|---|---|---|
| Normal-class fields carrying a `TableRelation` | 7,583 fields, 1,113 distinct texts | | |
| all 7,583 texts through `TryParseRelationArmsText`, cold process | **1,113** | **2.5 G** over an empty-process baseline (paired: 2.83, 2.92, 2.52, 1.35, 2.11) | 387 ms (319 to 654) |
| the same, `AL_RUNNER_PARSE_TREE_CACHE_BYTES=0` (3 runs) | 6,715 | 6.8 G (paired: 7.19, 6.83, 4.93) | 712 ms (442 to 1382) |
| the same texts again in the same process (served by the in-process keyed tree cache, not a second cold read) | 0 | not measured | 38 ms |
| whole cold `BcAppSymbolCache.Parse` of Base Application | 2,668 | 30.5 G (whole process) | 2,151 ms |
| the same with the relation parse returning `null` | 1,555 | 29.8 G | 1,973 ms |

Read the rows this way:

- **The count is the load-independent number.** Skipping the relation parse removes exactly
  1,113 tree builds, which equals the number of distinct relation texts. So the keyed tree
  cache already de-duplicates by text, and adding another memo here would save nothing.
- **Inside the real cold read, the difference cannot be separated from noise.** The paired
  instruction deltas for the last two rows were -0.35, 0.14, 0.03, 6.55 and 1.57 G, on about
  30 G per process. The isolated row is the upper bound. It includes JIT-compiling BC's parser.
  A likely reason the real read shows less is that its `CalcFormula` parse has already paid
  that JIT, but that was not measured.
- **The cost is paid once per cache root and app content hash.** A warm read is a `bc-symbols`
  cache HIT and never reaches the parser. The honest scale is the whole cold read of the same
  app, measured above at about 2.2 s.
- **#4094 adds little.** It extends the parse to FlowFilter and FlowField relations: 204 fields
  and 78 distinct texts, 17 of which are new. That is 17 more tree builds.

**Trap: the de-duplication depends on the keyed cache's budget.** If
`AL_RUNNER_PARSE_TREE_CACHE_BYTES` is set to 0, the builds go from 1,113 to 6,715. Only the
single-slot memo remains, and it catches adjacent repeats only. The default 8 MiB budget
holds all of these texts (mean 140 characters). A change that shrinks the budget, or keys the
cache on something other than the text, should re-run the count.

How it was measured: a throwaway xunit test, not committed. It called the private
`BcAppSymbolCache.Parse` by reflection, and in a second mode it fed `TryParseRelationArmsText`
the relation texts read out of the `.app`'s nested `SymbolReference.json`. It read
`ParseObjectTextCallCount` before and after, and ran each process under
`perf stat -e instructions:u`. The `null` variant came from a local, uncommitted early return
in `TryParseRelationArmsText`.
