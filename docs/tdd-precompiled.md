# `--tdd` and an object that arrives as a package (#5037, #5430)

`al-runner --tdd test`, where the tests call a procedure the implementing codeunit does not declare yet and
that codeunit is not a source folder of the run but a package: a `.app` in the test folder's
`.alpackages`, carrying a precompiled DLL (`.deps-bin`, Tier 1), its own source (the run compiles it,
Tier 3) or only symbols. Before this, each test of the object that named the procedure was reported FAILED
with `AL0132` and the run closed with `--tdd: no members were generated`.

## What happens now

The missing procedure is generated **beside** the object, in a new codeunit of the test's own compile, and
every call to it that goes through a variable is pointed at that codeunit. Nothing of the package is
read for it and nothing of the package is changed; nothing is written to disk.

```al
// the test, as written                       // the test, as compiled (in memory)
var Points: Codeunit "Precompiled Points";    var Points: Codeunit "Precompiled Points";
                                                  TddStub65321: Codeunit "TDD Stub 65321";
Result := Points.CalcPoints(250);             Result := TddStub65321.CalcPoints(250);
Result := Points.Twice(4);                    Result := Points.Twice(4);        // still the real body
                                              codeunit 65321 "TDD Stub 65321" { procedure "CalcPoints"(Arg1: Integer): Integer begin end; }
```

The signature is inferred from the call site exactly as for a source object (`TddGeneration.cs`: argument
types, the return type from how the result is used; a Text argument, a statement call and the like are
refused). The body is empty, so the call returns its type's default and the test's own assertion is the
red of the loop. Each test that reaches the stub is annotated like any generated member (`generatedStubs`,
the `reaches generated stub(s)` line, the closing list): a test never passes against a stub unannotated.

## Why beside, and not a shadow copy of the object

`precompiled-dll-respect.md` forbids changing a precompiled business-logic assembly's existing members.
Two designs stay inside it; this is the second.

- **A shadow copy of the object, with the stub added.** A package's symbols give every signature but not
  a body. A copy compiled from them would have empty bodies for every existing member, and a test that calls
  `Twice` would then run the shadow's empty `Twice`, not the real one: the existing members' behavior changes.
  It only works when the package ships its source, which most do not.
- **The call site, in the test's own compile (chosen).** A new codeunit (new objects are allowed) holds the
  stub; only the calls the compiler named as missing are rewritten. The same mechanism serves every shape of
  package, because it reads none of its code: a DLL, embedded source, symbols only.

## Design points a later edit gets wrong

- **One stub per precompiled object per FILE** (`TddPrecompiledStub.Group`). A file the compile drops (a
  call `--tdd` refuses elsewhere in it) takes its stub with it; a call site in another file pointing at that
  stub would then crash the emitter. Pinned by `TddPrecompiledTests` (the refused file sorts first).
- **A member is stubbed only when every call to it can be pointed at the stub.** A call through anything but
  a plain variable (an array element: the rewrite would skip evaluating the index) or outside a codeunit
  refuses the member, so no call is left failing while another passes against a stub.
- **The stub codeunit takes the first id of the test app's `idRanges` that no codeunit of the compile
  uses** (`app.json`); with no manifest or no free id the procedure is refused, never given a guessed id.
  That keeps the stub inside the range the app may allocate from, so it cannot take another app's id.
- **Edits are text edits computed against the trees as parsed**, applied back to front, and the trees
  re-parsed (`TddPrecompiledStub.Apply`). They run before the other generation of the same pass mutates a tree.
- **Only AL0132 (a missing procedure).** AL0126 (an existing procedure called with an argument count none of
  its overloads takes) is not taken: `GenerateBesidePrecompiled` filters on the diagnostic id.
- **Nothing follows the order the compiler reports the missing calls in**, because it changes between runs.
  A member called two ways (an assignment fixes its types, an expression does not) takes its shape from the
  first call, in file then position order, that fixes it; members and the stubs' ids go in key order.
  Two calls to one member that disagree on a shape are decided by file, then position, never by position
  alone. `AL_RUNNER_TDD_DIAG_ORDER=reverse` feeds the diagnostics backwards (a test seam used by
  `TddPrecompiledTests`) and the outcome must not change. One stderr line per stub codeunit names the id it
  took (`--tdd: stub codeunit "TDD Stub N" beside precompiled X in F: "M"`), so the id assignment is pinned.
- **A refusal says why**, once per member, on stderr (`--tdd: not generated beside precompiled X: "M" - ...`):
  a call that is not through a plain variable, no call that fixes the types, no free id in `idRanges`, or
  the exception that stopped it. The tests are still reported FAILED naming the missing symbol.
- The stub is a different instance from the variable it replaces. It has no state and an empty body, so no
  test can tell, but a stub that later gets a body would have to.

## A typo cannot be told from a missing member

A stub for a member that does not exist anywhere is exactly what a typo looks like: `Twicee(1)` on a package
codeunit that declares `Twice` is stubbed, and a test that assigns the result and never checks it passes.
On a source codeunit that was already so (the deliberate empty-body-returns-default design, #5147); this
extends it to objects the developer does not own, including Microsoft's. The run is annotated (`generatedStubs`,
the `reaches generated stub(s)` line, the closing list), so it is never silent, but a test that reaches a stub
should assert its result: that assertion is what fails (`Expected 25, got 0`). Whether to refuse or
distinctly label a stub of a Microsoft-published object is #5437.

## A field of a package's table, a value of a package's enum (#5430)

```al
[Test] procedure NewField() var E: Record "Loyalty Entry"; begin E."Points" := 5; end;
[Test] procedure NewValue() var T: Enum "Loyalty Tier"; begin T := "Loyalty Tier"::Gold; end;
```

