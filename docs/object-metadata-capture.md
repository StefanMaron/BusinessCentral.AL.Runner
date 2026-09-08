# Capturing BC's own object metadata

BC's `Compilation.Emit` hands the runner's `CodeModuleOutputter` a metadata document for
every application object it emits — the same XML the service tier stores in Application
Object Metadata at publish time. `AlObjectMetadataRegistry`
(`AlRunner/Patches/ObjectMetadataRegistry.cs`) keeps all of them, keyed by (object kind,
object id).

Three older registries keep one kind each and are unchanged: `AlReportMetadataRegistry`,
`AlPageMetadataRegistry`, `AlXmlPortMetadataRegistry`. The general registry overlaps them
on purpose — it is additive, and nothing reads it yet (issue #3548, step 2). Converting a
consumer to read from it is a separate change, per kind, with its own RED → GREEN.

<a id="which-kinds-arrive"></a>

## Which kinds reach `AddApplicationObject`

Measured on the `ObjectMetadataCapture` fixture (`AlRunner.Tests/Fixtures/`), BC 28.1,
`BCCOMPILER_TRACE=1`. Twelve kinds arrive with a non-empty metadata document:

| kind (`SymbolKind`) | | kind (`SymbolKind`) | |
|---|---|---|---|
| `Table` | ✓ | `Enum` | ✓ |
| `Page` | ✓ | `EnumExtension` | ✓ |
| `Codeunit` | ✓ | `PermissionSet` | ✓ |
| `Report` | ✓ | `PermissionSetExtension` | ✓ |
| `XmlPort` | ✓ | `TableExtension` | ✓ |
| `Query` | ✓ | `PageExtension` | ✓ |

Two things that a list of kinds written by hand would have got wrong:

- **`Query` is not in issue #3548's own table of twelve** — that table was measured from
  runtime packages of twelve ISV apps, none of which declares a query. It arrives here
  (1,489 characters on the fixture's one query). This is why the capture keys off "the
  metadata string is non-empty" rather than off a list of kinds.
- **`Interface` is in that table and does NOT arrive.** A runtime package ships `INT`
  documents; this outputter never sees them. Re-measured with an implementing codeunit
  present, in case an unimplemented interface were being elided — still absent. The same
  goes for `Profile`: a runtime package ships a `PROFILE` document, and a profile declared
  in a scratch copy of the fixture produced no `AddApplicationObject` call. The checked-in
  fixture declares no profile, so that half is a measurement made outside the tree rather
  than something this repository pins -- add a profile and a role-center page to the fixture
  if you want a future BC that starts emitting `PROFILE` to show up as a new trace line.

So this path covers eleven of the issue's twelve kinds plus one it did not know about.
Interface and profile metadata are only available from a runtime package (#3537) or from
`SymbolReference.json` (#3533, #3545).

Every kind that does arrive is id-bearing, and ids repeat across kinds — the fixture
deliberately declares a table, a page, a codeunit, an enum, a report, an xmlport and a
query all numbered 70660. A registry keyed on the id alone answers seven objects with one
document. The registry still accepts an id-less registration and keys it by name, so a
kind BC starts delivering later is stored rather than dropped.

<a id="surviving-a-warm-run"></a>

## Surviving a warm run

`Emit` runs only on a compile-cache **MISS**. Anything captured there and not persisted is
gone on the next run, silently: the registry is simply empty and every consumer takes its
not-found branch while the run stays green. `AlRunner/Patches/RecordPatches.AlObjectDeclParser.cs`
exists because of exactly that, and its header names this hazard as the reason it parses AL
source text instead.

Three mechanisms already solve it for the page and report registries, and the general
registry uses the same three rather than inventing a fourth.

| path | what runs | where |
|---|---|---|
| bundle AL-output cache HIT | `ProgramSupport.LoadEnumRegistrySidecar` replays the `objectMetadata` array out of `<key>.enum-registry.json` (one file, not one per registry) | `AlRunner/ProgramSupport/Suites.cs`, called from `AlRunner/Program.cs` |
| source-dependency compile cache HIT | `AlObjectMetadataRegistry.LoadSidecar` replays `<key>.object-metadata.json`; written by `PublishSourceDependencyCache`, scoped to the keys that dependency's own emit added | `AlRunner/DependencyLoader.cs` |
| `--watch` / `--server` RAD fast path | `BcRuntime.ResetForNewBundleReload` clears the registry each cycle; the per-module shadow snapshot survives the reset and is replayed on every fast-path return | `AlRunner/BcCompiler.Incremental.cs` |

The first two are pinned by `AlRunner.Tests/ObjectMetadataCaptureTests.cs`; the RAD one by
`AlRunner.Tests/RadObjectMetadataSnapshotReloadTests.cs`, which drives the two calls `--watch`
drives (`ResetForNewBundleReload`, then `TryEmitIncremental`) and reads the registry back per
(kind, id). It covers the destructive direction too — an object whose file is deleted between
cycles must stop being answerable, and one whose source changed must answer the new document
rather than the snapshot's.

Consequences worth knowing before changing any of this:

- The bundle sidecar's shape is part of the AL-output cache key (`schema:v14`,
  `AlRunner/ProgramSupport/Dependencies.cs`). A cache entry written under an older schema
  carries no `objectMetadata` array, and serving it would produce an empty registry on warm
  runs only.
- The dependency sidecar is scoped by identity key, not by id, because ids repeat across
  kinds. Scoping is what stops one dependency's sidecar carrying a sibling app's entries.
- `CaptureRadMetadataSnapshotFull` reads the process-wide registry, which is correct on the
  `Emit` call site and is the known asymmetry issue **#3282** describes for the
  page/xmlport snapshots on the `EmitDepSymbols(trackIncrementalBaseline: true)` one. The
  object snapshot is deliberately consistent with its two neighbours rather than diverging;
  #3282's fix should cover all three together.
- Report and report-layout metadata have no RAD shadow snapshot at all — issue **#2655**.

## Seeing what BC actually said

```
AL_RUNNER_TRACE_OBJECT_METADATA=1   # one line per registered entry: kind, id, name, length
AL_RUNNER_TRACE_OBJECT_METADATA=2   # ...and the document itself
```

The trace comes from `Register`, which is the single funnel both the emit path and all
three replay paths go through, so the same lines appear on a cold run and on a warm one.
That is what `AlRunner.Tests/ObjectMetadataCaptureTests.cs` asserts, twice, against one
cache directory.
