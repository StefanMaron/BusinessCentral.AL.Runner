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

An entry may also set `direction` to `runner-only` or `bc-only`, and ten do. An entry covers both
directions unless it says otherwise, and **a reason that justifies one direction must not license
the other**. All three `oracleLimitation` entries are `runner-only`: they exist because the runner
shows a merged runtime view, so BC-absent / runner-present is what they justify — while the
reverse, BC emitting a field the runner never builds, is a hard reader defect that must still
fail. Undirected, that defect was covered on every table by a permanent, uncapped licence.

There is deliberately **no occurrence cap**. One was implemented and used by nothing, which is a
ratchet nobody turns. No entry here has a bound that survives a rebuild — the counts move with the
BC build and with which apps are bundled — so any cap would be a number nobody measured. Direction
narrows *categorically* instead, which is build-independent and checkable per difference.

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

Verified across four BC builds: the same entries cover every difference on all four, with none
stale. The membership does not move; the counts do, which is the whole reason nothing asserts one.

| BC build | differences | members | System App tables | `Editable` | `DataClassification` | `EnumTypeId` |
|---|---:|---:|---:|---:|---:|---:|
| 27.5.46862.53931 | 65,505 | 74 | 127 | 78 | 617 | 75 |
| 28.1.49838.53910 | 70,728 | 74 | 138 | 78 | 661 | 76 |
| 28.1.49838.54044 | 70,728 | 74 | 138 | 78 | 661 | 76 |
| 28.4.53241.53989 | 70,836 | 74 | 138 | 78 | 663 | 76 |

The last three columns are System Application's own declared fields. **They are the pre-#3545
measurement and are all zero now** — kept because they are what the four-build verification was
run against, and because the first two columns are the baseline the section below reports
against. They are recorded here and asserted nowhere; see "Three symbol properties the reader
dropped" for the post-fix figures.

`maxOccurrences` is unused in the current file. The mechanism is right for a difference whose
count is bounded independently of the build; a count measured on one BC build and one set of apps
is not that, because the unit legs run 27.5 and 28.4 while the current numbers were measured on
28.1.

<a id="three-symbol-properties-the-reader-dropped"></a>
## Three symbol properties the reader dropped (#3545)

`Editable`, a field's `DataClassification` and the enum an `Enum "X"`-typed field names are
all stated in `SymbolReference.json`, and the reader read none of them. Together they were
2,628 of the 70,728 differences, and all three are AL-observable: a field's editability, the
classification the Field and Table Metadata virtual tables report, and whether an enum-typed
field can be resolved back to its enum object at all.

**All three landed**, the enum id last and by a different route. Reading it was never the
problem: stating `enumTypeId` on the `MetaField` makes BC's own
`FieldDataProvider.GetFieldRecordBuffer` resolve that id through `NCLMetadata`, and the runner
registered no metadata object for an enum — so every read of the Field virtual table for Base
Application table 1366 threw `NavMetadataNotFoundException("Enum 8889")`, which aborted codeunit
2 `Company-Initialize` and reported the corpus app as `EXEC-FAIL` with 0 of 3,112 tests run.
#3594 made the id resolvable first — a Cecil rewrite of
`NCLFieldEnumMetadata.GetEnumMetadataFromMetadataProvider` onto `AlEnumMetadataRegistry`, plus
registration of BC's own 16 platform enums, which no app declares — and only then stated it. See
[field-enum-metadata-resolution.md](field-enum-metadata-resolution.md).

The rules below are measurements, not readings of the AL documentation. Each was checked by
joining the fields of Business Foundation and System Application in `SymbolReference.json`
to the same field in BC's own emitted metadata, on four BC builds — **3,848 field
observations, zero counterexamples for all three rules**.

