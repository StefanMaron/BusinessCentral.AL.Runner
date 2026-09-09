# Resolving an `Enum`-typed field's option metadata

How the runner answers `MetaField.EnumTypeId` / `EnumTypeName`, and the option metadata BC
reads alongside them, for a table field declared `Enum "X"`. Issue #3594.

## The two halves, and why they ship together

Reading the value was never the hard part. `TypeDefinition.Subtype.Id` in
`SymbolReference.json` carries it, and #3545 measured that the presence of `Subtype.Id` and the
presence of BC's own emitted `EnumTypeId` agree on **3,848 of 3,848** table-declared field
observations across four BC builds.

Stating it is the hard part. A `MetaField` that declares an `EnumTypeId` makes BC build an
`NCLFieldEnumMetadata` for the field instead of a plain `NCLOptionMetadataWithCaptions`, and
every accessor on that object — `OptionString`, `Options`, `OrdinalValues`, `GetNames`,
`GetCaptionFromIndex` — funnels through one virtual:

```
NCLFieldEnumBaseMetadata.GetAppGroupAwareEnumMetadata()      // caches per app group
  -> NCLFieldEnumMetadata.GetEnumMetadataFromMetadataProvider()
       -> NavGlobal.MetadataProvider.GetEnumMetadata(enumId)
            -> NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, id)
                 -> throw new NavMetadataNotFoundException(ObjectType.Enum, id)
```

That last lookup is one the runner never populated for `Enum` objects. Tables, pages, reports,
queries, xmlports and permission sets all get entries from
`RecordPatches.NclMetadataCachePopulator`; enums never did, because AL enums are served through
a different hook entirely (`NCLEnumMetadata.Create(int)` →
`BcRuntime.NCLEnumMetadata_CreateByIdAlAware`), and nothing on the field path calls it.

So the id was only safe to state once the object it names could be found. Measured on the
corpus at pin `c9d5f656`, stating it alone:

```
EXEC-FAIL: out-of-scope: Field (virtual table 2000000041) — not-yet-implemented —
field-virtual-table: GetFieldRecordBuffer threw for table 1366 field:
NavMetadataNotFoundException: The metadata object Enum 8889 was not found.
```

The abort took codeunit 2 `Company-Initialize` with it, so the app reported `EXEC-FAIL` with
**0 of 3,112 tests run** — a total failure, not a degradation.

## What the fix does

**1. Redirect the lookup at data the runner has.**
`NCLFieldEnumMetadata.GetEnumMetadataFromMetadataProvider()` is Cecil-rewritten to
`BcRuntime.NCLFieldEnumMetadata_GetEnumMetadataFromRegistry`, which answers from
`AlEnumMetadataRegistry` — the same registry `NCLEnumMetadata.Create(int)` already uses, holding
the values, ordinals, captions and interface implementations of every source-compiled enum and
of every precompiled dependency's enums (`RecordPatches.AddBcAppPath` loads the latter from each
`.app`'s symbols).

This substitutes the lookup's **data source**, not its outcome. An enum id nothing declares
still raises BC's own `NavMetadataNotFoundException` — which is required, not incidental:
`NCLMetadata.TryGetMetaApplicationObject` is implemented as a `try`/`catch` over that exact
exception type, so "the object is not there" is only answerable as `false` when it arrives as
one. Returning `NCLOptionMetadata.Default` instead would make an unknown enum read as a
valueless one, the silent fake `loud-failures.md` forbids.

Same shape and same cause as #1896's `NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions`
rewrite (`AlRunner/Patches/PageEnumFieldMetadataPatches.cs`): a by-id `Enum` lookup at a
consumption point the runner never populated.

**2. Register BC's own platform enums.**
The redirect alone is not enough. **System enums** — ids `2000000001`..`2000000017` — are
declared by the platform, appear in no app's `SymbolReference.json`, and so were in no registry.
Base Application table `2000000132` has a field typed by enum `2000000002` "Entity Text
Scenario", which is why the corpus still aborted with the redirect in place, now naming that id
rather than 8889.

