// RecordPatches.TableTriggerMetadata — NCLMetaTable's five Is<Trigger>Defined flags (#3556).
//
// The table twin of RecordPatches.PageTriggerMetadata.cs (#3447), and it decides more: BC's own
// NavRecord.ALInsert/ALModify/ALDelete/ALRename run the ext.OnBefore<X> loop, the base table's
// own On<X> trigger and the ext.On<X> loop INSIDE `if (runApplicationTrigger &&
// metaTable.Is<X>TriggerDefined)`. The ext.OnAfter<X> loop sits outside it, guarded by
// runApplicationTrigger alone — which is why, before this, a tableextension's OnAfterInsert ran
// and its OnBeforeInsert did not. See docs/table-trigger-metadata.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly TriggerSurface TableTriggerSurface = new(
        "table trigger metadata", "table", "docs/table-trigger-metadata.md");

    private static readonly ConditionalWeakTable<object, StrongBox<int>> _definedTableTriggersCache = new();

    // BC's TableTriggers bit → the trigger names that set it, in BC's own order
    // (NCLMetaTable.DefinedTriggers, identical body on 27.0, 27.5 and 28.4). The base-table arm
    // reads the middle name of each triple off NavRecord; the extension arm reads all three off
    // NavRecordExtension. OnAfterModify is a bit of its own with a single name, and no base-table
    // arm at all — BC never sets it from the table's own class.
    private static readonly (string Bit, string? BaseName, string[] ExtensionNames)[] _tableTriggerBits =
    {
        ("Insert",        "OnInsert", new[] { "OnBeforeInsert", "OnInsert", "OnAfterInsert" }),
        ("Modify",        "OnModify", new[] { "OnBeforeModify", "OnModify", "OnAfterModify" }),
        ("OnAfterModify", null,       new[] { "OnAfterModify" }),
        ("Delete",        "OnDelete", new[] { "OnBeforeDelete", "OnDelete", "OnAfterDelete" }),
        ("Rename",        "OnRename", new[] { "OnBeforeRename", "OnRename", "OnAfterRename" }),
    };

    private static Dictionary<string, int>? _tableTriggerBitValues;

    /// <summary>
    /// Replacement for the private <c>NCLMetaTable.get_DefinedTriggers</c>, the single source of
    /// <c>IsInsertTriggerDefined</c>, <c>IsModifyTriggerDefined</c>,
    /// <c>IsOnAfterModifyTriggerDefined</c>, <c>IsDeleteTriggerDefined</c> and
    /// <c>IsRenameTriggerDefined</c>. Returns the <c>TableTriggers</c> bitmask as its underlying
    /// <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// Observably equivalent to BC's own body — the same five bits, the same trigger names in the
    /// same order, the same <c>…Async</c> fallback and <c>DeclaringType</c> comparison — for a
    /// table whose tableextensions BC would have loaded. Two deliberate differences, both because
    /// the runner hand-builds this metatable rather than loading it: the extension list is the
    /// UNION of BC's own <c>orderedExtensionObjects</c> and the runner's tableextension registry
    /// (the registry alone is what the runner fills, and the union keeps a BC-loaded metatable
    /// answering exactly as BC would); and the answer is cached only once every extension's
    /// compiled class resolves, because BC's unconditional cache would freeze an answer taken
    /// before the test assembly loaded.
    /// Verified against a real service tier by corpus codeunit 60433 (corpus PR #307), 6/6 on
    /// BC 28.4.53241.0. See docs/table-trigger-metadata.md.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NCLMetaTable_get_DefinedTriggers(object self)
    {
        if (self == null) return 0;
        if (_definedTableTriggersCache.TryGetValue(self, out var cached)) return cached.Value;

        var recordClrType = NCLMetaApplicationObject_get_ApplicationObjectClrType(self);
        if (recordClrType == null) return 0;

        var bits = EnsureTableTriggerBitValues(self.GetType());

        int mask = 0;
        var extensionTypes = TableExtensionTypesFor(self, out var allExtensionsResolved);
        foreach (var (bit, baseName, extensionNames) in _tableTriggerBits)
        {
            var value = bits[bit];
            if (baseName != null
                && IsTriggerImplemented(typeof(Microsoft.Dynamics.Nav.Runtime.NavRecord),
                                        recordClrType, baseName, isPublic: false, TableTriggerSurface))
            {
                mask |= value;
                continue;
            }
            foreach (var extType in extensionTypes)
            {
                if (!extensionNames.Any(n => IsTriggerImplemented(
                        typeof(Microsoft.Dynamics.Nav.Runtime.Extensions.NavRecordExtension),
                        extType, n, isPublic: true, TableTriggerSurface)))
                    continue;
                mask |= value;
                break;
            }
        }

        TableTriggerAudit.Record(self, recordClrType, extensionTypes, mask, bits);
        if (allExtensionsResolved) _definedTableTriggersCache.AddOrUpdate(self, new StrongBox<int>(mask));
        return mask;
    }

    /// <summary>
    /// The five <c>TableTriggers</c> bit values, read off BC's own enum by NAME rather than
    /// assumed to be 1/2/4/8/0x10 — a renumbering would otherwise silently move which flag a bit
    /// answers.
    /// </summary>
    private static Dictionary<string, int> EnsureTableTriggerBitValues(Type metaTableType)
    {
        if (_tableTriggerBitValues != null) return _tableTriggerBitValues;

        Type? enumType = null;
        for (var t = metaTableType; t != null && enumType == null; t = t.BaseType)
            enumType = t.GetProperty("DefinedTriggers",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.PropertyType;

        if (enumType == null || !enumType.IsEnum)
            throw new BcShapeGapException(
                "table trigger metadata", "NCLMetaTable.DefinedTriggers",
                "BC no longer declares the private DefinedTriggers property whose enum names the "
                + "table-trigger bits, so the runner cannot compute the Is<Trigger>Defined flags");

        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in Enum.GetValues(enumType))
        {
            var name = v.ToString();
            if (!string.IsNullOrEmpty(name)) values[name!] = Convert.ToInt32(v);
        }

        foreach (var (bit, _, _) in _tableTriggerBits)
            if (!values.ContainsKey(bit))
                throw new BcShapeGapException(
                    "table trigger metadata", $"NCLMetaTable.TableTriggers.{bit}",
                    $"BC's table-trigger enum no longer declares {bit}, so the runner cannot say "
                    + "which writes need trigger dispatch — see docs/table-trigger-metadata.md");

        _tableTriggerBitValues = values;
        return values;
    }

    /// <summary>
    /// The compiled <c>TableExtension{id}</c> classes extending this table: BC's own
    /// <c>orderedExtensionObjects</c> (empty on a runner-built metatable, populated on a
    /// BC-loaded one) unioned with the runner's tableextension registry — the same
    /// <c>_extensionIdsByBaseTable</c> / <see cref="FindTableExtensionType"/> pair that
    /// <see cref="RegisterParsedTableExtensions"/> instantiates from, so the flag and the
    /// extension instances a write dispatches to cannot disagree about which extensions exist.
    /// </summary>
    private static List<Type> TableExtensionTypesFor(object metaTable, out bool allResolved)
    {
        var types = new List<Type>();
        allResolved = true;
        if (!TryGetMetaObjectNumber(metaTable, out _, out var tableId)) return types;

        foreach (var loaded in BcLoadedTableExtensionTypes(metaTable))
            if (!types.Contains(loaded)) types.Add(loaded);

        if (!_parsedTables.TryGetValue(tableId, out var parsed)) return types;
        if (!_extensionIdsByBaseTable.TryGetValue(parsed.TableName.ToLowerInvariant(), out var extIds))
            return types;

        foreach (var extensionId in extIds)
        {
            var t = FindTableExtensionType(extensionId);
            if (t != null) { if (!types.Contains(t)) types.Add(t); continue; }

            allResolved = false;
            // Loud, once per (table, extension): the flags would otherwise report this table as
            // declaring fewer triggers than it does, and a false Is<X>TriggerDefined does not
            // merely mislead a metadata reader — BC skips the whole trigger block on the write.
            if (_unresolvedTableExtensionTypesWarned.Add((tableId, extensionId)))
                Console.Out.WriteLine(
                    $"[warn] RecordPatches: table {tableId}: tableextension {extensionId} extends it, but no "
                    + $"compiled TableExtension{extensionId} type is loaded, so the triggers it declares are "
                    + "missing from this table's Is<Trigger>Defined flags");
        }
        return types;
    }

    private static readonly HashSet<(int Table, int Extension)> _unresolvedTableExtensionTypesWarned = new();

    /// <summary>
    /// The CLR types behind BC's own <c>orderedExtensionObjects</c>, if anything filled it. A
    /// private field on a base type is not found through inheritance by <c>GetField</c>, so the
    /// type chain is walked by hand — same shape as <see cref="TryGetMetaObjectNumber"/>.
    /// </summary>
    private static IEnumerable<Type> BcLoadedTableExtensionTypes(object metaTable)
    {
        FieldInfo? field = null;
        for (var t = metaTable.GetType(); t != null && field == null; t = t.BaseType)
            field = t.GetField("orderedExtensionObjects",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        if (field?.GetValue(metaTable) is not System.Collections.IEnumerable list) yield break;
        foreach (var ext in list)
        {
            if (ext == null) continue;
            var t = NCLMetaApplicationObject_get_ApplicationObjectClrType(ext);
            if (t != null) yield return t;
        }
    }
}

