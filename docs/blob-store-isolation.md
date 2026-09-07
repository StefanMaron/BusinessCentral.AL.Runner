# BLOB store isolation: why a database-backed row must not share a NavBLOB with the record that inserted it

Derivation behind `AlRunner/Patches/BlobStoreIsolationPatches.cs`. The code carries the claims
and the citations; this file carries the evidence they rest on and the reasoning that produced
them. Issues #1751 and #1765.

<a id="the-divergence"></a>

## The divergence (#1751)

Both halves are measured against a real service tier by corpus codeunit 60940
"Test Blob Uncomm Isolation", green on BC 27.5 and 28.3:

* **Database-backed record** — a BLOB written through `CreateOutStream` with **no** following
  `Modify()` is invisible to the stored row. A second `Record` instance that `Get()`s the row
  reads it empty, and a re-`Get()` on the writing instance discards the write.
* **`temporary` record** — the very same write **is** visible through the store. `Get()` reads
  the unpersisted bytes straight back, and so does a second variable sharing the buffer via
  `Copy(..., true)`.

The corpus file was originally written asserting isolation for **both** shapes; real BC rejected
exactly the two temporary assertions and passed every control. So this is not a BC bug to
normalise away — it is two different contracts, and a blanket copy at the store boundary would
fix one by breaking the other.

<a id="why-the-runner-leaks"></a>

## Why the runner leaked the database case

Every table in the runner is backed by Ncl's `TempTableDataProvider` (see
`RecordPatches.NavDataAccessSource_GetDataAccessForTable`). That provider is the same code real
BC runs for `temporary` records, so the runner inherited the temporary contract for
database-backed tables too. Concretely, in Ncl:

```
TempTableDataProvider.Insert
  items = recordBuffer.ToArray()                  // BLOB copied BY REFERENCE
  new TempTableRecordBuffer(metaTable, items)
  value.CloneBlobs(recordBuffer)                  // clones ONLY dirty BLOBs
DataAccess.InsertAsync
  CreateNewBufferFromOutputBufferTransferBlobValuesFromOldRecord
    newBuffer[i] = oldRecord.GetChangedFieldValue(i)   // SAME object again
```

A BLOB that carried no value at `Insert` is not dirty, so `CloneBlobs` skips it and the stored
row keeps the record's own `NavBLOB` — which the record then goes on using.
`Content.CreateOutStream(o); o.WriteText(...)` mutates that one object and the stored row
changes with it. On real BC this only ever happens for temporary records, because a
database-backed row lives in SQL and there is no shared object to mutate.

<a id="the-insert-fix"></a>

## The Insert-side fix

Give the store its own `NavBLOB` at `Insert`, but **only** for the providers that stand in for
SQL. Two Cecil prepends (see `NclCecilRewrite`):

1. `TempTableDataProvider.Insert` → `OnBeforeStoreInsert(provider)` records, for the duration of
   this insert, whether the provider is database-backed.
2. `TempTableRecordBuffer.CloneBlobs` → `DetachStoredBlobs(stored)` deep-copies every `NavBLOB`
   the stored row holds, so it shares none with the record.

Prepends, not replacements: Ncl's own `CloneBlobs` body still runs afterwards and re-clones the
dirty BLOBs exactly as before, so the write-before-`Insert` shape is untouched. For a temporary
provider the flag is false, nothing is detached, and the aliasing real BC exhibits is preserved
verbatim.

`Modify()` needs no equivalent. `TempTableDataProvider.Modify` itself constructs no `NavBLOB`;
the construction is in `TempTableDataProvider.ModifyAllTrees`, which `Modify` calls and is its
only caller — and it stores `new NavBLOB(navBLOB.GetBytes(), useContentInstance: true)`, a
distinct `NavBLOB`. So a second uncommitted write after `Modify()` does not reach the row.
Verified by probe before the patch was written, and pinned by 60940's committed controls.

<a id="the-cloneblobs-call-count"></a>

## The single-call-site count `_currentInsertIsDatabaseBacked` rests on

The latch set by the `Insert` prepend and read by the `CloneBlobs` prepend is never reset. That
is safe only because `CloneBlobs` has exactly **one** call site in the whole of Ncl:
`TempTableDataProvider.Insert`, called synchronously. Nothing else can observe a stale value,
and every insert sets it afresh.

That is measured, not assumed — the call sites were counted by scanning
`Microsoft.Dynamics.Nav.Ncl.dll` (28.1) with Cecil, not read off a decompile. The whole patch's
correctness rests on the count, so **re-check it if a future BC version changes shape**: a second
`CloneBlobs` caller outside `Insert` would read a flag left over from an unrelated insert.

<a id="rename"></a>

## The Rename boundary (#1765)

Follow-up measurement to #1751/60940: does `Rename()` have the same BLOB store-aliasing boundary
as `Insert()`/`Modify()`? Corpus 60944 "Test Blob Rename Isolation" (green on BC 27.5 and 28.3)
answers **no**, and the shape is not symmetric with Insert/Modify at all:

