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
## Translation keys are out of scope by decision, and separately are derivable

**Translations are out of scope for the runner. This is a scope decision by the repository
owner, made 2026-09-10** (relayed on #3568): translations are not relevant right now, and
nothing in a run has ever had to do with them. These seven members are therefore a surface the
runner does not need to reproduce, and **no one should spend effort deriving BC's translation
keys or symbol kinds**. Seven members, ~25,700 differences — about a third of the total.

**The runner's answers here are unmeasured, not right.** `0` and `Module` are what the
`MetaField` ctor's own defaults leave behind. Nothing has checked them against BC, and the
entries say so, because an out-of-scope declaration that reads as "the derivation is correct"
would be a false claim rather than a scope boundary.

Corroborating that decision rather than grounding it: a `CaptionTranslationKey` is a lookup id
into a translation file, it means something only to something that reads those files, and **the
runner reads none** — no `.xlf` or `Translations/` handling anywhere in `AlRunner/`, and
`docs/limitations.md` records that it installs no BC translation resources. So no AL test could
observe this member today even if the scope call went the other way. What invalidates these
entries is **the owner putting translations back in scope**, not anything about BC.

It is also **derivable**, which matters because it means the entry can never become a permanent
limit — deriving it is possible and simply not wanted. BC's own `LanguageKeyHelper.ConstructObjectHash` is

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

The harness compares `MetaTable`, `PageDefinition`, `CodeUnit`, `Query` and `XmlPort`
documents. A bundle carries every kind BC emitted — enums, permission sets, reports, and the
`MetadataRuntimeDeltas` documents that cover table, page and permission-set extensions — and
`MetadataEquivalenceHarnessTests` asserts the set it compares AND the set it does not, so
covering less cannot happen quietly.

**#3782 is the programme that empties the not-compared list**, one kind per pull request, in the
order that issue records: `PageDefinition`, `CodeUnit`, `Query` and `XmlPort` (done), then
`Report`, `PermissionSet`, `Enum`, `MetadataRuntimeDeltas`. Each step adds its kind to
`ComparedKinds`, lands the resulting allowlist with a reason per member, and deletes its kind
from the `stillUncompared` list in `The_harness_states_which_kinds_it_does_not_compare`.

<a id="the-page-oracle-is-pagedefinition-not-metapagedefinition"></a>
### The page oracle is `PageDefinition`, not `MetaPageDefinition`

Tables have a factory — `MetaTableSnapshotSerializationHelper.CreateMetaTableFromXml`. Pages have
none; the reader is a constructor. BC's `Types` assembly ships **two** page types with the same
public shape, both with a public `(XmlNode)` constructor, and **neither throws** on the emitter's
own document:

| type | reading the emitter's Page 257 document |
|---|---|
| `Microsoft.Dynamics.Nav.Types.Metadata.PageDefinition` | `ID=257`, `Name="Source Codes"`, `Properties` and `Content` populated |
| `Microsoft.Dynamics.Nav.Types.Metadata.MetaPageDefinition` | `ID=0`, `Name=null`, `Properties=null`, `Content=null` |

Measured on BC 28.1.49838.53910. `MetaPageDefinition` accepts the node and ignores it. Using it
for both sides compares an empty object against an empty object: 235 pages "compared", **zero**
differences, and every other test in the file still green, because they all measure
*differences*. `MetadataEquivalencePageOracleTests` pins the asymmetry in both directions so the
`Meta`-prefixed type cannot be reached for by analogy with `MetaTable`.

<a id="page-controls-pair-by-id-in-three-spellings"></a>
### Page control collections pair by id, in all three spellings

The differ pairs collection elements by position by default, because order is meaningful in AL.
That is wrong for page controls, for a structural reason: BC's `Controls` list holds the page's
**ordinary field controls** as well as its part controls, and the runner reconstructs only the
parts. The lists therefore differ in length by construction, and positional pairing shifts every
element after the first divergence.

Measured before pairing was applied: 12 fabricated `Name` / `ID` / `PagePartID` triples across 7
System Application pages — page 4312 reporting `InputMessagePart` against `LogsPart`, page 9855
`Permissions` against `MetadataPermissions`. Not one was a disagreement about any control.

Two things had to be right for pairing to actually engage, and each failed silently on its own:

1. **The id property's spelling.** `MetaField` spells it `Id`; every page control type spells it
   `ID`, and reflection's name lookup is case-sensitive. `MetadataObjectDiffOptions.IdPropertyNames`
   now lists both. A collection whose elements have no int id still falls back to position — that
   is the legitimate answer, and `ControlContainerDefinition` and `ContentDefinition` genuinely
   have no `ID`.
2. **The member signature.** BC implements these interfaces *explicitly* and backs each collection
   with a field, so the differ walks one collection three times and reports it under three
   signatures — `ControlContainerDefinition.Controls`,
   `ControlContainerDefinition.Microsoft.Dynamics.Nav.Types.Metadata.IMetaControlContainerDefinition.Controls`,
   and `ControlContainerDefinition.#controlsField`. `PairByIdMembers` matches on the signature, so
   all three must be listed; listing only the plain one leaves the other two positional.

<a id="what-the-page-comparison-found"></a>
### What the page comparison found

235 pages, **32,958 differences across 124 members** on BC 28.1.49838.53910. Two groups, and they
need different answers:

**First, read the entry count correctly.** The allowlist gains 117 page entries, but **49 of them
are `#field` / `*Specified` companions** that BC's serializer sets from the same value as the member
beside them. There are **68 distinct members**, and comparing 117 against the table side's counts
overstates the page gap.

- **55 entries / 30 distinct members — values `SymbolReference.json` already carries and
  `EmitPageXml` does not read.** Tracked on **#3784**. This includes the `Actions` (106 pages),
  `Methods` (107) and `Views` (4) arrays, which are whole objects the runner emits no element for.
  `Extensible` is the sharpest: the runner hardcodes `"1"`, BC answers `0` on 104 of 235 pages, and
  the symbol file states `Extensible=0` for **all 104 with zero omissions** — a pure
  read-don't-guess fix rather than merely a wrong default.

  **Of those 55, thirty entries were closed by the reader fix and deleted** (#3784, step 1 of the
  four the owner approved): `Extensible`, `RefreshOnActivate`, `UsageCategory`, the two inherent
  permission masks, the four `…ML` strings with their `<presence>` companions, and
  `InsertAllowed` / `DeleteAllowed` / `MultipleNewLinesSpecified`. What stayed, and why, is
  [the read/derive split](#a-read-and-a-derivation-are-different-fixes) below.
- **62 entries / 38 distinct members — the ordinary field-control tree the runner does not build
  today**, plus the translation keys.

<a id="a-read-and-a-derivation-are-different-fixes"></a>
### A read and a derivation are different fixes, and the difference is measurable

#3784 looked like one bucket — "properties the symbol file states and the emitter drops" — and is
two. Sorting them needs a cross-tabulation of *(symbol value → emitted attribute)* over every page,
not a reading of the property's name, because the two look identical from the allowlist entry.

Measured on **BC 28.4.53241.54407**, Business Foundation (11 pages) + System Application (225),
symbol file against the ground truth this harness generates:

| the runner must | example | what the cross-tab shows |
|---|---|---|
| **read** the stated value | `Extensible` | absent → BC `"1"` (110), `"0"` → `"0"` (105), `"1"` → `"1"` (21) — BC's answer is a function of the symbol value alone |
| **derive** a value the file never states | `AnalysisModeEnabled` | 94 pages where the symbol file states **nothing** and BC writes `"1"`; it tracks `PageType` (List/Worksheet), not the file |

Three traps this sorting sprang, each of which would have produced a wrong entry deletion:

- **A property can be BOTH.** `HelpLink` is a read on the 6 pages that state it and a **derived
  default on the other 230** (`https://learn.microsoft.com/dynamics365/business-central/` and
  friends). Reading it closes a sixth of the difference and the entry stays — so "the fix reads
  this property" does not imply "the entry goes".
- **Two write rules, not one.** BC writes `Extensible` and `RefreshOnActivate` on all 236 pages
  whether the AL states them or not; everything else it writes *iff* stated, because
  `PageProperties` raises its `…Specified` bit **from the setter** and `Equals()` compares it.
  Emitting an AL default for a silent page is a different document, not a harmless one.
- **A fix closes entries that never cited it.** Writing the `…ML` strings materialises BC's
  `MultiLanguage` object, so eight `…ML.<presence>` / `#…MLField.<presence>` entries filed under
  the control-tree group went stale too.

**So take the stale list from the harness, never from the fix's own account of itself.**
`No_allowlist_entry_has_gone_stale` prints exactly which entries stopped matching; predicting them
from the diff got `HelpLink` wrong in one direction and those eight `<presence>` entries wrong in
the other.

<a id="the-control-tree-is-a-cost-decision-not-an-unprovable-one"></a>
### The control tree is a cost decision, and it has a proof path

**No page entry in the allowlist claims a value is underivable, and that distinction is the point.**

An earlier draft of this document and of the allowlist said the control tree could not be
reconstructed faithfully, because `SymbolReference.json` stores the binding as AL text (`Rec."No."`)
while BC's document stores the compiled `DataColumnName`. The first half is true; the conclusion
drawn from it was wrong, and it would have recorded a live architecture question as settled.

`DataColumnName` **is the field number**, resolvable through table metadata the runner already
builds, and control ids are identical in both sources — so every derived value is independently
checkable against the ground truth this harness now provides. Measured on BC 28.1.49838.53910:

| | |
|---|---|
| Rec-bound controls whose source table is in the bundle | **442 of 442 exact, 0 wrong** |
| residual category 1 | field names differing in case or spacing (`Rec."Task Id"` vs `Task ID`) |
| residual category 2 | controls bound to a page **variable**, which BC names `"Control" + <id of the first control bound to that variable>` — a dedup rule over data the symbol file already carries |

**The resolution machinery already exists and is in use.** `DependencyPageMetadataXml.cs`'s own
header records that `SubFormLink` field names "ARE resolved (to numeric ids, off the part's own and
the host's SourceTable — see `EmitSubFormLinkXml`)". The file cited as evidence for impossibility
does the resolution.

`ControlGUID` is the same shape — a **pure encoding**, not an opaque designer token:

```
{<pageId:08x>-<ctlId & 0xFFFF:04x>-0000-0c<(ctlId >> 24) & 0xFF:02x>-<(ctlId >> 16) & 0xFF:02x>00836bd2d2}
```

Page 257, control 791368264 gives `{00000101-5248-0000-0c2f-2b00836bd2d2}`, character for character
what BC emits: **5,001 of 5,001 exact** on 28.1.49838.53910 and 5,035 of 5,035 on 28.4.53241.54407.

So these are **cost and scope decisions with a proof path**, tracked on #2460 for actions. What the
entries record is that the work has not been done — never that it could not be.

<a id="one-build-measured-three-evaluated"></a>
### The allowlist is measured on ONE build and evaluated on THREE

This property is easy to miss and it is load-bearing for every kind #3782 adds. An agent
generates the allowlist from the bundles on one machine — one BC build. CI then evaluates that
same file on **three** BC versions per pull request (27.0, 27.5, 28.4) and **eight** on `main`.

A difference exists only if the object carrying it exists on that version **and** declares the
property shape that produces it, and BC moves both between minors. So an entry can be correct on
two legs and match nothing on a third — which `No_allowlist_entry_has_gone_stale` reports as a
stale entry, because from its side "nothing matched" is exactly what a landed fix looks like.

That fired on the first kind added after tables (#3782). Four `InfopartPageDefinition` entries
passed on 27.0 and 28.4 and failed on 27.5:

| | 27.5 | 28.1 / 28.4 |
|---|---|---|
| the only page with a part-control `ProviderID` | 4306 "Agent Tasks" | 8705 "Table Information Card" |
| does that part declare `Editable`? | **yes**, so the runner matches BC and there is no difference | no, so there is one |

**Deleting the four entries was not the fix** — the differences are real on the other two legs,
where the harness requires an entry. The allowlist had no way to say "this difference exists on
some versions and not others".

`versionContingent: true` says it. An entry carrying it is exempt from the **unused** check and
from nothing else: it is still matched, so it can never hide a difference — a difference it does
not cover still fails, on every version. It requires a `Doc` pointer for the same reason
`outOfScope` does, since exempting an entry removes the signal that a landed fix must shrink this
file.

**Every step should run this check before pushing, and record the answer either way.** Step 2
(`CodeUnit`) did, and the answer was no: none of its five entries is version-contingent. The
check is cheap because the bundles are already on the box — count each member's population per
build rather than reasoning about it:

| member | 27.5 | 28.1 | 28.4 |
|---|---:|---:|---:|
| System Application codeunits | 506 | 533 | 534 |
| …carrying `ALNamespace` | 506 | 533 | 534 |
| …stating either Inherent mask | 455 | 481 | 482 |
| …emitting a `<Methods>` subtree | 135 | 137 | 138 |

27.5 is the leg that reddened step 1, and it carries all five populations. The contrast is the
point: step 1's failure was a member whose entire population was **one page**, so a version
moving that page's shape took the population to zero. A population in the hundreds cannot do
that. **The trigger is a small population, not a large difference count** — and the two are easy
to confuse, because a member differing on 558 objects and a member differing on one both read as
a single line in the allowlist.

**It is deliberately not a list of BC versions.** A list has to be edited whenever the matrix
moves and goes stale silently when it is not — the same defect as the build-keyed count pin that
went inert in `The_current_reader_reproduces_the_known_defect_shapes`. What the flag declares is a
*property* of the difference: its population is version-contingent.

<a id="a-flagged-entry-can-go-stale-without-the-harness-saying-so"></a>
### The flag's own blind spot: a flagged entry can go stale and nothing reports it

Exempting an entry from the unused check removes the one signal that says "this entry is
finished". So a `versionContingent` entry whose population has gone to zero **on every leg** —
because the fix landed — sits in the file as inert cover, and the harness stays green.

That has already happened once. #3791 moved five page flags outside the `SourceTable > 0` branch
and thereby closed the entire remaining population of six flagged entries
(`SourceObjectDefinition.{ModifyAllowed,DelayedInsert}` and their `#…Field` companions,
`PageProperties.{IsPreview,#isPreviewField}`). The harness could not report it, precisely
because the flag exempts them.

**This is never a false green.** An undeclared difference still fails on every version, so the
flag cannot hide a difference — it can only excuse an entry for not finding one. The cost is a
stale entry nobody is told about, which is a follow-up rather than a blocker.

Two consequences for anyone auditing:

- **"The harness passes" does not confirm a flagged entry is still live.** The technique that
  works is to flip the flag off and re-run: a genuinely stale entry then shows up in
  `No_allowlist_entry_has_gone_stale`.
- **An entry that is NOT flagged needs no such check** — it is already subject to the stale
  check, so the harness passing *is* the liveness proof. That is why step 2's five CodeUnit
  entries needed only the population measurement above: none is flagged, so none can go stale
  unnoticed.

**Applied to the population, not to the failure.** 15 of the 117 page entries have a whole
population of one or two occurrences on the measured build, resting on one or two objects with one
property shape. The four that failed on 27.5 are the ones that version happened to hit; all 15 are
marked, so the next six kinds do not rediscover this one leg at a time.

<a id="what-the-page-measurement-does-not-cover"></a>
### What the page measurement does not cover

**One BC build, two apps.** Base Application's ~4,000 pages are not measured at all: `apps.json`
excludes it because its emit needs a `PublicKeyToken=null` copy of `Microsoft.AspNetCore.StaticFiles`
that no BC artifact ships (#3549). None of the figures above is evidence about the pages most AL
tests actually touch.

**Base Application is not covered.** Its emit needs a `PublicKeyToken=null` copy of
`Microsoft.AspNetCore.StaticFiles` that no BC artifact ships, and without it BC's emitter
produces zero objects for the whole app (#3549). The generator treats a zero-object emit as a
failure rather than writing an empty bundle, and `apps.json` says why Base Application is absent
rather than listing it and failing every leg.

<a id="codeunits"></a>
## Codeunits: the runner has no `MetaCodeunit`, so its derivation is projected into one

Tables and pages each hand the harness a runner-built object. Codeunits have neither, and the
distinction decides whether the comparison measures anything at all.

**Nothing in the runner builds a `Types.Metadata.MetaCodeunit`.** `NavCodeunit.get_MetaCodeunit`
returns an `NCLMetaCodeunit` built by `CreateEmptyNCLMetaCodeunit` and populated with the AL
CLR type alone (`AlRunner/Patches/CodeunitPatches.MetaCodeunit.cs`) — a different type, and
empty of the members BC's document states.

**And the obvious substitute is BC's own answer.** The codeunit document in
`AlObjectMetadataRegistry` is what BC's emitter produced, captured at compile time; it is the
*same* document the ground truth holds. Handing it over would compare BC against BC and report
zero differences having measured nothing — the failure step 1 of #3782 hit with
`MetaPageDefinition`, where 235 pages compared clean against a default object.

So `RecordPatches.TryBuildCodeunitMetadataEquivalenceXml` renders the runner's genuinely
independent derivation — the `SymbolReference.json` properties `BcAppSymbolCache.ObjectSymbol`
carries, the same values `CodeUnit Metadata` (2000000137) answers from — into BC's document
shape, and BC's own `MetaCodeunit(XmlNode)` constructor parses both sides. One type against
itself.

**Only the five derived attributes are written**, and a value the runner does not derive is
left off rather than defaulted, so BC's constructor applies its own default and the reported
difference is a true statement about what the runner does not know.
`MetadataEquivalenceCodeunitOracleTests` asserts both halves: that BC's constructor really
parses (real values from a real document, not a default object), and that the runner's
projection does *not* state the emitter-only members.

### What the first run measured

BC 28.1.49838.53910, Business Foundation (25 codeunits) and System Application (533):
**558 codeunits, 2,220 differences across 5 members.**

Five of `MetaCodeunit`'s eleven members agree **exactly**, on every codeunit: `Id`, `Name`,
`SubType`, `TableNo` and `SingleInstance` — including all 12 System Application codeunits that
declare a `TableNo`, which bounds #3546 to the cross-app `#<appid>#`-qualified form it names.

| member | count | BC | runner |
|---|---:|---|---|
| `MetaCodeunit.ALNamespace` | 558 | `System.Utilities` | `<null>` |
| `MetaCodeunit.InherentEntitlements` | 505 | `Execute` | `None` |
| `MetaCodeunit.InherentPermissions` | 505 | `Execute` | `None` |
| `MetaRuntimeInfo.Methods.<presence>` | 326 | `MetaMethod` | `<absent>` |
| `MetaRuntimeInfo.#methodsDictionary.<presence>` | 326 | `MetaMethod` | `<absent>` |

The last two are one collection reached under two signatures — `MetaRuntimeInfo` backs the
property with a field and the differ walks both.

**None is a limit of the symbol file**, which is why all five are tracked on #3788 rather than
declared permanent. Checked against `SymbolReference.json` directly, on the same build:

- **`ALNamespace` is fully derivable.** Objects live in a nested `Namespaces` tree rather than
  the flat top-level `Codeunits` list, and the tree path equals BC's attribute for **533 of
  533**. `BcAppSymbolCache` walks that tree but does not retain the path.
- **Both Inherent masks are stated verbatim**, as `Properties` entries with value `"X"`;
  presence agrees for **533 of 533**. The letters are the mask spelling
  `RecordPatches.CodeunitMetadataFromBcDocument.cs` already parses from BC's document.
- **`Methods` is partly derivable.** The file states `Methods` with `Id`, `Name`, `Parameters`
  and `ReturnTypeDefinition`; BC's emitted names are a subset of the file's for 64 codeunits,
  **not** for 73 (BC emits event-publisher and internal methods the file omits), and 396
  codeunits emit no `<Method>` at all.

`MetaCodeunit` exposes **no** `SingleInstance` member, so the harness cannot compare that column
in either direction — which is how #3790 stayed hidden: the symbol file writes `"1"` and the
codeunit parser matched only the word `"true"`, so all 38 single-instance System Application
codeunits read as false. `CodeunitSymbolSingleInstanceSpellingTests` pins both spellings.

<a id="one-verdict-for-a-missing-bundle"></a>
## One verdict for a missing bundle, shared by every reader

"There is no ground-truth bundle" has **three** answers, not two, and only
`MetadataEquivalenceBundleGate.RequireBundles()` gives them (#3789):

| state | verdict |
|---|---|
| bundles exist | return them |
| none, on a developer box | **skip** — the generator is a provisioning step nobody has run |
| none, **on CI** | **fail** — the generator runs before `dotnet test` by construction, so its absence is a workflow regression |

The third is the one that matters, and it is the one that was missing. Five classes read a
bundle and each had written its own `Skip.If(bundles.Count == 0, …)` — including
`MetadataEquivalencePageOracleTests`, whose entire purpose is pinning that BC's
`MetaPageDefinition` accepts the emitter's document and reads nothing from it. **A skip reads
green in the summary line**, so a bundle-less CI leg silenced precisely the discrimination that
stops the harness measuring nothing: the anti-green-over-nothing class was the one without the
guard.

Not currently exploitable, and worth saying so rather than overstating the fix — the real gate
runs through `RunAll()` and does fail loudly, and `Unbuildable.Count == 0` plus
`No_allowlist_entry_has_gone_stale` make "N objects compared, 0 differences" unreachable without
something failing. It is still the wrong shape, for the reason
`.claude/rules/guards-need-a-third-state.md` gives: the third state must not be spelled as the
success state, and a guard safe only by accident of a neighbour is still on that rule's list.

**Held in place by a test, not a convention.** `MetadataEquivalenceBundleGateTests` asserts that
nothing but the gate calls `LoadBundles`, that no reader writes its own empty-bundle skip, and
that the gate really throws under `CI=true` and really skips without it — driven through
`AL_RUNNER_METADATA_GROUND_TRUTH` pointed at an empty directory, so it exercises the real path.
That matters because #3782 has five more object kinds to go, each adding a class that reads a
bundle, and the cheap thing to write is a bare `Skip.If`.

<a id="every-bundled-object-must-state-a-real-id"></a>
## Every bundled object must state a real id, and BC spells it two ways

BC's emitter states an object's id as a **root attribute** for `MetaTable`, `PageDefinition`,
`CodeUnit`, `Enum`, `PermissionSet` and `MetadataRuntimeDeltas` — and as a **direct child `ID`
element** for `Query`, `XmlPort` and `Report`.

`ClassifyDocument` read the attribute only, so all 12 of those documents in a System Application
bundle carried `Id = 0` (#3782, steps 3/4). That is the worst shape a wrong value can have: it
keys nothing, it is not unique, and it reads exactly like a real id. The harness keys its
comparison on `(kind, id)`, so seven queries would have collapsed onto one object key with
nothing reporting it — and the generator's own *file naming* had already worked around the same
fact, which is how it survived unnoticed:

> An earlier dump keyed files on kind+id alone; Report, Query, XmlPort and ReportExtension carry
> their id in a CHILD element, so every one of them landed on id=0 and collapsed onto a single
> file per kind.

The lookup now tries the attribute, then a **direct** child `ID`/`Id` — direct, because a
`QueryColumn` states its own id the same way and a descendant search would return a column's id
for the query itself. `MetadataGroundTruthObjectIdTests` pins all three claims against the
bundle a test process is really about to read, over **every** kind rather than the compared
ones, so the next step's `Report` fails before that step is written rather than after.

The generator's duplicate-id guard was widened at the same time, from `MetaTable` alone to every
id-keyed kind. `MetadataRuntimeDeltas` is deliberately excluded: one such root covers
TableExtension, PageExtension and PermissionSetExtension, so several legitimately carry the id
of the object they extend.

<a id="queries"></a>
## Queries

**The oracle is `MetaQuery(XmlNode, int metadataAppGroupId, int languageAppGroupId)`**, a public
constructor, proven to parse rather than assumed to — measured against the emitter's own Query
774 "Users in Plans" on BC 28.1.49838.53910, it answers `Id=774`, `Name="Users in Plans"`,
`QueryType=Normal`, `InherentPermissions=Execute` and `DataItems[2]`.

**Queries are the one kind that needs no rendering step.** Tables hand over an `NCLMetaTable`,
pages and xmlports hand over a document; a query hands over the object itself, because
`RecordPatches.NclMetaQueryBuilder` already builds a real `Types.Metadata.MetaQuery` from
`SymbolReference.json` — the same object `NCLMetaQuery.CreateDynamicQuery` consumes at runtime.
So the comparison measures exactly what AL gets.

**The circularity that had to be refused.** `BuildMetaQueryDesign` prefers BC's own captured
document when one is registered (#3608), which is right at runtime and wrong here: that document
*is* the ground truth, so the comparison would be BC against BC and would report perfect
agreement having measured nothing. `TryBuildQueryMetadataEquivalenceDesign` therefore **throws**
rather than falling back when `HasBcQueryMetadataDocument` is true. A ground-truth bundle holds
precompiled dependency queries, which are never emitted here, so that throw is a finding rather
than a case to handle.

### What the query comparison found

7 queries, **616 differences**, of which 553 are the seven already-declared `TranslationKey.*`
members — they match on signature, so a query carries them like any other object. The real
remainder is **63 differences across 12 members**.

**Every structural member agrees exactly** — zero differences on `MetaQueryColumn.*`,
`MetaQueryDataItem.*` and `MetaQueryOrderBy.*`, including every compiler-assigned column id,
`FieldNo`, `ColumnType`, `MethodType`, `QueryColumnIndex`, `DataItemLinkType` and
`DataItemTable`. That is the strongest positive result the harness has produced for any kind,
and `The_query_structural_tree_still_agrees_with_BC_exactly` pins it: those ids are handed
verbatim to `NavQuery.ValidateExpectedType` and `GetColumnByNo` by precompiled callers, so a
regression is an AL-visible wrong answer.

| member | x | BC | runner | derivable from |
|---|---:|---|---|---|
| `MetaQuery.InherentEntitlements` | 4 | `Execute` | `None` | `InherentEntitlements = "X"`, 4 of 4 |
| `MetaQuery.InherentPermissions` | 4 | `Execute` | `None` | `InherentPermissions = "X"`, 4 of 4 |
| `MetaQuery.Caption` | 4 | see below | see below | both sides, see below |
| `MetaQuery.HelpLink` | 7 | the docs URL | `<null>` | a constant BC writes unconditionally |
| `MetaQuery.APIGroup` / `APIPublisher` / `QueryCategory` | 7 each | `<empty>` | `<null>` | a null/empty distinction only |
| `MetaQuery.APIVersion` | 4 | `beta` | `<null>` | a constant on non-API queries |
| `MetaQuery.RuntimeInfo.<presence>` | 7 | present | `<null>` | not stated in the symbol file |
| `MetaQuery.CaptionML.<presence>` (+`#captionML`) | 4 + 4 | present | `<null>` | the flat `Caption`, plus the language id |
| `MetaQueryDataItemLink.LinkOperator` | 4 | `=` | `<null>` | stated in BC's document; AL has no other operator |

`Caption` is two different situations, which is why it is tracked rather than called a defect in
one direction:

```
Query 777  Name='Role Center from Plans'  CaptionML='ENU=RoleCenter from Plans'
           symbol Caption='RoleCenter from Plans'
           BC's MetaQuery.Caption = 'Role Center from Plans'   runner = 'RoleCenter from Plans'
Query 8888 Name='Outbox Emails'  no CaptionML at all  symbol states no Caption
           BC's MetaQuery.Caption = 'Outbox Emails'            runner = <null>
```

On 777 BC's `Caption` property reports the object **name** while the runner reports the
**declared caption** — which is what both the symbol file and BC's own `CaptionML` say. On
8888/8889/8890 nothing is declared and BC falls back to `Name`. Both are derivable; which is
correct depends on what AL observes through this property, and that is worth settling before
changing anything. All of it is tracked on **#3798**.

<a id="xmlports"></a>
## XmlPorts

**The oracle is `MetaXmlPort(XmlDocument, CreateRequestForm, int, int,
RemoveItemsOnPageBasedOnLicenseAndApplicationArea)`** — an `XmlDocument` rather than an
`XmlNode`, and two trailing **delegates** the harness passes null. That null is safe by BC's own
body, which guards the only use with `if (createRequestForm != null && val != null)`, so it
leaves `RequestFormMetadata` unbuilt on **both** sides; `MetadataEquivalenceQueryXmlPortOracleTests`
measures that rather than resting on the decompile.

**This kind cannot fail the way pages did.** `MetaXmlPort`'s body is a switch over the
uppercased child element name ending in `throw new ArgumentException(name)`, so it cannot
silently ignore a document — and the runner-side projection depends on that refusal to catch a
mis-spelled element name. A test pins it.

### The runner derives no xmlport structure at all

The runner's whole knowledge of a precompiled xmlport is `BcAppSymbolCache.ObjectSymbol` —
`(Kind, Id, Name, Caption)`. `RecordPatches.AlXmlPortParser` fills `_parsedXmlPorts` from the
**corpus app's AL source text**, and `AlXmlPortMetadataRegistry` holds BC's **emit-captured**
schema for locally-compiled xmlports; a System Application xmlport is in neither. So
`TryBuildXmlPortMetadataEquivalenceXml` states two values, and that is the finding rather than
an omission. Using the registry instead would have been the same circularity queries had to
refuse.

### What the xmlport comparison found

4 xmlports, **322 differences across 43 members**, of which 296 are declared by entries this
step added — the rest were already covered by the `TranslationKey.*` and `MetaRuntimeInfo`
entries, which match on signature across every object kind. Two separable parts:

**182 of the 322 are one finding under two signatures.** `MetaXmlPort.Nodes.<presence>` and
`#nodes.<presence>`, 91 each: every `<Node>` element BC emits across the four xmlports is absent.
SymbolReference.json states an xmlport's `Id`, `Name`, `Properties` and `Variables` and **no node
tree at all** — measured, not inferred. Nine further members are computed *from* the nodes and go
green with them (`Schema`, `SchemaSet`, `SchemaTypeName`, `#typeNames`, `#xsdBuilder`,
`#mainPrefix`, `DefaultNamespace`), and two of those — `Schema` and `SchemaTypeName` — do not
merely differ but **throw `NullReferenceException`** when read on the runner's side. That is
#3510's shape seen from the derivation end.

This is **not** a claim that the tree is underivable. Nobody has measured whether it can be
reconstructed, and "the symbol file does not store it" is evidence about storage, never about
derivability — the mistake step 1 made about `DataColumnName` and `ControlGUID`.

**15 of them are properties the symbol file states verbatim and the runner throws away.**
`VisitSymbolContainer` keeps `TableNo`/`SingleInstance`/`Subtype` for `Codeunit` only; every
other kind gets `new ObjectSymbol(kind, id, name, caption, TargetObjectName:)`, so an xmlport's
whole `Properties` array is parsed and discarded:

```
XmlPort 9001  Direction sym=<none> bc=Both      Encoding sym=<none> bc=UTF-16  PreserveWhiteSpace sym=<none> bc=0
XmlPort 9862  Direction sym=Export bc=Export    Encoding sym=UTF8   bc=UTF-8   PreserveWhiteSpace sym=1      bc=1
XmlPort 9863  Direction sym=Export bc=Export    Encoding sym=UTF8   bc=UTF-8   PreserveWhiteSpace sym=1      bc=1
XmlPort 9864  Direction sym=Import bc=Import    Encoding sym=UTF8   bc=UTF-8   PreserveWhiteSpace sym=1      bc=1
```

Every xmlport that declares one has it stated, and the three that state none are the three where
BC answers its own default. A read-don't-guess fix, the same shape as `Extensible` in #3784.
`Permissions` needs a normalization (`tabledata "Security Group" = r` against BC's
`TableData Security Group=r`) and `UseRequestForm` needs AL's default rather than the CLR's.
All of it is tracked on **#3797**.

<a id="one-build-measured-three-evaluated-query-xmlport"></a>
### These two populations were measured across four builds, and the flag is still not blanket

`versionContingent` exempts an entry from the unused-entry check and nothing else. With only 7
queries and 4 xmlports, the temptation is to apply it to every entry — and doing so was
**measured wrong**.

A mutation making the xmlport projection state BC's own `Direction` verbatim — manufacturing
agreement, the precise failure these entries exist to prevent — **passed all 22 tests** with the
flag applied everywhere, and failed `No_allowlist_entry_has_gone_stale` the moment the flag came
off those two entries. The flag cannot hide a difference that exists; it does hide one that
stopped existing for the wrong reason.

So 21 of the 49 entries carry it — the members whose population depends on a per-object
*declaration*, where one object changing empties the entry. The other 28 are values BC writes
unconditionally for every object of the kind, so their population cannot shrink without the
derivation having changed, which is exactly what the stale check should report.

The population stability that would have justified the blanket application is real and was not
sufficient. Measured across the four bundles on the authoring box — 27.5.46862.53931,
28.1.49838.53910, 28.1.49838.54308 and 28.4.53241.54407 — both populations are **identical**:
same 7 and 4 ids, same property values, same 9/20/22/40 node counts. The harness runs on
unit-test legs only, which are exactly 27.5 and 28.4.

<a id="what-the-query-and-xmlport-measurement-does-not-cover"></a>
### What this measurement does not cover

**One app.** Only System Application carries either kind, and Base Application is excluded
(#3549). #3499's 356 query failures are BaseApp queries, so none of them is in this population —
which is why all 7 queries here built successfully and this measurement is not a fix for that
issue. What it contributes is the instrument: a runner-side null now reports as
`the runner built no MetaQuery design at all` against a named id, rather than as a stack trace
in a corpus run.

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
