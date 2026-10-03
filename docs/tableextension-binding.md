# Which table a tableextension extends

A source tableextension extends the one table its `extends` clause resolves to (#5289, the table
twin of the pageextension rule in [pageextension-binding.md](pageextension-binding.md#which-page-an-extends-clause-names)).
Until then the runner registered it against every table of that NAME, so where two tables share a
name in different namespaces, one source table and Base Application's, the extension of either ran
its `OnInsert` for both and added its fields and keys to both, and one extension's `modify(...)`
caption answered for both. Nothing was raised. Same shape whether the clause was bare or
namespace-qualified (measured on #5289's fixture, `main` at 8d5987b1).

## The rule

`RecordPatches.TryParseTableExtensionFile` records, per extension id, the clause's name, the
namespace written in front of it, and the declaring file's own namespace and `using`s
(`TableExtensionTarget`, `RecordPatches.TableExtensionTargets.cs`). From that, which source tables
the clause names:

- A **qualified** clause (`extends NS."Table"`) names the source table of that name in NS.
- A **bare** clause names the source table of that name in the extension's own namespace, which the
  compiler resolves first, and otherwise the one in the global namespace or in a namespace the file
  imports with `using` (the order `ResolveInFileScope` applies to a table's own names, #4133).

A dependency's table is extended exactly when the clause names **no** source table. A dependency's
table carries no namespace in the symbol cache, so the namespace written in front of a dependency's
name is not compared.

## Where it applies

The registries stay keyed by table name, and every reader asks which of the extensions of that name
extend ITS table (`ExtensionIdsForTable`, `ExtensionFieldsFor`, `ExtensionKeysFor`,
`ExtensionSourceInfoFor`). Readers: the extension instances registered on a record, the flags of
`DefinedTriggers`, the OnValidate/OnLookup wiring of extension fields, the field and key merge into
the table's metatable, the `modify(...)` deltas, the choice between BC's own table document and the
derivation, and the maps from an extension to its base table (`ExtensionBaseObjectIds`,
`TableExtensionBaseTableIds`, `ExtensionIdsOfBaseObject`).

A table's field and key lists keep the old name-pooled answer unless an extension of its name
targets another table. Only then are they built from each extension's own contribution, which also
keeps a field id that two extensions of different tables both declare: the pooled list de-duplicates
by id.

## What it does not decide

- A **precompiled** dependency's tableextension states a name only (its symbol has no namespace), so
  it is registered with no target and keeps matching every table of that name, source or dependency.
  The page twin is #5288 item 3.
- A **dependency table's** own namespace is unknown, so two dependency tables sharing a name are not
  told apart, and a bare clause is not checked against the `using`s when the table is a dependency's.
  Reading it needs `BcAppSymbolCache` to carry the namespace, which changes the symbol payload and
  needs a cache version bump (#5288 item 2 for pages).
- A bare clause from a namespace that holds a **dependency** table of the name, with a `using` that
  imports a source table of that name, reads as the source table; the compiler takes the own
  namespace (#5224).

## Measurement

Corpus codeunit 69428 (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#541) asks a real
service tier: this app's `Shipment Method` against Base Application's, one extension of each
spelled bare and one qualified, observing the trigger, the fields, the `OnValidate` of a field id
both extensions declare, the keys and the caption. `AlRunner.Tests/TableExtensionTargetNamespaceTests.cs`
pins each reader above without a service tier.
