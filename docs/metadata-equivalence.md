# Metadata equivalence: proving the SymbolReference derivation equals BC's emitter

The runner gets a precompiled app's table metadata by reading its `SymbolReference.json` and
building a `MetaTable` by hand — about 3,800 lines across `BcAppSymbolCache.cs`,
`BcAppSymbolCache.TableExtensions.cs` and `RecordPatches.BcAppFallback.cs`. That derivation is
acceptable only if it produces the same objects BC's own metadata emitter produces for the same
app, and the proof has to be total rather than a list of properties someone thought to check.

Issue #3533 sets that standard. This document is how it is enforced.

<a id="the-two-sides"></a>
## The two sides, and why they are the same type

```
ground truth   MetaTable.CreateMetaTableFromXml(<BC's own emitted document>)
the runner     RecordPatches.GetOrBuildNCLMetaTable(id).GetMetaTableOriginal()
```

The runner builds a `Microsoft.Dynamics.Nav.Types.Metadata.MetaTable` and hands it to
`NCLMetaTable.CreateFromMetaTable`; `GetMetaTableOriginal()` hands that same instance back. So
both sides are one type, and the comparison is member-for-member rather than a translation
between two shapes.

`AlRunner/Metadata/MetadataObjectDiff.cs` walks every readable instance member found by
reflection: public and non-public properties, plus non-public fields that are not auto-property
backing storage (`MetaTable.fieldsById` is real state reachable only as a field). It recurses
into nested BC metadata objects, pairs `Fields` by id and everything else by position, pairs
dictionary entries by key, and terminates on a pair it has already compared.

There is no member allowlist in the differ. What is known-different is declared once, with a
reason, in `tests/expectations/metadata-equivalence/allowlist.json`.

<a id="where-the-ground-truth-lives"></a>
## Where the ground truth lives, and why it is not in the repository

`tools/metadata-ground-truth` compiles a Microsoft app's shipped AL source with BC's own
compiler and keeps every document the emitter produced. `tools/gen-metadata-ground-truth.sh`
runs it for the apps `tests/expectations/metadata-equivalence/apps.json` declares, into
`<artifacts-root>/../metadata-ground-truth/<bc-build>/<publisher>_<app>_<version>/`.

Measured on BC 28.1.49838.54044: Business Foundation 3.2 s and 70 documents, System Application
15.2 s and 1,218 documents. Gzipped the two bundles are 35 KB and 656 KB.

Small enough to check in, and it is deliberately not checked in:

- It is **Microsoft's compiler output over Microsoft's source.** Everything else the runner
  consumes from Microsoft is provisioned by the user from Microsoft; redistributing this in a
  public repository is a licensing decision, not a technical one.
- It belongs to **one exact BC build.** A checked-in copy would either churn on every Microsoft
  release or go stale silently — and going stale silently is the failure mode this whole harness
  exists to remove.
- Regenerating is cheap and needs nothing the box does not already have. CI regenerates it in
  the `Generate BC metadata ground truth` step of `bc-tests.yml`, on the two unit-test legs.

What IS checked in is the part that carries judgement: the app list, the allowlist, the
generator and the differ.

One consequence worth stating plainly: because the bundle is generated on the same box from the
same artifacts, the `.app` the runner reads and the emitter output it is compared against always
come from one build. There is no staleness question to get wrong.

The harness enforces that rather than assuming it: it reads only
`<ground-truth-root>/<the build this process loaded>/`, and it finds each bundle's `.app` inside
that build's own artifact directory. A dev box accumulates a directory per build, every one of
them holding a System Application bundle with the same table ids, and registering two of those
packages into `RecordPatches`' process-global state makes the second id lose to the first — one
app's metadata measured against another app's symbols, silently. CI has one build, so this would
never have surfaced there. When no bundle exists for the loaded build the skip message names the
exact `--artifacts` to pass, because the generator's own default is the NEWEST build and the test
host loads whichever build the runner was compiled against.

