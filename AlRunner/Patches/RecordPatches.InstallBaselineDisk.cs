// RecordPatches.InstallBaselineDisk — cross-PROCESS persistence of the dependency+company
// install baseline that RecordPatches.InstallBaseline.cs captures in memory.
//
// WHY
//   #1867 made the dependency Install triggers + Company-Initialize run ONCE per process and
//   cached the resulting InstallBaselineSnapshot in a process-lifetime dictionary keyed by
//   InstallTriggerRunner.CurrentDependencySetKey(). That removes the repeat inside one
//   process, but every NEW process still pays it in full: measured at 5.9s of a 23.3s warm
//   single-fixture run (96.1% of the app group's run_ms), ~177s of the CI unit-test step, and
//   8-10s each on the corpus / runner-extras suites. The computation is deterministic given
//   (dependency assembly set, runner build, BC version), so it can be computed once and
//   reloaded from disk.
//
// WHAT IS PERSISTED
//   Exactly the four things InstallBaselineSnapshot holds — table rows, isolated storage,
//   record links, auto-increment counters — MINUS the self-populating virtual system tables
//   (see IsSelfPopulatingVirtualTableId). Everything else round-trips through BC's OWN
//   NavValue byte codec (NavValue.GetBytes / NavValue.CreateNavValueFromBytes, the pair the
//   service tier itself uses to move field values in and out of binary), so the runner is not
//   inventing a second, divergent encoding of AL values.
//
// WHY THE VIRTUAL TABLES ARE EXCLUDED — deliberate, not an oversight
//   AllObj (2000000038), AllObjWithCaption (2000000058), Field (2000000041) and their
//   siblings are not install-trigger output at all: they are process-wide projections of the
//   assemblies currently loaded, and RecordPatches.GetDataAccessForTableCore re-populates
//   each of them on EVERY access as an idempotent top-up. Inside one process, letting a
//   second app group inherit the first's rows is harmless precisely because of that top-up
//   (see the #1867 field doc in TestExecutor.cs). Across processes it is not the same bet:
//   the file would carry one bundle's object inventory into an unrelated bundle's run, keyed
//   by a dependency-set key that says nothing about which test assemblies were loaded, and a
//   top-up only ADDS rows — it never removes the foreign ones. So they are dropped on write
//   and rebuilt on demand after a disk restore, which is the same state the very first app
//   group of an uncached process has. On the measured Base Application closure this also
//   removes 23,367 of 23,645 rows from the file.
//
// FAITHFULNESS / REFUSAL
//   Serialisation is all-or-nothing. Any value this codec cannot prove it can rebuild
//   identically (an unknown NavValue kind, a wrapper value, a value whose type disagrees with
//   its field's, an Option whose NCLOptionMetadata is not the field's, a lazily-loaded
//   DB-backed BLOB, more than one DataAccessSource, a source that is not the skeleton one)
//   aborts the whole write with a verbose line naming the table and the value type. Nothing
//   partial is ever written, and the in-memory path is unaffected — a refusal costs only the
//   persistence, never correctness. On read, any magic/version/shape mismatch or decode error
//   deletes the entry, logs, and recomputes.
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // "ALIB" — little-endian magic, and the schema version. BUMP THE VERSION whenever the
    // byte layout or the set of encodable value kinds changes: an old file that still
    // deserialises cleanly under new semantics is the one failure mode a cache cannot
    // detect for itself.
    private const uint InstallBaselineDiskMagic = 0x42494C41;
    internal const int InstallBaselineDiskSchemaVersion = 2;

    // Pool-entry kinds. Kind is stored per DISTINCT NavValue instance, not per row slot.
    private const byte KindBytes = 1;       // NavValue.GetBytes() + NavValue.CreateNavValueFromBytes
    private const byte KindNullString = 2;  // NavText/NavCode with IsNull (DB NULL, GetBytes cannot say so)
    private const byte KindBlob = 3;        // NavBLOB — GetBytes has no CreateNavValueFromBytes counterpart
    private const byte KindMedia = 4;       // NavMedia/NavMediaSet — same gap, and #2270 made it reachable

    /// <summary>Table ids whose rows are a projection of the loaded-assembly set rather than
    /// install-trigger output, and which
    /// <see cref="GetDataAccessForTableCore"/> re-populates on every access. Excluded from the
    /// on-disk baseline — see the file header for why that is a correctness decision and not
    /// just a size one.</summary>
    internal static bool IsSelfPopulatingVirtualTableId(int tableId) => tableId switch
    {
        AllObjVirtualTableId or AllObjWithCaptionVirtualTableId or FieldVirtualTableId
            or IntegerVirtualTableId or ReportLayoutListVirtualTableId
            or PageMetadataVirtualTableId or ReportMetadataVirtualTableId
            or ReportDataItemsVirtualTableId or PageControlFieldVirtualTableId
            or TableMetadataVirtualTableId => true,
        _ => false,
    };

    private static void DiskLog(string message)
        => Console.Error.WriteLine($"[InstallBaselineDisk] {message}");

    /// <summary>The one DataAccessSource the skeleton session hands to every AL record
    /// operation. A disk restore has no captured source object to key
    /// <c>_dataAccessByTable</c> by, so it re-attaches the restored tables to this one — and
    /// the writer refuses to persist a snapshot whose source is anything else, so the two
    /// can never disagree.</summary>
    internal static object? ResolveSkeletonDataAccessSource()
    {
        var session = AlRunner.BcRuntime.SkeletonSession;
        if (session == null || _fSessionDataAccessSource == null) return null;
        return _fSessionDataAccessSource.GetValue(session);
    }

    private sealed record PoolEntry(
        byte Kind, int NclType, int DefinedLength, int Flags, int TableIndex, int FieldIndex, byte[] Bytes);

    // ── NavBLOB private state (Ncl.dll = runtime engine, ours to poke — see
    //    .claude/rules/precompiled-dll-respect.md). NavBLOB.DeepCopy(), the deep copy the
    //    in-memory baseline already uses, carries exactly these three, so a persisted BLOB
    //    that carries the same three is equivalent to a deep copy of the original.
    //    The contents stream itself is not poked: NavBLOB's public (byte[]) constructor
    //    installs it the same way AssignFromStream does, so only the two flags need direct
    //    access. Resolved eagerly together (and loudly, per loud-failures.md) so a BC shape
    //    change surfaces as a named MissingFieldException rather than a BLOB that quietly
    //    round-trips without its state.
    private static FieldInfo? _blobSizeWhenNoContents, _blobIsDirty;

    private static void EnsureBlobFields()
    {
        if (_blobIsDirty != null) return;
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        _blobSizeWhenNoContents = typeof(NavBLOB).GetField("sizeWhenNoContents", F)
            ?? throw new MissingFieldException(nameof(NavBLOB), "sizeWhenNoContents");
        _blobIsDirty = typeof(NavBLOB).GetField("isDirty", F)
            ?? throw new MissingFieldException(nameof(NavBLOB), "isDirty");
    }

    /// <summary>INavValueMetadata carrying the metadata of one CAPTURED value rather than of
    /// its field. The distinction is load-bearing: a NavText remembers its own maxLength and
    /// BC's <see cref="NavValue.CreateNavValueFromBytes"/> applies whatever length the
    /// metadata it is handed reports, so rebuilding with the field's metadata would silently
    /// re-length values. Option metadata is the one part that cannot be serialised (it is a
    /// live NCLOptionMetadata graph), so it comes from the field — and the writer refuses any
    /// Option value whose own metadata is not reference-equal to its field's.</summary>
    private sealed class CapturedValueMetadata : INavValueMetadata
    {
        public CapturedValueMetadata(NavNclType nclType, int definedLength, NCLOptionMetadata? optionMetadata)
        {
            NclType = nclType;
            NavDefinedLengthMetadata = definedLength;
            _optionMetadata = optionMetadata;
        }

        private readonly NCLOptionMetadata? _optionMetadata;
        public NavNclType NclType { get; }
        public int NavDefinedLengthMetadata { get; }
        public Microsoft.Dynamics.Nav.Types.NavType NavType => NavNclTypeHelper.GetNavTypeFromNclType(NclType);
        public NCLOptionMetadata NavOptionMetadata => _optionMetadata
            ?? throw new InvalidOperationException(
                $"install-baseline disk codec: no option metadata captured for {NclType}");
    }

    // ────────────────────────────────────────────────────────────── write ──

    /// <summary>Encode a snapshot for disk, or return null (having logged the reason) when any
    /// part of it cannot be proven to round-trip. Never throws for content reasons — a refusal
    /// is a normal outcome that costs only the persistence.</summary>
    internal static byte[]? TrySerializeInstallBaselineSnapshot(InstallBaselineSnapshot snapshot, string cacheKey)
    {
        if (snapshot.Sources.Count != 1)
        {
            DiskLog($"not persisting: snapshot has {snapshot.Sources.Count} DataAccessSource(s), expected exactly 1");
            return null;
        }
        var source = snapshot.Sources[0];
        var skeleton = ResolveSkeletonDataAccessSource();
        if (skeleton == null || !ReferenceEquals(skeleton, source.Source))
        {
            DiskLog("not persisting: the captured DataAccessSource is not the skeleton session's, "
                  + "so a disk restore could not re-attach the rows to the source AL will read through");
            return null;
        }

        var tables = source.Tables.Where(t => !IsSelfPopulatingVirtualTableId(t.TableId)).ToList();
        var pool = new List<PoolEntry>();
        var poolIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var rowIndices = new List<int[][]>(tables.Count);
        var fieldCounts = new List<int>(tables.Count);

        for (var ti = 0; ti < tables.Count; ti++)
        {
            var table = tables[ti];
            if (table.MetaTable is not NCLMetaTable meta)
            {
                DiskLog($"not persisting: table {table.TableId}'s MetaTable is "
                      + $"{table.MetaTable?.GetType().Name ?? "null"}, not NCLMetaTable");
                return null;
            }
            fieldCounts.Add(meta.FieldCount);
            var rows = new int[table.Rows.Length][];
            for (var ri = 0; ri < table.Rows.Length; ri++)
            {
                var values = table.Rows[ri];
                var indices = new int[values.Length];
                for (var fi = 0; fi < values.Length; fi++)
                {
                    var value = values[fi];
                    if (value == null) { indices[fi] = -1; continue; }
                    if (poolIndex.TryGetValue(value, out var existing)) { indices[fi] = existing; continue; }
                    var entry = TryEncodeValue(value, meta, table.TableId, ti, fi);
                    if (entry == null) return null;   // TryEncodeValue already logged the reason
                    poolIndex[value] = pool.Count;
                    indices[fi] = pool.Count;
                    pool.Add(entry);
                }
                rows[ri] = indices;
            }
            rowIndices.Add(rows);
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(InstallBaselineDiskMagic);
            w.Write(InstallBaselineDiskSchemaVersion);
            w.Write(cacheKey);

            w.Write(tables.Count);
            for (var ti = 0; ti < tables.Count; ti++)
            {
                w.Write(tables[ti].TableId);
                w.Write(fieldCounts[ti]);
                w.Write(tables[ti].Rows.Length);
            }

            w.Write(pool.Count);
            foreach (var e in pool)
            {
                w.Write(e.Kind);
                w.Write(e.NclType);
                w.Write(e.DefinedLength);
                w.Write(e.Flags);
                w.Write(e.TableIndex);
                w.Write(e.FieldIndex);
                w.Write(e.Bytes.Length);
                w.Write(e.Bytes);
            }

            foreach (var rows in rowIndices)
                foreach (var indices in rows)
                {
                    w.Write(indices.Length);
                    foreach (var i in indices) w.Write(i);
                }

            TenantStoragePatches.SerializeInstallBaseline(w, snapshot.IsolatedStorage);
            RecordLinkPatches.SerializeInstallBaseline(w, snapshot.RecordLinks);

            var ai = snapshot.AutoIncrement;
            w.Write(ai?.Count ?? 0);
            if (ai != null)
                foreach (var (k, v) in ai) { w.Write(k); w.Write(v); }
        }
        return ms.ToArray();
    }

    private static PoolEntry? TryEncodeValue(
        NavValue value, NCLMetaTable meta, int tableId, int tableIndex, int fieldIndex)
    {
        if (fieldIndex >= meta.FieldCount)
        {
            DiskLog($"not persisting: table {tableId} row has {fieldIndex + 1}+ values but the "
                  + $"metatable declares {meta.FieldCount} fields");
            return null;
        }
        var field = (INavValueMetadata)meta.GetFieldByIndex(fieldIndex);
        var self = (INavValueMetadata)value;

        if (value.IsWrapper)
        {
            DiskLog($"not persisting: table {tableId} field index {fieldIndex} holds a wrapper "
                  + $"{value.GetType().Name}, whose inner value this codec cannot rebuild");
            return null;
        }
        if (self.NclType != field.NclType)
        {
            DiskLog($"not persisting: table {tableId} field index {fieldIndex} holds a "
                  + $"{self.NclType} value in a {field.NclType} field");
            return null;
        }

        if (self.NclType == NavNclType.NavBlob)
        {
            if (value is not NavBLOB blob)
            {
                DiskLog($"not persisting: table {tableId} field index {fieldIndex} is NavBlob but "
                      + $"the value is {value.GetType().Name}");
                return null;
            }
            EnsureBlobFields();
            var size = (int)_blobSizeWhenNoContents!.GetValue(blob)!;
            var dirty = (bool)_blobIsDirty!.GetValue(blob)!;
            var inMemory = blob.IsInMemory;
            if (!inMemory && size != 0)
            {
                DiskLog($"not persisting: table {tableId} field index {fieldIndex} holds a "
                      + "DB-backed NavBLOB with no in-memory contents; its bytes live outside the snapshot");
                return null;
            }
            var bytes = inMemory ? blob.GetBytes() : Array.Empty<byte>();
            return new PoolEntry(KindBlob, (int)self.NclType, size, (inMemory ? 1 : 0) | (dirty ? 2 : 0),
                tableIndex, fieldIndex, bytes);
        }

        if (self.NclType is NavNclType.NavMedia or NavNclType.NavMediaSet)
        {
            // Media and MediaSet are absent from NavValue.CreateNavValueFromBytes's switch, so
            // they cannot ride the KindBytes path even though NavMediaValueBase.GetBytes() is
            // exactly the 16 GUID bytes their own CreateFromBytes consumes. Before #2270 that
            // was academic — nothing put a Media in the baseline — but one unencodable value
            // makes TrySerializeInstallBaselineSnapshot return null for the WHOLE snapshot, so
            // hydrating Customer/Vendor/Item Variant with their Image/Picture would have cost
            // every --test-data run its disk baseline, silently, and reported only a DiskLog
            // line.
            //
            // The parent id is deliberately NOT persisted. It is -1 on a freshly read row and
            // NavRecord.GetFieldValue overwrites it via SetOwnerRecordInformation before AL
            // sees the value, so round-tripping it would preserve a number nothing reads.
            if (value is not NavMediaValueBase media)
            {
                DiskLog($"not persisting: table {tableId} field index {fieldIndex} is {self.NclType} "
                      + $"but the value is {value.GetType().Name}");
                return null;
            }
            return new PoolEntry(KindMedia, (int)self.NclType, 0, 0, tableIndex, fieldIndex,
                EncodeInstallBaselineMedia(media));
        }

        var isStringLike = self.NclType is NavNclType.NavText or NavNclType.NavCode;
        if (value.IsNull)
        {
            if (!isStringLike)
            {
                DiskLog($"not persisting: table {tableId} field index {fieldIndex} holds a NULL "
                      + $"{self.NclType} ({value.GetType().Name}); only NavText/NavCode NULLs are encodable");
                return null;
            }
            return new PoolEntry(KindNullString, (int)self.NclType, self.NavDefinedLengthMetadata, 0,
                tableIndex, fieldIndex, Array.Empty<byte>());
        }

        // Every kind below is one BC's own NavValue.CreateNavValueFromBytes switch can rebuild
        // from NavValue.GetBytes(). Kinds absent from that switch (ByteArray, Char, …) fall
        // through to the refusal at the bottom rather than being guessed at. Media and
        // MediaSet are absent from it too, and are handled above by KindMedia — their own
        // CreateFromBytes does consume what their GetBytes produces.
        var encodable = self.NclType is NavNclType.NavBigInteger or NavNclType.NavBigText
            or NavNclType.NavBoolean or NavNclType.NavByte or NavNclType.NavCode
            or NavNclType.NavDate or NavNclType.NavDateFormula or NavNclType.NavDateTime
            or NavNclType.NavDecimal or NavNclType.NavDuration or NavNclType.NavGuid
            or NavNclType.NavInteger or NavNclType.NavOption or NavNclType.NavRecordId
            or NavNclType.NavOemText or NavNclType.NavOemCode or NavNclType.NavTime
            or NavNclType.NavTableFilter or NavNclType.NavText;
        if (!encodable)
        {
            DiskLog($"not persisting: table {tableId} field index {fieldIndex} holds "
                  + $"{self.NclType} ({value.GetType().Name}), which has no BC byte round-trip");
            return null;
        }

        if (self.NclType == NavNclType.NavOption
            && !ReferenceEquals(self.NavOptionMetadata, field.NavOptionMetadata))
        {
            DiskLog($"not persisting: table {tableId} field index {fieldIndex} holds an Option whose "
                  + "NCLOptionMetadata is not the field's, so a restore could not recover its option set");
            return null;
        }

        int definedLength;
        try { definedLength = self.NavDefinedLengthMetadata; }
        catch (NotSupportedException) { definedLength = 0; }

        return new PoolEntry(KindBytes, (int)self.NclType, definedLength, 0,
            tableIndex, fieldIndex, value.GetBytes());
    }

    // ─────────────────────────────────────────────────────────────── read ──

    /// <summary>Decode a snapshot previously written by
    /// <see cref="TrySerializeInstallBaselineSnapshot"/>. Returns null (logged) on any
    /// mismatch — the caller deletes the file and recomputes. Never partially restores: the
    /// whole snapshot object is built before the caller touches the live store.</summary>
    internal static InstallBaselineSnapshot? TryDeserializeInstallBaselineSnapshot(byte[] blob, string cacheKey)
    {
        var sourceObject = ResolveSkeletonDataAccessSource();
        if (sourceObject == null)
        {
            DiskLog("cannot restore: the skeleton session has no DataAccessSource yet");
            return null;
        }
        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);

            if (r.ReadUInt32() != InstallBaselineDiskMagic) { DiskLog("cannot restore: bad magic"); return null; }
            var version = r.ReadInt32();
            if (version != InstallBaselineDiskSchemaVersion)
            {
                DiskLog($"cannot restore: schema version {version}, this build writes {InstallBaselineDiskSchemaVersion}");
                return null;
            }
            var storedKey = r.ReadString();
            if (!string.Equals(storedKey, cacheKey, StringComparison.Ordinal))
            {
                DiskLog("cannot restore: the file's embedded cache key does not match the key it was looked up by");
                return null;
            }

            var tableCount = r.ReadInt32();
            var tableIds = new int[tableCount];
            var fieldCounts = new int[tableCount];
            var rowCounts = new int[tableCount];
            for (var i = 0; i < tableCount; i++)
            {
                tableIds[i] = r.ReadInt32();
                fieldCounts[i] = r.ReadInt32();
                rowCounts[i] = r.ReadInt32();
            }

            // Rebuild each table's NCLMetaTable through the SAME lookup the live path uses, so
            // a table whose metadata this process has not built yet gets built (and inserted
            // into the skeleton NCLMetadata cache) exactly as a live capture would have.
            var metaTables = new NCLMetaTable[tableCount];
            for (var i = 0; i < tableCount; i++)
            {
                var meta = EnsureTableInMetadataCache(tableIds[i]);
                if (meta == null)
                {
                    DiskLog($"cannot restore: no NCLMetaTable for table {tableIds[i]} in this process");
                    return null;
                }
                if (meta.FieldCount != fieldCounts[i])
                {
                    DiskLog($"cannot restore: table {tableIds[i]} now has {meta.FieldCount} fields, "
                          + $"the file was written against {fieldCounts[i]}");
                    return null;
                }
                metaTables[i] = meta;
            }

            var poolCount = r.ReadInt32();
            var pool = new NavValue[poolCount];
            for (var i = 0; i < poolCount; i++)
            {
                var kind = r.ReadByte();
                var nclType = (NavNclType)r.ReadInt32();
                var definedLength = r.ReadInt32();
                var flags = r.ReadInt32();
                var tableIndex = r.ReadInt32();
                var fieldIndex = r.ReadInt32();
                var bytes = r.ReadBytes(r.ReadInt32());
                if (tableIndex < 0 || tableIndex >= tableCount) { DiskLog("cannot restore: pool entry table index out of range"); return null; }
                var meta = metaTables[tableIndex];
                if (fieldIndex < 0 || fieldIndex >= meta.FieldCount) { DiskLog("cannot restore: pool entry field index out of range"); return null; }
                pool[i] = DecodeValue(kind, nclType, definedLength, flags, meta, fieldIndex, bytes);
            }

            var baselineTables = new List<BaselineTable>(tableCount);
            for (var ti = 0; ti < tableCount; ti++)
            {
                var rows = new NavValue[rowCounts[ti]][];
                for (var ri = 0; ri < rows.Length; ri++)
                {
                    var n = r.ReadInt32();
                    var values = new NavValue[n];
                    for (var fi = 0; fi < n; fi++)
                    {
                        var idx = r.ReadInt32();
                        if (idx == -1) continue;
                        if (idx < 0 || idx >= poolCount) { DiskLog("cannot restore: row value index out of range"); return null; }
                        values[fi] = pool[idx];
                    }
                    rows[ri] = values;
                }
                baselineTables.Add(new BaselineTable(tableIds[ti], metaTables[ti], rows));
            }

            var isolatedStorage = TenantStoragePatches.DeserializeInstallBaseline(r);
            var recordLinks = RecordLinkPatches.DeserializeInstallBaseline(r);

            var aiCount = r.ReadInt32();
            var autoIncrement = new Dictionary<int, long>(aiCount);
            for (var i = 0; i < aiCount; i++)
            {
                var k = r.ReadInt32();
                autoIncrement[k] = r.ReadInt64();
            }

            if (ms.Position != ms.Length)
            {
                DiskLog($"cannot restore: {ms.Length - ms.Position} trailing byte(s) after the payload");
                return null;
            }

            return new InstallBaselineSnapshot(
                new List<BaselineSource> { new BaselineSource(sourceObject, baselineTables) },
                isolatedStorage, recordLinks, autoIncrement);
        }
        catch (Exception ex)
        {
            DiskLog($"cannot restore: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The KindMedia payload: the 16 GUID bytes NavMediaValueBase.GetBytes() produces
    /// and NavMedia/NavMediaSet.CreateFromBytes consumes. Split out from the encoder so the
    /// round trip is exercisable without a skeleton session — see
    /// TestDataLobValueHydrationTests.</summary>
    internal static byte[] EncodeInstallBaselineMedia(NavMediaValueBase media) => media.GetBytes();

    /// <summary>The other half of <see cref="EncodeInstallBaselineMedia"/>.</summary>
    internal static NavValue DecodeInstallBaselineMedia(NavNclType nclType, byte[] bytes)
        => nclType switch
        {
            NavNclType.NavMedia => NavMedia.CreateFromBytes(bytes, 0, bytes.Length),
            NavNclType.NavMediaSet => NavMediaSet.CreateFromBytes(bytes, 0, bytes.Length),
            _ => throw new InvalidDataException($"{nclType} is not a media kind"),
        };

    private static NavValue DecodeValue(
        byte kind, NavNclType nclType, int definedLength, int flags,
        NCLMetaTable meta, int fieldIndex, byte[] bytes)
    {
        switch (kind)
        {
            case KindNullString:
                return nclType switch
                {
                    NavNclType.NavText => new NavText(definedLength, (string?)null),
                    NavNclType.NavCode => new NavCode(definedLength, (string?)null),
                    _ => throw new InvalidDataException($"NULL {nclType} is not an encodable kind"),
                };

            case KindBlob:
            {
                EnsureBlobFields();
                var inMemory = (flags & 1) != 0;
                var blob = inMemory ? new NavBLOB(bytes) : new NavBLOB();
                _blobSizeWhenNoContents!.SetValue(blob, definedLength);
                _blobIsDirty!.SetValue(blob, (flags & 2) != 0);
                return blob;
            }

            case KindMedia:
                return DecodeInstallBaselineMedia(nclType, bytes);

            case KindBytes:
            {
                var field = (INavValueMetadata)meta.GetFieldByIndex(fieldIndex);
                var optionMetadata = nclType == NavNclType.NavOption ? field.NavOptionMetadata : null;
                var valueMetadata = new CapturedValueMetadata(nclType, definedLength, optionMetadata);
                return NavValue.CreateNavValueFromBytes(valueMetadata, bytes, 0, bytes.Length);
            }

            default:
                throw new InvalidDataException($"unknown pool-entry kind {kind}");
        }
    }

    /// <summary>Value-level digest of everything an on-disk baseline carries, in the order it
    /// carries it: for every persistable table, every row, every field slot — the value's own
    /// NclType, its own defined length, its NULL flag and the exact bytes BC's
    /// <c>GetBytes()</c> produces — plus the isolated-storage, record-link and auto-increment
    /// state (sorted, since those stores' enumeration order is not meaningful).
    ///
    /// This is the ROUND-TRIP PROOF the cross-process test asserts on: the writing process
    /// logs it for the snapshot it captured, the reading process logs it for the snapshot it
    /// decoded, and the two must be the same string. It deliberately does not go through
    /// <c>ToString()</c> (which would hide a lost maxLength, a lost NULL flag or a lost
    /// NavCode alpha index) and deliberately does not compare the serialised payload byte for
    /// byte (which would be sensitive to how BC happens to intern equal values on decode, a
    /// property of BC's caches rather than of this codec's fidelity).
    ///
    /// Computed only when PerfTrace is on — it walks every value in the snapshot.</summary>
    internal static string ComputeRoundTripDigest(InstallBaselineSnapshot snapshot)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var source in snapshot.Sources)
            foreach (var table in source.Tables)
            {
                if (IsSelfPopulatingVirtualTableId(table.TableId)) continue;
                for (var ri = 0; ri < table.Rows.Length; ri++)
                {
                    var row = table.Rows[ri];
                    for (var fi = 0; fi < row.Length; fi++)
                    {
                        sb.Append(table.TableId).Append('|').Append(ri).Append('|').Append(fi).Append('|');
                        var v = row[fi];
                        if (v == null) { sb.Append("<null>\n"); continue; }
                        var meta = (INavValueMetadata)v;
                        int definedLength;
                        try { definedLength = meta.NavDefinedLengthMetadata; }
                        catch (NotSupportedException) { definedLength = -1; }
                        sb.Append(meta.NclType).Append('|').Append(definedLength).Append('|')
                          .Append(v.IsNull ? 'N' : '-').Append('|');
                        try { sb.Append(Convert.ToHexString(v.GetBytes())); }
                        catch (Exception ex) { sb.Append("<nobytes:").Append(ex.GetType().Name).Append('>'); }
                        sb.Append('\n');
                    }
                }
            }
        foreach (var line in TenantStoragePatches.DescribeInstallBaseline(snapshot.IsolatedStorage))
            sb.Append(line).Append('\n');
        foreach (var line in RecordLinkPatches.DescribeInstallBaseline(snapshot.RecordLinks))
            sb.Append(line).Append('\n');
        foreach (var (k, v) in (snapshot.AutoIncrement ?? new Dictionary<int, long>()).OrderBy(p => p.Key))
            sb.Append("ai|").Append(k).Append('|').Append(v).Append('\n');

        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>Order-independent digest over the tables an on-disk baseline actually carries
    /// (i.e. excluding <see cref="IsSelfPopulatingVirtualTableId"/> tables). Same hashing as
    /// <see cref="ComputeContentDigest"/>; the point of the narrower scope is that it is the
    /// scope in which "restored from disk" and "captured in memory" must be identical, which
    /// is what the round-trip test asserts.</summary>
    internal static string ComputePersistableContentDigest(InstallBaselineSnapshot snapshot)
    {
        var narrowed = snapshot.Sources
            .Select(s => new BaselineSource(s.Source,
                s.Tables.Where(t => !IsSelfPopulatingVirtualTableId(t.TableId)).ToList()))
            .ToList();
        return ComputeContentDigest(narrowed);
    }
}