/// <summary>
/// Opt-in AUDIT of what <see cref="RecordPatches.NCLMetaTable_get_DefinedTriggers"/> answered,
/// one line per table, written when the process exits — an inspection channel like
/// <c>AL_RUNNER_HOOK_AUDIT</c>, not a diagnosis, so it is not <c>[warn]</c>-tagged and reports no
/// problem. Off unless <c>AL_RUNNER_TABLE_TRIGGER_AUDIT=1</c>.
///
/// <para>Written straight to the process's stdout HANDLE: the test phase redirects
/// <see cref="Console"/>, so a <c>Console.Out</c> write from here is swallowed.</para>
/// </summary>
internal static class TableTriggerAudit
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("AL_RUNNER_TABLE_TRIGGER_AUDIT") == "1";

    private static readonly object Gate = new();
    private static readonly SortedDictionary<int, string> Lines = new();
    private static bool _flushRegistered;

    internal static void Record(
        object metaTable, Type recordClrType, List<Type> extensionTypes, int mask,
        Dictionary<string, int> bits)
    {
        if (!Enabled) return;
        if (!RecordPatches.TryGetMetaObjectNumber(metaTable, out _, out var tableId)) return;

        var defined = bits.Where(b => b.Value != 0 && (mask & b.Value) == b.Value && b.Key != "Unknown")
                          .OrderBy(b => b.Value).Select(b => b.Key).ToArray();
        var line = $"[table-trigger-audit] table={tableId} clr={recordClrType.Name}"
                 + $" ext={(extensionTypes.Count == 0 ? "-" : string.Join(",", extensionTypes.Select(t => t.Name)))}"
                 + $" triggers={(defined.Length == 0 ? "-" : string.Join(",", defined))}";

        lock (Gate)
        {
            Lines[tableId] = line;
            if (_flushRegistered) return;
            _flushRegistered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    private static void Flush()
    {
        lock (Gate)
        {
            try
            {
                using var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                foreach (var line in Lines.Values) stdout.WriteLine(line);
            }
            catch { }
        }
    }
}
