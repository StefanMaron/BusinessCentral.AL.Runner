# `--tdd` and a codeunit that no app declares (#5431)

`al-runner --tdd test`, where a test names a codeunit that exists in no app of the run:

```al
[Test] procedure CallsAnObjectNoAppDeclares()
var Missing: Codeunit "No Such Codeunit"; Result: Integer;
begin Result := Missing.Calc(1); end;
```

Before this the test was reported FAILED with `error AL0185: Codeunit 'No Such Codeunit' is missing` at the
variable and the run closed with `--tdd: no members were generated`. The compile never got as far as `Calc`:
AL0185 is a declaration error, so the emit throws instead of reporting the call.

## What happens now

An EMPTY codeunit of that name is appended, in memory, to the first file (file order) that names it, with the
first id of the test app's `idRanges` that no codeunit of the compile uses. The compile runs again; now `Calc`
is an ordinary AL0132 against an object of the app, and the existing generation (`TddGeneration.cs`) adds the
procedure from the call site as it does for any object of the source it compiles. Nothing is written to disk
and no existing object is touched.

```
--tdd: generated codeunit "No Such Codeunit" (id 65326) in A1ObjectTests.Codeunit.al: no app of the run declares it
```

Each test that reaches the object is annotated like any generated member (`generatedStubs`, the
`reaches generated stub(s)` line, the closing list): `No Such Codeunit: codeunit 65326` for the object, plus
`No Such Codeunit: procedure "Calc"(Arg1: Integer): Integer` for each procedure it calls. A test that only
declares the variable, or passes it on, names the object alone. "Reaches" is the static call graph of
`TddCallGraph` from the type reference, and for a variable declared outside any procedure (a global of the test
codeunit) from every mention of its name in the object.

## Refused, and the run says why (`--tdd: codeunit "X" not generated - ...`)

An empty codeunit is wrong whenever the real one exists but is out of reach, because the test would then pass
against nothing. Refused: a codeunit of that name some module or namespace declares
(`Compilation.GetApplicationObjectTypeSymbolsByNameAcrossModulesAndNamespaces`); a name another bundle of the
run declares (`TddCrossBundle.RunDeclaresCodeunit`, a text probe of every bundle: this is the app that forgot to
declare its dependency on a sibling folder); a name written as an id or with a namespace; no free id in
`idRanges` (no manifest, or the range is full). The test is FAILED with the AL0185 as before.

Measured: a codeunit in another namespace is NOT AL0185 for a file in the global namespace (it resolves), so the
namespace case needs a file with its own `namespace` line (the fixture's); a sibling bundle without a declared
dependency is AL0185.

## Design points a later edit gets wrong

- **One object per name, so one file.** AL object names are unique, so the per-file stub of
  `tdd-precompiled.md` (distinct names) is impossible here. The object sits in the first file that names it;
  another file naming it is not touched. If that file is dropped (a call `--tdd` refuses elsewhere in it, or any
  other compile error), the object goes with it and every other file naming it is dropped too, reported FAILED
  with the emitter's own message rather than the AL0185. Loud, never a silent pass, but one refused call in the
  first file costs the others. Measured, not fixed here (#5447).
- **Order.** The diagnostics, the sites and the ids follow no feed order: names are taken in ordinal order, sites
  by file then position, and `TddGeneration.Generate` now sorts the diagnostics it reads by file, position and id
  before inferring anything (the shape of a member is the first call that fixes it, #5244). That also fixes the
  same order-dependence for a procedure of an object that is declared. `AL_RUNNER_TDD_DIAG_ORDER=reverse`
  (the seam of `tdd-precompiled.md`) feeds them backwards first.
- **Two phases, because AL0185 hides everything else.** The first compile throws, so there are no AL0132s to read;
  the object is added from the declaration diagnostics (`GetDeclarationDiagnostics`), the compile is repeated, and
  the AL0132s of the repeat are generated as usual (`BcCompiler`, `TddRecompile`). A second AL0185 this does not
  generate (a `Record "X"`, a page, an enum, a refused name) in ANY file of the same compile makes the repeat throw
  again, so the members of the objects that were generated are not added either. The objects are still reported (#5447).
- `Run` on the new object is the codeunit's own `Run`, never a generated procedure.

## Not covered

A table, page, enum, report, query or xmlport that no app declares; a codeunit named only by `Codeunit::"X"`
with no variable of that type. The other kinds are #5445.

**An object of a package the app does not declare as a dependency is NOT detected, and is shadowed.** Measured
(a package in `.alpackages` carrying `Precompiled Points`, the test app declaring no dependency on it): AL0185,
an empty codeunit of that name is generated, so a test whose calls can be generated would run against it. It is
annotated (`generatedStubs`), but "no app of the run declares it" is false there. Nothing is read of the packages
that the app's declarations leave out, so the generation cannot tell; #5446.