<a id="the-allowlist"></a>
## The allowlist

`tests/expectations/metadata-equivalence/allowlist.json` is the only thing standing between "a
difference someone has decided about" and "a difference nobody has looked at". Three properties
make it that:

| | |
|---|---|
| a difference not listed | fails |
| an entry that matches nothing | fails — so a landed fix must delete its entries |
| an entry exceeding its `maxOccurrences` | fails — so a declared defect getting worse still fails |

Every entry states a reason **and exactly one kind of reason**, because the four kinds need
different evidence and conflating them is how a recoverable difference gets a permanent licence:

| kind | means | needs |
|---|---|---|
| `issue: N` | a defect, tracked, expected to go away | the issue |
| `outOfScope: true` | the runner does not implement the surface, so no AL test can observe it | a `Doc` pointer |
| `oracleLimitation: true` | the ground truth is the wrong shape for this member | a `Doc` pointer |
| `cannotExpress: true` | the symbol file cannot express it **and** it cannot be derived | evidence about derivability |

Enforced at load by `MetadataDifferenceAllowlist`, so a malformed entry fails the run rather than
sitting in the file granting cover nobody reviewed. `Doc` is spelled that way on purpose:
`tools/test_doc_pointers.py` already validates every `Doc` value under `tests/expectations`,
path and anchor, so a scope claim cannot point at a section that has been renamed away.

**No entry currently claims `cannotExpress`**, and the way that changed is the reason the
distinction exists. The seven `TranslationKey` entries — 25,721 of the 70,728 differences, 36% of
the diff — were first declared a permanent limit of the symbol file, on the evidence that
`SymbolReference.json` contains the string `TranslationKey` zero times in both packages. That
observation is true. The conclusion drawn from it was wrong twice over, and both corrections are
below. **"Nothing is stored" is evidence about storage, never about derivability.**

<a id="translation-keys-are-out-of-scope"></a>
## Translation keys are out of scope, and separately are derivable

A `CaptionTranslationKey` is a lookup id into a translation file. It means something only to
something that reads those files, and **the runner reads none**: there is no `.xlf` or
`Translations/` handling anywhere in `AlRunner/`, and `docs/limitations.md` already records that
the runner installs no BC translation resources. So no AL test can observe this member, and the
honest declaration is a scope boundary. What would invalidate it is the runner gaining
translation support — not anything about BC.

It is also **derivable**, which matters because it means the entry can never become a permanent
limit. BC's own `LanguageKeyHelper.ConstructObjectHash` is

```
(uint)(FNV-1a-32 over the UTF-16LE bytes of the name + int.MaxValue)
```

and a key is `<Kind> <hash>` components joined by `" - "`, ending in the property. Measured
against the ground truth: **2,153 of 2,153 keys reproduced exactly**, with the property component
decomposing to `Caption` (1,138), `ToolTip` (988) and `OptionCaption` (27) and nothing left
unexplained. `MetadataEquivalenceHarnessTests.TranslationKeysAreDerivable_NotAPermanentLimit`
pins that, so the claim under the allowlist reason is checked rather than asserted.

**`CaptionML` is a different member and is not covered by any of this.** It is the caption
*text*, it is AL-observable — `Format()` on an option with an `OptionCaption`, field captions in
error messages — and `MetaField.CaptionML.<presence>` (988) is declared separately as a tracked
defect. The two must not be folded together: doing so would license a real difference behind a
scope declaration that does not apply to it.

<a id="the-oracle-is-build-time-per-app-metadata"></a>
## The oracle is build-time per-app metadata, not runtime merged metadata

The ground truth is what BC's compiler emits **for one app at build time**. A running BC system
holds something else: metadata with every installed app's tableextension deltas applied. The
runner models the second, because that is the state AL actually executes against.