* **Database-backed record** — `Rename()` re-persists the record variable's whole current buffer
  under the new key (proven by the scalar-field control: an uncommitted plain-`Text` write also
  survives a `Rename`). A BLOB committed earlier with `Modify()` survives an unrelated
  `Rename()` intact — expected, needs no patch, and the runner already matches it via Ncl's own
  `TempTableDataProvider.Modify` (`Rename` routes through the very same method with
  `primaryKeyChanged=true`).
* **`temporary` record** — an uncommitted write (never `Modify()`'d) still leaks through a
  `Rename`, unsurprising and already covered by the temporary half of #1751's aliasing. But a
  BLOB that **was** committed with `Modify()` **before** the `Rename()` call is **lost** —
  `CalcFields()` after `Get()` on the renamed row reads `HasValue()` = false, even though the
  exact same Insert→write→Modify sequence **without** the `Rename()` round-trips correctly
  (60940's temporary positive control). Measured, not assumed: this is the one genuine surprise
  of 60944, identical on both BC versions.

### Why the runner does not reproduce the loss on its own

`TempTableDataProvider.Modify` is also what `Rename()` calls
(`RecordImplementation.RenameRecordAsync` builds a rekeyed buffer and calls
`dataAccess.ModifyAsync`, same as a plain `Modify`). Its private `ModifyAllTrees` only replaces a
BLOB field in the row being stored when Ncl's own dirty-tracking calls that field "changed"
(`GetChangedFieldValue(j) != null && navBLOB.IsDirty`) — for a `Rename` that does not touch the
BLOB, it is not dirty, so the row keeps whatever `NavBLOB` object the pre-rename baseline
already held.

For a `temporary` table that object legitimately still carries the bytes `Modify()` persisted —
Ncl's own store faithfully keeps them. The runner's `FlowFieldPatches.LoadBlobField` (added for
#1724) then finds that row by primary key on the next `CalcFields()` and loads it — correctly, by
the runner's own read of Ncl's state, but **not** what real BC's temporary-table blob JIT-load
does once a `Rename` has run. Real BC's mechanism is closed; the measured *result* is what corpus
60944 pins, and the patch reproduces the result.

<a id="the-rename-fix"></a>

## The Rename-side fix, and why it is keyed by the row object

A third Cecil prepend, on the same `TempTableDataProvider.ModifyAllTrees` that #1751's Insert
path already names as `Modify`'s real BLOB-write path. When, for a **non**-database-backed
(temporary) provider, this call is a rename (`workTableBuffer` is a fresh buffer, not the same
object as the removed `storedTableBuffer`) **and** a BLOB field is **not** dirty on this call (it
is carrying over a value from before the rename, not a fresh write), that field index on the
*row* (`workTableBuffer`, the object Ncl adds to the AVL tree) is marked as ineligible for
`FlowFieldPatches.LoadBlobField`'s by-primary-key reload fallback.

**Keyed by the row object, not the `NavBLOB` value object.** A first attempt marked the `NavBLOB`
instance itself, but `Get()`'s own `Find()`-based read materialises a **different** `NavBLOB`
instance for `parentBuffer.ReadOnlyBuffer` than the one `TempTableDataProvider.TryGetValue`
returns from the tree directly (same bytes, different object identity) — so a value-keyed marker
silently failed to catch the one path (`LoadBlobField`'s Step 1, sizing the JIT-load placeholder
from `original.ALLength`) that made `ALHasValue` true regardless of whether Step 4's byte-copy
ran. The *row* object, in contrast, is verified stable: it is literally the same
`TempTableRecordBuffer` that `list[k].Add(workTableBuffer)` inserts into the tree and
`TryGetValue` returns back out.

A future successful `Modify()` calls the same method again with a fresh
`workTableBuffer`/`storedTableBuffer` pair (Ncl always constructs a new `TempTableRecordBuffer`
per `Modify`), so the old row object holding the marker is simply never looked up again;
`ConditionalWeakTable` lets it be collected once nothing else references it.

Scoped to non-database-backed providers only: the database-backed shape
(`test3/Blob_CommittedWrite_Rename_SecondInstanceGet_ReadsWrittenBytes`) must keep working — and
does, because `MarkDatabaseBacked()` means `_databaseBackedProviders.TryGetValue` succeeds for it
and the method never marks anything for that provider.

### Mirroring Ncl's own dirty predicate exactly

`OnModifyAllTrees` reproduces Ncl's own test for "this call writes the BLOB"
(`TempTableDataProvider.ModifyAllTrees`: `navBLOB != null && navBLOB.IsDirty`).
`GetChangedFieldValue(j)` being non-null is **not** enough on its own — a `Rename`'s rekeyed
buffer carries a non-null `NavBLOB` for every field (built via `recordBuffer.ToArray()`), but
that object's own `IsDirty` flag is false unless this call is the one that actually wrote new
bytes.

### Why the prepend does not forward `primaryKeyChanged`

The Cecil prepend receives `this, mutableRecordBuffer, workTableBuffer, storedTableBuffer`; the
trailing `primaryKeyChanged bool` is not forwarded. Renames are detected via `workTableBuffer`
being a distinct object from `storedTableBuffer`, which Ncl's own `Modify()` guarantees is true
if and only if `primaryKeyChanged` was true — see `TempTableDataProvider.Modify`'s two branches.
