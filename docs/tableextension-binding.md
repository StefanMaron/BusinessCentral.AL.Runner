# Which table a tableextension extends

A source tableextension extends the one table its `extends` clause resolves to (#5289, the table
twin of the pageextension rule in [pageextension-binding.md](pageextension-binding.md#which-page-an-extensions-extends-clause-names)).
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

A dependency's table is extended when the clause names **no** source table, and also when a bare
clause reaches a source table only through the global namespace or a `using` (see "Undecided
clauses"). A dependency's table carries its namespace in the symbol cache since #5224, but this
match does not read it yet (#5293 item 2), so the namespace written in front of a dependency's
name is not compared.

## Undecided clauses

The compiler resolves a bare clause in the file's own namespace first. If that namespace holds a
dependency's table of the name, which the runner cannot see, and the file also imports a same-named
source table, the extension extends the dependency's table (BC's compiler binds it: the field compiles
on that table and not on the source one). So a bare clause with no own-namespace source table and a
source hit through the global namespace or a `using` keeps the dependency's table of that name as well.
That is a superset, as before #5289: never lost from the table the compiler bound, and still also
attached to the source table when the compiler bound the dependency's. Corpus codeunit 69428
(`BareClauseFromTheBaseNamespaceExtendsTheBaseTableDespiteAnImportedLocalTable`) asserts the
right-table direction. Reading the compiler's own resolution would settle it exactly (#5296).

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
- A **dependency table's** namespace is carried (#5224), but the `extends` match does not read it, so
  two dependency tables sharing a name are not told apart, and a bare clause is not checked against
  the `using`s when the table is a dependency's (#5293 item 2; #5288 item 2 for pages, which still
  carry none).
- An undecided clause (above) still attaches to the source table when the compiler bound the
  dependency's. The compiler's own target would remove that (#5296). #5224 fixed the relation-name
  twin of the scoping gap, a table's own names rather than an `extends` clause.

## Measurement

Corpus codeunit 69428 (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#541) asks a real
service tier: this app's `Shipment Method` against Base Application's, one extension of each
spelled bare and one qualified, observing the trigger, the fields, the `OnValidate` of a field id
both extensions declare, the keys and the caption. `AlRunner.Tests/TableExtensionTargetNamespaceTests.cs`
pins each reader above without a service tier.
