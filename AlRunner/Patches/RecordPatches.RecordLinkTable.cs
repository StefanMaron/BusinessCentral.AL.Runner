// RecordPatches.RecordLinkTable — the AL link surface, backed by the Record Link table.
//
// Real BC has ONE store for record links: table 2000000068. `Rec.AddLink`, `HasLinks`,
// `DeleteLink`, `DeleteLinks` and `CopyLinks` are reads and writes of that table, and AL
// that opens `Record "Record Link"` sees exactly what those methods wrote. Issue #3378
// measured the runner keeping two stores instead, so anything crossing between the two
// surfaces answered 0 — including the System Application's own `Record Link Impl.`, which
// reaches the platform through a RecordRef built from a Variant.
//
// See docs/scope.md § "RecordLink" for the boundary this replaced.

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int RecordLinkTableId = 2000000068;

    private const string RecordLinkSurface = "Record Link (2000000068) link store";

    /// <summary>Column slots of the Record Link table, resolved once per metatable.</summary>
    private sealed class RecordLinkColumns
    {
        internal NCLMetaTable Meta = null!;
        internal NavValue[] Empty = null!;
        internal NCLMetaField LinkId = null!;
        internal NCLMetaField RecordId = null!;
        internal NCLMetaField Url1 = null!;
        internal NCLMetaField Description = null!;
        internal NCLMetaField? Company;
        internal NCLMetaField? UserId;
        internal NCLMetaField? Created;
    }

    private static readonly ConditionalWeakTable<object, RecordLinkColumns> _recordLinkColumns = new();

    private static RecordLinkColumns ResolveRecordLinkColumns(NCLMetaTable meta)
        => _recordLinkColumns.GetValue(meta, static m =>
        {
            var table = (NCLMetaTable)m;
            var byName = new Dictionary<string, NCLMetaField>(StringComparer.OrdinalIgnoreCase);
            var empty = new NavValue[table.FieldCount];
            for (var i = 0; i < table.FieldCount; i++)
            {
                var f = table.GetFieldByIndex(i);
                byName[f.FieldName] = f;
                if (f.FieldIndex >= 0 && f.FieldIndex < empty.Length)
                    empty[f.FieldIndex] = f.EmptyValue;
            }

            // Loud, never silent: a renamed column here would otherwise produce link rows
            // that insert cleanly and can never be found again — the exact silent-fake shape
            // loud-failures.md forbids. The four below are the ones every read filters or
            // asserts on; the three optional ones only carry provenance.
            NCLMetaField Required(string name)
                => byName.TryGetValue(name, out var f)
                    ? f
                    : throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        "Record.AddLink",
                        $"the Record Link table has no '{name}' column — BC metadata shape changed",
                        "record-link");

            return new RecordLinkColumns
            {
                Meta = table,
                Empty = empty,
                LinkId = Required("Link ID"),
                RecordId = Required("Record ID"),
                Url1 = Required("URL1"),
                Description = Required("Description"),
                Company = byName.TryGetValue("Company", out var c) ? c : null,
                UserId = byName.TryGetValue("User ID", out var u) ? u : null,
                Created = byName.TryGetValue("Created", out var cr) ? cr : null,
            };
        });

    /// <summary>
    /// The Record Link table's own TempTableDataProvider — the same one an AL
    /// <c>Record "Record Link"</c> reaches, because it comes out of the same
    /// <c>_dataAccessByTable</c> cache <c>GetDataAccessForTableCore</c> populates. That
    /// sameness is the whole fix; a second DataAccess here would re-create #3378.
    /// </summary>
    /// <param name="create">false for read-only callers, so asking whether a record has
    /// links never materialises an empty table.</param>
    private static (object Provider, RecordLinkColumns Columns)? GetRecordLinkStore(bool create)
    {
        var source = ResolveSkeletonDataAccessSource();
        if (source == null) return null;

        object? dataAccess = null;
        if (_dataAccessByTable.TryGetValue(source, out var existing))
            existing.TryGetValue(RecordLinkTableId, out dataAccess);

        if (dataAccess == null)
        {
            if (!create) return null;
            var meta0 = EnsureTableInMetadataCache(RecordLinkTableId);
            if (meta0 == null) return null;
            var perTable = _dataAccessByTable.GetValue(source,
                static _ => new ConcurrentDictionary<int, object>());
            dataAccess = perTable.GetOrAdd(RecordLinkTableId,
                _ => _mCreateTempDataAccess!.Invoke(source, new object[] { meta0 })!);
        }

        var provider = GetDataProvider(dataAccess);
        if (provider == null || provider.GetType().Name != "TempTableDataProvider") return null;

        var meta = provider.GetType()
            .GetField("table", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(provider) as NCLMetaTable;
        if (meta == null) return null;

        return (provider, ResolveRecordLinkColumns(meta));
    }

    /// <summary>Every Record Link row currently stored, as the provider holds them.</summary>
    private static List<NavValue[]> ReadRecordLinkRows(object provider)
    {
        var rows = new List<NavValue[]>();
        if (provider.GetType().GetField("primaryTree", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(provider) is IEnumerable tree)
            foreach (var row in tree)
                if (row is TempTableRecordBuffer buffer)
                    rows.Add(buffer.ToArray());
        return rows;
    }

    /// <summary>Replace the whole row set. Used by the delete paths: the store is a handful
    /// of rows, so rewriting it is cheaper to get right than reaching for the provider's own
    /// delete overload, and it reuses the rollback path's already-proven helpers.</summary>
    private static void ReplaceRecordLinkRows(object provider, RecordLinkColumns columns, List<NavValue[]> rows)
    {
        NoteRecordLinkWrite();
        ClearProviderInPlace(provider);
        InsertRows(provider, columns.Meta, rows.ToArray());
    }

    /// <summary>Take the pre-write image of the Record Link table before this store touches it.
    /// The store writes through <see cref="InsertRows"/> / <see cref="ClearProviderInPlace"/>,
    /// which are the ROLLBACK path's own helpers and therefore deliberately do not notify it —
    /// so without this call an <c>asserterror</c> after <c>Rec.AddLink(...)</c> would leave the
    /// link row behind while an AL <c>Insert</c> into the same table rolls back, which is two
    /// writers of one table disagreeing about one invariant.</summary>
    private static void NoteRecordLinkWrite() => NoteTransactionWriteForTable(RecordLinkTableId);

    private static byte[]? SlotBytes(NavValue[] row, NCLMetaField field)
    {
        var idx = field.FieldIndex;
        if (idx < 0 || idx >= row.Length) return null;
        var v = row[idx];
        // A slot with no value is genuinely "no RecordId", and belongs to no record. Anything
        // else is BC's own encoding of a stored RecordId and is left to throw: a value that
        // will not serialise would otherwise silently read as "not this record's links".
        if (v == null || v.IsNull) return null;
        return v.GetBytes();
    }

    private static bool RowBelongsTo(NavValue[] row, RecordLinkColumns columns, byte[] parentKey)
    {
        var bytes = SlotBytes(row, columns.RecordId);
        return bytes != null && bytes.AsSpan().SequenceEqual(parentKey);
    }

    /// <summary>BC's own encoding of a RecordId, which is what the stored column holds — so
    /// "is this row this record's" is a byte comparison, never a re-parse.</summary>
    private static byte[]? ParentKeyBytes(object? record, RecordLinkColumns columns)
    {
        if (record is not NavRecord rec) return null;
        return NavValue.CreateNavValueFromObject(columns.RecordId, rec.ALRecordId).GetBytes();
    }

    private static int ReadLinkId(NavValue[] row, RecordLinkColumns columns)
    {
        var idx = columns.LinkId.FieldIndex;
        if (idx < 0 || idx >= row.Length || row[idx] == null) return 0;
        return (int)row[idx].ToDecimal();
    }

    private static string ReadText(NavValue[] row, NCLMetaField field)
    {
        var idx = field.FieldIndex;
        if (idx < 0 || idx >= row.Length || row[idx] == null) return string.Empty;
        return row[idx].ToString() ?? string.Empty;
    }

    /// <summary>Build one Record Link row. Layout mirrors what BC's own AddLink writes:
    /// the caller's URL and description, a fresh AutoIncrement Link ID, the parent's
    /// RecordId, and this session's company. Type stays at the column's own default, which
    /// is ordinal 0 = Link — the type AddLink creates.</summary>
    private static NavValue[] BuildRecordLinkRow(
        RecordLinkColumns columns, int linkId, NavValue parentRecordId, string url, string description)
    {
        var values = (NavValue[])columns.Empty.Clone();

        void Set(NCLMetaField? f, object? value)
        {
            if (f == null || value == null) return;
            var idx = f.FieldIndex;
            if (idx < 0 || idx >= values.Length) return;
            values[idx] = value is NavValue already ? already : NavValue.CreateNavValueFromObject(f, value);
        }

        Set(columns.LinkId, linkId);
        Set(columns.RecordId, parentRecordId);
        Set(columns.Url1, url);
        Set(columns.Description, description);
        Set(columns.Company, ReadSkeletonCompanyIdentity().Name);
        // Created / User ID carry provenance only: no AL surface on Record reads them back,
        // and BC fills them from the session. Written when the columns resolve so a test
        // reading the table sees a plausible row, skipped rather than guessed when they do not.
        Set(columns.Created, DateTime.UtcNow);
        Set(columns.UserId, SkeletonUserId());

        return values;
    }

    /// <summary>The next "Link ID". Taken from the SAME AutoIncrement counter
    /// <c>NavRecord.ALInsertAsync</c> uses, so a link added through the AL surface and a
    /// Record Link row an AL <c>Insert(true)</c> writes can never be handed the same primary
    /// key — the collision #2289 records for the other provider-level writer.</summary>
    private static int NextRecordLinkId(RecordLinkColumns columns)
        => (int)AlRunner.BcRuntime.TakeAutoIncrementValue(RecordLinkTableId, columns.LinkId.FieldNo);

    // ── The AL surface, as reads and writes of the table ──────────────────────────────

    internal static int RecordLinkStore_Add(object? record, string url, string description)
    {
        // Loud, never silent (loud-failures.md): BC's AddLink returns the Link ID of the row it
        // created, and AL tests `LinkId > 0` for success — so a 0 here is a fake success that
        // the caller cannot distinguish from a real one. The old dictionary store's own comment
        // said as much. Every branch below that cannot produce a row raises instead.
        var store = GetRecordLinkStore(create: true)
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "Record.AddLink",
                $"the Record Link table ({RecordLinkTableId}) has no in-memory store on this "
                + "session — the skeleton exposes no DataAccessSource, or its metatable did not build",
                "record-link");
        var (provider, columns) = store;

        if (record is not NavRecord rec)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "Record.AddLink",
                $"a link was added to a {record?.GetType().Name ?? "null"}, which carries no RecordId "
                + "to own it",
                "record-link");
        var parentValue = NavValue.CreateNavValueFromObject(columns.RecordId, rec.ALRecordId);

        var nextId = NextRecordLinkId(columns);
        NoteRecordLinkWrite();
        InsertRows(provider, columns.Meta,
            new[] { BuildRecordLinkRow(columns, nextId, parentValue, url, description) });
        return nextId;
    }

    internal static bool RecordLinkStore_HasLinks(object? record)
    {
        var store = GetRecordLinkStore(create: false);
        if (store == null) return false;
        var (provider, columns) = store.Value;
        var key = ParentKeyBytes(record, columns);
        if (key == null) return false;

        foreach (var row in ReadRecordLinkRows(provider))
            if (RowBelongsTo(row, columns, key))
                return true;
        return false;
    }

    internal static void RecordLinkStore_DeleteAll(object? record)
    {
        var store = GetRecordLinkStore(create: false);
        if (store == null) return;
        var (provider, columns) = store.Value;
        var key = ParentKeyBytes(record, columns);
        if (key == null) return;

        var rows = ReadRecordLinkRows(provider);
        var kept = rows.Where(r => !RowBelongsTo(r, columns, key)).ToList();
        if (kept.Count == rows.Count) return;
        ReplaceRecordLinkRows(provider, columns, kept);
    }

    internal static void RecordLinkStore_DeleteOne(object? record, int linkId)
    {
        var store = GetRecordLinkStore(create: false);
        if (store == null) return;
        var (provider, columns) = store.Value;
        var key = ParentKeyBytes(record, columns);
        if (key == null) return;

        var rows = ReadRecordLinkRows(provider);
        // BC's DeleteLink on a Link ID that is not this record's is a no-op, not an error.
        var kept = rows.Where(r => !(RowBelongsTo(r, columns, key) && ReadLinkId(r, columns) == linkId)).ToList();
        if (kept.Count == rows.Count) return;
        ReplaceRecordLinkRows(provider, columns, kept);
    }

    internal static void RecordLinkStore_Copy(object? from, object? to)
    {
        var store = GetRecordLinkStore(create: false);
        if (store == null) return;
        var (provider, columns) = store.Value;

        var srcKey = ParentKeyBytes(from, columns);
        if (srcKey == null || to is not NavRecord dst) return;
        var dstValue = NavValue.CreateNavValueFromObject(columns.RecordId, dst.ALRecordId);

        var rows = ReadRecordLinkRows(provider);
        // Snapshot before writing: src may be dst (a record copied onto itself), and the
        // copies must not themselves be copied.
        var toCopy = rows.Where(r => RowBelongsTo(r, columns, srcKey)).ToList();
        if (toCopy.Count == 0) return;

        // Copying creates NEW rows, so each copy gets a fresh Link ID — unlike MoveLinks
        // below, which relocates the existing rows and keeps theirs.
        var built = toCopy
            .Select(r => BuildRecordLinkRow(
                columns, NextRecordLinkId(columns), dstValue,
                ReadText(r, columns.Url1), ReadText(r, columns.Description)))
            .ToArray();
        NoteRecordLinkWrite();
        InsertRows(provider, columns.Meta, built);
    }

    internal static void RecordLinkStore_Move(object? from, object? to)
    {
        var store = GetRecordLinkStore(create: false);
        if (store == null) return;
        var (provider, columns) = store.Value;

        var srcKey = ParentKeyBytes(from, columns);
        if (srcKey == null || to is not NavRecord dst) return;
        var dstValue = NavValue.CreateNavValueFromObject(columns.RecordId, dst.ALRecordId);

        var rows = ReadRecordLinkRows(provider);
        var moved = false;
        foreach (var row in rows)
        {
            if (!RowBelongsTo(row, columns, srcKey)) continue;
            var idx = columns.RecordId.FieldIndex;
            if (idx < 0 || idx >= row.Length) continue;
            // Moving keeps the row and its Link ID; only the owner changes.
            row[idx] = dstValue;
            moved = true;
        }
        if (moved) ReplaceRecordLinkRows(provider, columns, rows);
    }

    /// <summary>Does any record of <paramref name="tableId"/> have a link? Answered by
    /// reading the table id back out of each row's own RecordId, so it cannot report
    /// another table's links.</summary>
    internal static bool RecordLinkStore_TableHasLinks(int tableId)
    {
        var store = GetRecordLinkStore(create: false);
        if (store == null) return false;
        var (provider, columns) = store.Value;

        foreach (var row in ReadRecordLinkRows(provider))
        {
            var idx = columns.RecordId.FieldIndex;
            if (idx < 0 || idx >= row.Length || row[idx] == null || row[idx].IsNull) continue;
            if (RecordIdTableNo(row[idx]) == tableId) return true;
        }
        return false;
    }

    /// <summary>The table number inside a stored RecordId value, rebuilt through BC's own
    /// byte round-trip rather than re-derived.</summary>
    private static int RecordIdTableNo(NavValue value)
    {
        var bytes = value.GetBytes();
        return NavRecordId.CreateFromBytes(bytes, 0, bytes.Length).TableNo;
    }

    /// <summary>The session's user, for the Record Link row's provenance column. Read off the
    /// skeleton session rather than invented, and skipped when it exposes none.</summary>
    private static string? SkeletonUserId()
    {
        var session = AlRunner.BcRuntime.SkeletonSession;
        if (session == null) return null;
        foreach (var name in new[] { "UserId", "UserName" })
        {
            var p = session.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p?.GetValue(session) is string s && s.Length > 0) return s;
        }
        return null;
    }
}