So where the runner shows a merged view, a difference is **expected by construction and is not
evidence about the runner**. Measured: 115 fields on four Business Foundation tables (230, 242,
308, 6635), which are tableextension fields other registered apps contribute. In BC 29 everything
is merged at database level anyway, which makes the runner's merged view the forward-looking one
rather than a deviation.

Two consequences worth meeting before a non-empty diff is:

- **A multi-app bundle can never produce an empty diff.** That is a limitation of this oracle, not
  a wart in the runner. Do not read the residue as normal noise, and do not read it as a bug —
  read the three `oracleLimitation` entries and check the difference is one of them.
- **The faithful comparison would need BC's post-publish runtime metadata** — what a service tier
  holds after deltas are applied — which is not obtainable without a tier. That is the ceiling on
  how empty this diff can ever get.

Verified across BC 27.5.46862.53931, 28.1.49838.53910, 28.1.49838.54044 and 28.4.53241.53989:
the same 74 entries cover every difference on all four, with none stale. The counts move
(System Application `DataClassification` is 661 / 661 / 663 / 617 and `EnumTypeId` 76 / 76 / 76 /
75), the membership does not.

`maxOccurrences` is unused in the current file. The mechanism is right for a difference whose
count is bounded independently of the build; a count measured on one BC build and one set of apps
is not that, because the unit legs run 27.5 and 28.4 while the current numbers were measured on
28.1.

<a id="what-is-compared"></a>
## What is compared, and what is not

The harness compares `MetaTable` documents. A bundle carries every kind BC emitted — codeunits,
pages, enums, permission sets, queries, reports, xmlports, and the `MetadataRuntimeDeltas`
documents that cover table, page and permission-set extensions — and
`MetadataEquivalenceHarnessTests` asserts the set it compares AND the set it does not, so
covering less cannot happen quietly.

**Base Application is not covered.** Its emit needs a `PublicKeyToken=null` copy of
`Microsoft.AspNetCore.StaticFiles` that no BC artifact ships, and without it BC's emitter
produces zero objects for the whole app (#3549). The generator treats a zero-object emit as a
failure rather than writing an empty bundle, and `apps.json` says why Base Application is absent
rather than listing it and failing every leg.

<a id="running-it"></a>
## Running it

```bash
tools/gen-metadata-ground-truth.sh              # once per BC build
tools/engine-test-bootstrap.sh                  # the bc-engine-serial collection needs this
AL_RUNNER_METADATA_EQUIVALENCE_REPORT=/tmp/diff.tsv \
  dotnet test AlRunner.Tests -c Release --no-build --settings engine.runsettings \
    --filter FullyQualifiedName~MetadataEquivalenceHarnessTests
```

The dump is one line per differing member per object: app, object, path, `Type.Member`, BC's
value, the runner's. It is the working specification for the reader work.

`AL_RUNNER_METADATA_GROUND_TRUTH` overrides where bundles are read from and written to.

<a id="a-single-unresolved-dotnet-reference-costs-the-whole-app"></a>
## A single unresolved .NET reference costs the whole app

Worth knowing before adding an app to `apps.json`. BC's emitter is atomic per module: one AL
error anywhere produces **zero** objects for the entire app, not a smaller set. Two instances:

- System Application's `SamplingPerfProfilerImpl` calls
  `JsonSerializer.Deserialize(TextReader, Type)`. The service tier ships the net6.0 build of
  Newtonsoft.Json, whose `TextReader` parameter is typed against `System.Runtime 6.0.0.0`, and no
  .NET 6 reference assemblies exist on a net8 or net10 box — so AL0133, and no ground truth at
  all. The generator ships the netstandard2.0 build of the same Newtonsoft version in a
  `dotnet-shims` directory at the head of its probing paths; see the csproj.
- Base Application's `Microsoft.AspNetCore.StaticFiles`, above, which has no such fix yet.

So an app that "almost compiles" produces nothing, and the generator says so loudly instead of
writing a bundle.