where "Loyalty Entry" and "Loyalty Tier" are objects of a package that do not declare `Points` and `Gold`. Before this each test
was FAILED with `AL0132` and the run closed with `--tdd: no members were generated`. The same rule as above: new objects that
exist for this run only, nothing of the package changed, nothing written to disk. The member goes in a `tableextension`
or an `enumextension` appended to the test's own file:

```al
tableextension 65401 "TDD Table Extension 65401" extends "Loyalty Entry" { fields { field(65402; "Points"; Integer) { } } }
enumextension 65402 "TDD Enum Extension 65402" extends "Loyalty Tier" { value(65402; "Gold") { } }
```

One stderr line per extension names the ids it took (`--tdd: table extension "TDD Table Extension 65401" (id 65401) beside package
table Loyalty Entry in A1FieldTests.Codeunit.al: "Points" (field 65402)`), and a test that reaches the member is annotated like any
other (`Loyalty Entry: field "Points": Integer`, `Loyalty Tier: enum value "Gold" = 65402`). Measured on both package shapes
(a DLL with symbols, and symbols with the app's source): the test writes the field, inserts, reads the row back through another
record and gets the value; the enum value formats as its name and differs from the package's own.

What differs from the procedure case, and what a later edit gets wrong:

- **One extension per table or enum, in one file, not one per file.** A procedure stub is reached by rewriting each call site, so it
  can sit per file. An extension changes the table itself, and two files adding the same field clash, so it goes in the FIRST file
  (file order) naming a missing member of that table, holding every such member, and the other files compile against it. If that
  file is dropped (a call `--tdd` refuses in it), the other files naming the field fail with the AL0132 again: loud, the
  same trade as a generated object (`tdd-missing-object.md`, #5447).
- **A field is typed only by an assignment** `Rec.Field := <expression>`, from the first site (file, then position) that has one; a field
  that is only read, or assigned a Text (no length to infer), is refused, and the run says so
  (`--tdd: not generated beside package table X: "F" - no assignment ...`). A site of the same field in another file that assigns
  another type no longer compiles and is FAILED.
- **Ids come from the test app's `idRanges`**, as for a generated object: the extension's id is the first free for ITS object type
  (a codeunit or table with the number does not take it, `TddPrecompiledStub.FreeTableExtensionId`), a field id or an enum ordinal the
  first of the range that the table or enum does not use (its symbol), nor a tableextension or enumextension in the test app's own files does. A value is therefore NOT
  numbered after the enum's highest ordinal as for an enum of the app, and an `AsInteger()` assertion on it proves nothing. Tables and enums
  are taken in name order and fields and values in name order, whichever order the compile reported them in. The runner's compiler
  also accepts a field id or an ordinal outside the range (measured); the range is used because it is the one the app may allocate
  from. Whether BC enforces that was not measured: this is a runner-only development tool, and no claim about BC rests on it.
- **Refused when the package does not let anyone extend the object.** `IsExtensible` of the table or enum symbol is read first:
  an extension of an enum without `Extensible = true` (AL0504) or of a table with `Extensible = false` (AL0564) does not compile (measured), and
  would take its file down with it, so it is refused with the object named and the tests are FAILED on the original AL0132. No free extension id, field id
  or ordinal in the ranges refuses the same way.
- **The extended object is named with its namespace.** Two packages may each declare a table of one name in different namespaces
  (one app cannot: AL0197 whatever the namespaces). With both packages in the compile, an unqualified `extends "X"` was measured to be
  `AL0275: 'X' is an ambiguous reference`, which drops the file the extension sits in; qualified, it binds to the one meant. The message
  and the annotation carry the namespace too (`Loyalty.Data.Namespaced Entry`).
- **The record engine is told of a table extension only after the compile settled, and only if its file survived**
  (`TddGeneration.SettlePackageExtensions`, then `RecordPatches.TddRegisterTableExtension`, the call a generated field of a source table makes
  through `TddReparseAndRefreshTable`). Without it the field compiles and the first access throws `extension field N from extension M not
  found in NCLMetaTable` (measured, both package shapes). An enum is compiled from the trees and needs nothing.
  If the home file is dropped (it also names a field that cannot be typed, say), nothing is registered, the members are taken out of the
  generated list and the run says `--tdd: dropped with its file: ...`: otherwise the record engine kept a field nobody declares, visible to
  a test in a file that did compile (`RecordRef.FieldCount`).
- **A compile that registered an extension records no incremental baseline.** The registration is state the trees do not reproduce: the
  `--watch` reset clears it, and an incremental cycle reuses the previous baseline (extension appended, nothing regenerated), so the field
  failed with the `not found in NCLMetaTable` above. So the next `--watch` cycle is a full rebuild, which generates and registers again; the
  reason is printed (`FULL REBUILD ... generated a field of a package's table in a tableextension`). Same gate as an excluded object
  (`BcCompiler.Emit`). Not run: `--server` (it resets once per request, by reading).

## Not covered (reported FAILED as before)

An overload of a package's procedure, a call through anything but a plain variable, a field that is only read, a table or
enum the package does not let anyone extend, and a field id or ordinal that another app's extension of the same table or enum already
holds (the choice does not see it: a loud AL0206 / AL0231 that drops the file, #5491). An object no app declares at all is
[`tdd-missing-object.md`](tdd-missing-object.md).

Tests: `TddPrecompiledTests` (a DLL package and a source package, end to end), `TddPrecompiledStubTests`, `TddPackageMemberTests` (a field and a
value, a DLL package beside a source package, a twin table in another namespace, the refusals).