`BcRuntime.EnsureSystemEnumsRegistered` reads them from BC's own inventory —
`PlatformMetadataProvider.GetSystemEnums()` for the ids and names, `GetEnumALCodeById(id)` for
the AL source Microsoft ships for each — and parses the `value(...)` declarations out of that
source. The values are therefore Microsoft's own declaration, not anything this runner invents.

### What the system-enum population actually is

Measured on BC 28.1.49838.53910:

| | |
|---|---|
| system enums exposed | 16 (`2000000001`–`2000000015`, `2000000017`) |
| `value(...)` declarations across all of them | 57 |
| declarations using a `"quoted"` name | 27 |
| `Caption = '...'` occurrences | 73 |
| enums declaring **no** values | 3 (`2000000001`, `2000000002`, `2000000006`) |

The three that declare nothing are extensible enums an app is expected to extend. An empty
option set is the correct answer for them, not a parse failure — and `2000000002`, the one that
aborted the corpus, is among them.

The AL shape is uniform: `value(<ordinal>; <Name>)` with an optional `{ Caption = '...'; }`
body, the name bare or double-quoted, and no `Locked` or comma-suffixed caption forms in the
whole population. `BcRuntime.ParseSystemEnumValues` matches exactly that, over source with
comments stripped — BC's own enum sources are heavily doc-commented, and the word `value`
appears in prose. `AlRunner.Tests/SystemEnumAlSourceParseTests.cs` pins each of these
properties, including the two opposite-pair cases: a comment mentioning `value(...)` must not
become a member, and a comment marker inside a caption string must survive.

Registration is lazy (first field-enum resolution), once per process, never overwrites an
entry an app already registered, and never throws: a BC build that does not expose this
inventory leaves the registry untouched, and a field typed by a system enum then fails the same
loud way it did before. That is a missing optimisation, not a silent wrong answer.

## Result

At corpus pin `c9d5f656`, BC 28.1.49838.53910:

| | tests | pass | exec-fail |
|---|---|---|---|
| before (baseline) | 3,112 | 3,112 | 0 |
| stating the id alone | 0 | 0 | **1** |
| after (both halves) | 3,112 | 3,112 | 0 |

and `MetaField.EnumTypeId` moves from a declared difference to an agreeing one, so its entry is
deleted from `tests/expectations/metadata-equivalence/allowlist.json` and
`MetadataEquivalenceHarnessTests` asserts `AssertReaderAgrees` for it rather than a constant `0`.

`MetaField.EnumTypeName` remains declared under #3568 for an unrelated reason: BC leaves it
`null` on the ~1,800 fields that are not enum-typed where the runner's `MetaField` constructor
supplies the empty string.

## Two failure modes worth keeping in mind

**The redirect and the system-enum registration are separately load-bearing**, and removing
either aborts the corpus with a *different* id — 8889 (an app enum, table 1366) without the
Cecil rewrite, 2000000002 (a platform enum, table 2000000132) without
`EnsureSystemEnumsRegistered`. That asymmetry is what distinguishes the two guards.

**`enumId` must be read as a field, never through the `Id` property.**
`NCLFieldEnumBaseMetadata.Id` is `GetAppGroupAwareEnumMetadata().Id`, which calls the very
method the rewrite replaces — reading it inside the replacement recurses. The Cecil block
refuses to install if the `private readonly int enumId` field is not present, because the
replacement body would otherwise have no way to learn which enum the field names.

## BC-version stability

`NCLFieldEnumMetadata.GetEnumMetadataFromMetadataProvider` is byte-identical between BC 27.0 and
BC 28.4 — `compare_symbols` reports `signatureChanged: false`, `bodyChanged: false`, zero changed
blocks — so the rewrite target is not a moving one across the supported range.
