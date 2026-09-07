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
