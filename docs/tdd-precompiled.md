# `--tdd` and a codeunit that arrives as a package (#5037)

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

## Not covered (reported FAILED as before)

A field of a package's table, an enum value of a package's enum, an overload of a package's procedure, an
object no app declares, and a call through anything but a plain variable. The first three have the same
shape (a new object beside it: a table extension, an enum extension); they are tracked from #5037.

Tests: `TddPrecompiledTests` (a DLL package and a source package, end to end), `TddPrecompiledStubTests`.
