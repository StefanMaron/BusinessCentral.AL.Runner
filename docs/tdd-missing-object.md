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
--tdd: generated codeunit "No Such Codeunit" (id 65326) in A1ObjectTests.Codeunit.al: no app of the run and no package it can read declares it
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
declare its dependency on a sibling folder); a name any package of the run's package folders declares, the
dependency the compile leaves out because `app.json` does not list it (#5446, below); a name written as an id or with a namespace; no free id in
`idRanges` (no manifest, or the range is full). The test is FAILED with the AL0185 as before.

### A package the app does not depend on (#5446)

The compile resolves only the packages the app declares (`BcCompiler.NarrowToDeclaredReferences`), so a codeunit
of a package in `.alpackages` or the package cache that `app.json` leaves out is AL0185 exactly like one nobody
declares, and `GetApplicationObjectTypeSymbolsByNameAcrossModulesAndNamespaces` cannot see it. An empty codeunit
added for it would shadow the real one: a test whose calls can be generated passes against nothing. So the
generation asks the packages the latest scan found (`BcCompiler.ScannedPackagesForTdd`, every .app with symbols,
declared or not, minus the app being compiled), through `BcAppSymbolCache.Get` (the symbol cache the virtual
tables read), whether any declares a codeunit of the name, in any namespace and any case:

```
--tdd: codeunit "Package Only Points" not generated - the package Tdd Package Only 1.0.0.0 (Tdd_Package_Only_1.0.0.0.app) declares it - if the test means that codeunit, add the dependency on it to app.json; if it means a new one, give it another name; an empty codeunit would shadow it
```

The test stays FAILED with the AL0185. The package named is the first in name, version, path order, never the first
scanned. The scan covers every package of the folders, declared or not (the whole platform-app set on a standard box),
so a NEW codeunit whose name happens to match one of theirs is refused too: hence the two readings in the message.
Only a codeunit counts: a package that declares a TABLE (or any other kind) of the name does not block the codeunit.
Trap: this reads every package of the folders on a cold symbol cache, so it runs only for a name that is AL0185 and
survived the cheaper checks above, and it runs before the id is reserved, so a refused name never uses up an id.

#### A package that cannot be read (#5450)

An unreadable package may be the object's home, and "unreadable" is not "absent". But the compile tolerates such a
file (a valid manifest, a malformed `SymbolReference.json`: only the AL0185 for the missing codeunit, no AL1023), and
refusing on it for every name would turn the whole generation off for the run. So the refusal depends on the NAME: the
package's `SymbolReference.json` text (every module of it: the outer .app and the one nested in it) is searched,
ignoring case, after its JSON escapes are decoded: `\uXXXX` in either case and `\/ \" \\ \' \n \r \t \b \f`.

- the name is not in the text: the package cannot declare it. It is skipped, and the run says so on one line:
  `--tdd: the package Broken Unrelated 1.0.0.0 (Broken_Unrelated_1.0.0.0.app) could not be read (JsonReaderException: ...), but its symbols text never mentions "X", so it cannot declare it and was skipped; remove or replace that file`
- the name is in the text, or the text cannot prove an absence: refuses, naming the package and the remedy
  (`... could not be read (...), so it cannot be ruled out as the home of the codeunit; remove or replace that file`).
  The text cannot prove an absence when it holds a backslash sequence outside the decoded set (`\a`, `\x41`, an octal
  `\101`, a short `\u12`, a lone trailing backslash), when the name holds a backslash, quote or control character,
  when the text holds a NUL (UTF-16 without a byte-order mark reads as interleaved NULs), and when the file cannot be
  opened at all.

The rule is "an escape the search does not know refuses", not "any backslash refuses": BC's own writer emits the
decoded set in captions and doc comments, so an unrelated package with those and a trailing comma is still skipped.
The set is closed on purpose: BC reads symbols with Newtonsoft, which accepts more than the reader that refused the
file (it takes `\'` and a trailing comma), so a spelling nobody decoded must not read as an absence.

Trap: a wrong "absent" is a shadow again, so every doubt refuses; widening what the search proves absent needs a test
per new spelling.

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