<a id="what-the-3848-counts"></a>
**What those 3,848 are, and what they are not.** They are the **table-declared** fields of the
150 `MetaTable` objects the two apps emit: 911 + 978 + 978 + 981 across the four builds. They
are NOT every field the two apps declare. Each build also carries 125 fields added by a
`tableextension`, and the join reaches only 10 of those per build — 40 observations across the
four, which is the whole of the difference between 3,848 and the 3,888 a reader who counts
declared fields arrives at (#3603).

**The rule was separately checked on those 40, and it holds** — zero counterexamples, measured
the same way. They are not an unmeasured population; they are counted separately because they
join through a different route, described next.

| | rule |
|---|---|
| `Editable` | stated only where it is `0`; silence means true. So only the false is carried, and `MetaField`'s own null default decides the rest. **Landed.** |
| field `DataClassification` | see the next section — it is the one with exceptions. **Landed.** |
| `EnumTypeId` / `EnumTypeName` | `TypeDefinition.Subtype.Id` and `.Name`, when `TypeDefinition.Name == "Enum"`. Presence of the subtype and presence of BC's emitted `EnumTypeId` agree on every observation. **Landed (#3594)** — stating it required making the id resolvable first. |

**`EnumTypeId`/`EnumTypeName` are not read from AL source either**, independently of #3594. The
enum's OBJECT ID is not in the type text, and the name alone would produce a pair BC never emits
— an id of 0 beside a real name. A source-compiled enum field is served by
`FixupEnumFieldOptionMetadata`, a different mechanism, and half of this pair is worse than none
of it.

<a id="keys"></a>
## Four key properties the reader dropped, and the two names that are not the same name (#3568)

`MetaKey` carries **two** name members and they mean different things. Getting that wrong is
what kept this cluster mis-declared through two issues.

| member | what it is | reaches AL? |
|---|---|---|
| `MetaKey.KeyName` | the name AL **declared** (`Key1`, `PrimaryKey`, `UniqueID`), stated verbatim in `SymbolReference.json` | **yes** |
| `MetaKey.Name` | a **derived** positional field spec over field ids (`Field1,Field2`), which BC regenerates | no |

`NCLMetaKey.CreateFromMetaKey` (BC 28.1, `Ncl.dll`) settles it: it passes `metaKey.KeyName`,
`metaKey.Unique`, `metaKey.SumIndexFields` and `clusteredOverride || metaKey.Clustered` into the
`NCLMetaKey` the runtime uses, and never passes `metaKey.Name` at all. So `KeyName` is
AL-observable — `ObsolescenceGuard.ThrowIfObsoleted` formats `key.GetKeyName()` into the error a
`SetCurrentKey` on an obsoleted key raises — and `Name` is emitter-only and stays declared in the
allowlist.

The reader built the primary key under a hardcoded `"PK"` with `clustered: true`, carried
neither `Unique` nor `SumIndexFields` on any key, and gave `FieldMetadataRelation` an id with no
name. Six members, **688 differences**, all now zero:

| member | differences | what it was |
|---|---:|---|
| `FieldMetadataRelation.Name` | 340 | the id was carried, the name was not |
| `MetaKey.DebuggerDisplay` | 226 | follows `KeyName`, not `Name` — it went green with the rest |
| `MetaKey.KeyName` | 91 | hardcoded `"PK"` on every primary key |
| `MetaKey.Clustered` | 11 | hardcoded `true` on every primary key |
| `MetaKey.SumIndexFields` | 8 | never built |
| `MetaKey.Unique` | 3 | never read |

<a id="clustered"></a>
### `Clustered` defaults to FALSE, and the synthesized key is the exception

This is the one that reads backwards. Measured over all 150 tables of Business Foundation +
System Application at 28.1.49838.53910, joining each key in `SymbolReference.json` to the same
key in BC's own emitted document — **150 tables, 236 keys, zero counterexamples**:

| what the symbol file says | BC emits | count |
|---|---|---:|
| `Clustered = "1"` | true | 133 |
| `Clustered = "0"` | false | 1 |
| a **declared key** stating no `Clustered` | **false** | 86 (11 of them primary keys) |
| **no `Keys` at all** on the table | **true** | 6 |

So `Clustered` is stated verbatim with false as the default, and the only key BC clusters
without being told to is the one it **synthesizes** for a table that declares no key — those 6
tables carry `"Keys": null`. BC names that synthesized key after its single field (`ID`,
`IgnoreCase`, `ApiVersion`, `User Security ID`, `Code Set`), not `"PK"`.

Assuming instead that a primary key defaults to clustered is what the reader did, and it was
wrong on the 11 declared primary keys that state nothing.

### Two spellings, because the two sources disagree

`SumIndexFields` is stated as **field ids** in the symbol file (`"Field11,Field12"`) and as
**field names** in AL source (`SumIndexFields = "Self Time", "Full Time"`). Both readers exist
and they parse different things; `BcAppSymbolCacheKeyPropertiesReadTests` pins the symbol-file
form, including the `"1"`/`"0"` booleans and the absent-versus-false distinction the builder
depends on.

<a id="field-dataclassification-inherits-its-owner"></a>
## A field's DataClassification inherits its OWNER, with two exceptions

BC's emitter states the *effective* classification on each field, not the declared one. The
rule, and both exceptions, are load-bearing:

1. The field's own `DataClassification`, when it states one — 613 of the 978 **table-declared**
   fields on BC 28.1, agreeing with BC on 613 of 613. (978 is the symbol-file population; the
   988 fields BC emits across the same 150 tables are those 978 plus the 10 extension-added
   fields BC merges in — see below. Two populations, both correct, counted from opposite
   sides of the join.)
2. Otherwise the **owning object's**: the `table` for a field the table declares, the
   **`tableextension`** for a field an extension adds. Not the extended table — System
   Application's extension of `User Details` declares no classification and neither do its
   six fields, and BC answers `CustomerContent` for all six while the extended table declares
   `SystemMetadata`. Inheriting from the extended table answers `SystemMetadata` on every one
   of them, which is how this was found: the first attempt cleared 1,558 differences and
   created 6.
3. Otherwise `CustomerContent`, which is `ALDataClassification`'s member 0 and therefore what
   `MetaField` answers when nothing is passed.

**The exceptions.** A `FlowField`/`FlowFilter` and a `Blob` field that are silent about
`DataClassification` inherit nothing — BC emits no attribute for them, so they land on
`CustomerContent` however the owner is classified. This is not cosmetic: 11 such fields sit on
tables declaring `SystemMetadata`, so inheriting unconditionally trades one wrong answer for
another rather than fixing anything. Verbatim from System Application's table 9131, whose
table line reads `DataClassification = SystemMetadata`: field 1 `Id` (`Text[250]`, silent) is
emitted `SystemMetadata`, and field 8 `FieldsJson` (`Blob`, silent) is emitted with no
`DataClassification` at all.

<a id="the-125-extension-added-fields"></a>
### The 125 extension-added fields, and why only 10 of them are observable

Measured on BC 28.1.49838.54308 (#3603). The two apps declare six `tableextension` objects
between them, carrying 125 fields. Every one targets a table in its own app, so this is not a
cross-app visibility question — the discriminator is `ObsoleteState`:

| extension | target | fields | `ObsoleteState` | merged into the emitted `MetaTable`? |
|---|---|---:|---|---|
| `Plan User Details` | `User Details` (774) | 6 | none | **yes** |
| `NoSeriesLineObsolete` | `No. Series Line` (309) | 4 | `Removed` | **yes** |
| `NoSeriesObsolete` | `No. Series` (308) | 4 | `Moved` | no |
| `ObsoleteSourceCodeSetupExt` | `Source Code Setup` (242) | 107 | `Moved` | no |
| `ObsoleteSourceCodeExt` | `Source Code` (230) | 2 | `Moved` | no |
| `ObsoleteReturnReasonExt` | `Return Reason` (6635) | 2 | `Moved` | no |

**A `Moved` field is gone from the metadata; a `Removed` field is still in it.** `Moved` means
the field now lives in another app, so BC's emitter leaves it out of this app's table
altogether — table 242 emits **1** field against the 107 its extension names. `Removed` means
the field stays declared and is merely no longer usable, so it is emitted, carrying its
`ObsoleteState`. That is why 309 emits 18 fields including ids 11 and 10000-10002.

So the 115 unobservable fields are unobservable **because BC emits nothing for them** — there
is no ground truth to disagree with, on this or any other member. This is a property of what
Microsoft's own apps happen to declare, not a limit of the harness.

**The rule holds on all 10 that are observable**, expected against emitted, zero
counterexamples. Six of them are `User Details` 774-779, which is the case that established the
owner is the `tableextension` and not the extended table: the extension declares no
classification, the extended table declares `SystemMetadata`, and BC answers `CustomerContent`
on all six. The other four are `No. Series Line` 11 and 10000-10002, whose extension is also
silent and which BC likewise answers `CustomerContent`.

**What is therefore still unmeasured, stated plainly:** no field in either app is added by a
`tableextension` that *declares* a `DataClassification` of its own — all six extensions are
silent. So rule 2's inheritance is confirmed for a silent extension and **the case of an
extension declaring a non-default classification has no observation behind it in this
population**. It is not a counterexample; it is an untested branch.

**BC's six platform-added fields** were wrong on both members and are fixed with them. Their
values are BC's own, read out of `SystemFieldsHelper` in `Microsoft.Dynamics.Nav.Types`, which
builds them by parsing boilerplate XML: `Editable="0"` on all six, and
`DataClassification="EndUserPseudonymousIdentifiers"` for `$systemId`, `SystemCreatedBy` and
`SystemModifiedBy` against `"SystemMetadata"` for `timestamp`, `SystemCreatedAt` and
`SystemModifiedAt`. The harness measured the same split independently across 150 tables.

**What this cost, measured on BC 28.1.49838.53910** over the same two apps:

| | before | after |
|---|---:|---:|
| differences | 70,728 | 68,177 |
| members differing | 74 | 72 |
| `MetaField.Editable` | 987 | **0** |
| `MetaField.DataClassification` | 1,564 | **0** |
| `MetaField.EnumTypeId` | 77 | 77 — still open at the time of this measurement; closed later by #3594 |
| `MetaField.EnumTypeName` | 1,888 | 1,888 (#3568) |

No other member moved in either direction, and no new member appeared.

**The symbol cache replays a parse, so this needed a `CacheVersion` bump** (34 → 35), and the
reason is worth keeping. `DataClassificationName` is stored *effective* rather than declared,
which is a different value read out of unchanged bytes with no change of shape — exactly what
`BcAppSymbolCache`'s structural payload hash cannot see. Measured here rather than reasoned
about: a payload written by an earlier build of this same change was replayed warm and put 154
of System Application's fields back on `CustomerContent` while the build was green and the
harness reported the reader as fixed.

<a id="a-check-that-cannot-fail-reads-like-a-check-that-passed"></a>
## A check that cannot fail reads exactly like a check that passed

Three defects in this harness have now had the same shape, and it is the shape to watch for in
anything added to it.

1. **A reason category that could not be wrong.** Seven `TranslationKey` entries claimed a
   permanent limit of the symbol file — 36% of the diff — on evidence about *storage*, which says
   nothing about *derivability*. Fixed by making the kind of reason an explicit field with its own
   evidence requirement.
2. **A licence that covered both directions.** The three merged-runtime entries were written for
   "the runner has a field BC's build-time emit does not", and silently also covered "BC emitted a
   field and the runner built none" — a hard reader defect, on every table, permanently. Fixed by
   `direction`.
3. **An assertion that went inert when the build moved.** The known-defect counts were pinned per
   four-part BC build. CI resolves 28.4 to a build that moves, so the lookup missed, the four
   numbers went unasserted, and the test passed green having checked nothing.

The third is worth dwelling on, because the fix was not to pick a failure mode. Pinning tighter
goes inert; pinning looser asserts a number that held once. **The counts move and the shape does
not**, so the test asserts the shape: the reader answers a *constant* — `Editable` `True` where BC
says `False`, `DataClassification` `CustomerContent`, `EnumTypeId` `0` — on every occurrence, on
every build measured. That is stronger than a count (a count passes if the reader answers a
different constant) and it cannot disappear. The counts live in the table above, where a
measurement belongs.

The general rule: **an answer a check could not have failed to give is not evidence.** When adding
an assertion here, ask what would have to be true for it to fail, and make sure that state is
reachable.

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
