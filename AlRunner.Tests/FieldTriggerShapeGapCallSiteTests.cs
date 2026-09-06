// FieldTriggerShapeGapCallSiteTests — issue #3047.
//
// WHAT WAS UNPINNED
// -----------------
// #3026 converted seventeen silent skips on the field-trigger install path into named
// BcShapeGapExceptions, routed through the seven helpers in
// AlRunner/Patches/FieldTriggerShapeGaps.cs. Exactly ONE of the seventeen — the base-table
// RequireHandlerBacking pair — was held in place by a test
// (FieldTriggerHandlerBackingShapeGapTests). Revert any of the other sixteen to the old
// `&& _fXxx != null` form and the whole suite stayed green.
//
// The existing count guard cannot see them, BY CONSTRUCTION. VirtualTableRefusalClaimTests
// counts `throw (RecordPatches\.)?[A-Za-z]+(?<!Bc)ShapeGap\(` over a fixed file list, and none
// of the seventeen matches it: RecordPatches.NclMetaTableBuilder.cs is not on that list, six of
// the seven helpers throw INTERNALLY (`=> backing ?? throw new BcShapeGapException(...)`) rather
// than at a textual `throw` site, and the seventh is `throw FieldTriggerShapeGap.HandlerConstruction(`,
// whose `.` breaks `[A-Za-z]+ShapeGap\(`. Measured on the merged tree: 0 regex matches in
// RecordPatches.NclMetaTableBuilder.cs, FieldTriggerShapeGaps.cs and FieldTriggerInstallGaps.cs,
// so that count reads 67 before and after — correct, and blind to this.
//
// HOW THE SEVENTEEN ARE PINNED HERE
// ---------------------------------
// Two layers, because neither alone is enough:
//
//   1. BEHAVIOURAL, fifteen of the seventeen (SiteReachedByFaultInjection). One cached
//      reflection static is set to null — or, for the two constructor guards, to a decoy
//      generic type that exists but has the wrong constructor shape — for the duration of one
//      wiring call, and restored in a finally. That is the exact state a BC build with a moved
//      NCLMetaField / EventTriggerData layout would leave EnsureFieldTriggerReflection in.
//      Each row asserts the refusal's Surface and Member EXACTLY, so a row cannot be satisfied
//      by a different site's refusal over the same static: sites 3 and 7 (base vs extension
//      install loop) demand the same member and are told apart by their arrangement, and sites
//      5/11 and 6/12 likewise.
//
//   2. STRUCTURAL, all seventeen (AllSeventeenCallSitesStillStand). Two of the seventeen have
//      NO injectable fault, and saying so is part of the claim:
//        * RequireScanType(_tFieldTriggerHandlerAttr, …) — that static is
//          EnsureFieldTriggerReflection's OWN sentinel (`if (_tFieldTriggerHandlerAttr != null)
//          return;`), so nulling it makes the very next call re-resolve it from Ncl.dll.
//        * HandlerConstruction("…NavApplicationObjectBase", …) — BuildFieldTriggerHandler
//          re-resolves _tNavApplicationObjectBase from Ncl.dll whenever it is null.
//      Both are reachable only on a BC build that genuinely lacks the type, which no supported
//      build does. The per-helper call-site census catches the mutation the issue names anyway:
//      reverting a guard to `&& _fXxx != null` DELETES a FieldTriggerShapeGap.RequireXxx( call,
//      and the census goes red. Their MESSAGES are pinned by EveryFactorySpellsTheWireFormat.
//
// PROPORTIONALITY IS THE OTHER HALF (#3041's central claim)
// ---------------------------------------------------------
// #3026 moved five guards OUT of the method-top and DOWN to the point of use, so a table with
// no field trigger is no longer refused for a member it never reads. That claim had no test,
// and the regression it invites is OVER-refusing: a trigger-less table failing to wire on a
// moved-layout build. TriggerlessTableStillWires asserts it per member and for all seven at
// once, and TriggerlessTableIsStillRefusedForAScanType is the control that stops the whole
// suite passing by never refusing at all — FieldTriggerType is deliberately NOT proportional,
// because without it the runner cannot tell whether ANY table declares a trigger.
//
// THE BLAST RADIUS OF A SCAN-TYPE REFUSAL
// ---------------------------------------
// WireFieldTriggerHandlersAll runs at bundle load (BcRuntime.SetTestAssembly, Program.cs), so a
// RequireScanType refusal is a RUN-LEVEL ABORT, not an attributable single-test failure. That is
// loud rather than silent and so not a defect — but it was unstated and unpinned, which is the
// difference between "one test names the moved member" and "the whole bundle fails to load".
// ScanTypeGap_AbortsBundleLoad_NamingTheMember pins it, and docs/limitations.md now says it.
//
// WHY A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE OR A CORPUS TEST
// ----------------------------------------------------------------------
// No AL statement can move a private BC field, and no statement about Business Central is being
// made — the subject is what the RUNNER does when it cannot read BC's own layout. That is the
// classification .claude/rules/bc-behavior-tests-go-upstream.md gives
// FieldTriggerHandlerBackingShapeGapTests and FieldTriggerInstallSilentSkipTests, whose harness
// this file reuses.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Extensions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>Base-table stand-in with one void trigger of each kind, on two different fields.
/// <c>internal</c> for the reason on <c>FieldTriggerGapStandInRecord</c>: xUnit discovery
/// resolves a PUBLIC type's whole base chain, and <c>NavComplexValue</c> lives in a
/// reference-only assembly absent from the test bin dir.</summary>
internal sealed class FieldTriggerCallSiteRecord : NavRecord
{
    internal FieldTriggerCallSiteRecord() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerShapeGapCallSiteTests.ValidateFieldNo)]
    public void OnValidateNo() { }

    [FieldTriggerHandler(FieldTriggerType.OnLookup, FieldTriggerShapeGapCallSiteTests.LookupFieldNo)]
    public void OnLookupDescription() { }
}

/// <summary>Exactly ONE trigger, returning void, so the constructor-guard rows below name a
/// method deterministically rather than depending on dictionary iteration order.</summary>
internal sealed class FieldTriggerCallSiteSyncOnlyRecord : NavRecord
{
    internal FieldTriggerCallSiteSyncOnlyRecord() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerShapeGapCallSiteTests.ValidateFieldNo)]
    public void OnValidateSyncOnly() { }
}

/// <summary>Exactly one trigger, returning <c>ValueTask</c>, so BuildFieldTriggerHandler takes
/// its async branch and the <c>Func&lt;T, ValueTask&gt;</c> constructor guard is the one reached.</summary>
internal sealed class FieldTriggerCallSiteAsyncOnlyRecord : NavRecord
{
    internal FieldTriggerCallSiteAsyncOnlyRecord() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerShapeGapCallSiteTests.ValidateFieldNo)]
    public ValueTask OnValidateAsyncOnly() => default;
}

/// <summary>No field triggers at all: the proportionality subject, and the arrangement that
/// makes the EXTENSION install loop reachable without the base-table loop refusing first.</summary>
internal sealed class FieldTriggerCallSiteTriggerlessRecord : NavRecord
{
    internal FieldTriggerCallSiteTriggerlessRecord() : base(null!, 0) { }
}

/// <summary>
/// Stand-in <c>TableExtension&lt;id&gt;</c> covering all four extension trigger kinds: a
/// <c>modify(field)</c> before/after pair on field 1, and the <c>OnValidate</c> / <c>OnLookup</c>
/// of a field the extension ADDS on field 2. Both fields exist on the base table, so nothing
/// here reaches #3048's unresolvable-field refusal.
/// </summary>
internal sealed class FieldTriggerCallSiteExtension : NavRecordExtension
{
    internal FieldTriggerCallSiteExtension() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnBeforeValidate, FieldTriggerShapeGapCallSiteTests.ValidateFieldNo)]
    public void OnBeforeValidateNo() { }

    [FieldTriggerHandler(FieldTriggerType.OnAfterValidate, FieldTriggerShapeGapCallSiteTests.ValidateFieldNo)]
    public void OnAfterValidateNo() { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerShapeGapCallSiteTests.LookupFieldNo)]
    public void OnValidateDescription() { }

    [FieldTriggerHandler(FieldTriggerType.OnLookup, FieldTriggerShapeGapCallSiteTests.LookupFieldNo)]
    public void OnLookupDescription() { }
}

/// <summary>
/// Stands in for BC's <c>FieldTriggerHandler&lt;T&gt;</c> on a build where the type still
/// exists but its constructors moved — the only fault that reaches the two
/// <c>HandlerConstruction</c> constructor guards, since a missing TYPE is caught one line
/// earlier. It closes over <c>NavApplicationObjectBase</c> without complaint and offers
/// neither <c>(Type, Action&lt;T&gt;)</c> nor <c>(Type, Func&lt;T, ValueTask&gt;)</c>.
/// </summary>
internal sealed class FieldTriggerCallSiteDecoyHandler<T>
{
}

[Collection(BcEngineCollection.Name)]
public sealed class FieldTriggerShapeGapCallSiteTests : IDisposable
{
    internal const int ValidateFieldNo = 1;
    internal const int LookupFieldNo = 2;

    /// <summary>The tableextension id every extension-arrangement row registers. One id is
    /// enough: <c>_tableExtensionTypeCache</c> is keyed by it and always maps to the same
    /// stand-in type, while <c>_extensionIdsByBaseTable</c> is keyed by the base table's NAME,
    /// which is unique per row.</summary>
    private const int StandInExtensionId = 93997;

    private const int AbortTableId = 93995;
    private const int PerTableControlTableId = 93996;

    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public FieldTriggerShapeGapCallSiteTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-3047-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── plumbing (same shape as FieldTriggerHandlerBackingShapeGapTests) ────────────────

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static FieldInfo Static(string name) =>
        typeof(RecordPatches).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"RecordPatches.{name} not found — this test tracks that field.");

    private static readonly MethodInfo EnsureReflection =
        typeof(RecordPatches).GetMethod("EnsureFieldTriggerReflection", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("RecordPatches.EnsureFieldTriggerReflection not found.");

    private static readonly MethodInfo WireOne =
        typeof(RecordPatches).GetMethod("WireFieldTriggerHandlers", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("RecordPatches.WireFieldTriggerHandlers not found.");

    /// <summary>Calls the real wiring entry point, unwrapping reflection's TargetInvocationException.</summary>
    private static bool Wire(NCLMetaTable table, int tableId)
    {
        try
        {
            return (bool)WireOne.Invoke(null, new object[] { table, tableId })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static string TableName(int tableId) => $"FieldTriggerCallSite {tableId}";

    /// <summary>
    /// The metatable for a freshly written one-off AL table, with <paramref name="recordType"/>
    /// registered as its Record CLR type so <c>FindRecordType</c> resolves. Both fields the
    /// stand-ins declare triggers for really exist on it, so no row can be satisfied by #3048's
    /// unresolvable-field refusal instead of the shape gap it is asserting.
    /// </summary>
    private NCLMetaTable Arrange(int tableId, Type recordType)
    {
        var dir = Path.Combine(_root, tableId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Table.al"), $$"""
            table {{tableId}} "{{TableName(tableId)}}"
            {
                fields
                {
                    field({{ValidateFieldNo}}; "No."; Code[20]) { }
                    field({{LookupFieldNo}}; "Description"; Text[50]) { }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """);
        RecordPatches.AddSourceDir(dir);

        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);
        var table = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, tableId, false, 0);
        Assert.NotNull(table);
        Assert.Equal(tableId, table.TableId);

        Assert.True(table.TryGetFieldByNo(ValidateFieldNo, out _), $"table {tableId} must carry field {ValidateFieldNo}");
        Assert.True(table.TryGetFieldByNo(LookupFieldNo, out _), $"table {tableId} must carry field {LookupFieldNo}");

        EnsureReflection.Invoke(null, null);

        var cache = (ConcurrentDictionary<int, Type>)Static("_recordTypeCache").GetValue(null)!;
        cache[tableId] = recordType;

        return table;
    }

    /// <summary>Registers the stand-in tableextension against <paramref name="tableId"/> for the
    /// duration of <paramref name="body"/>. Both dictionaries are process-global, so both are
    /// unwound in the finally; the class is DisableParallelization (BcEngineCollection), so no
    /// sibling observes the window.</summary>
    private static void WithExtensionRegistered(int tableId, Action body)
    {
        var extIds = (Dictionary<string, List<int>>)Static("_extensionIdsByBaseTable").GetValue(null)!;
        var extTypes = (ConcurrentDictionary<int, Type>)Static("_tableExtensionTypeCache").GetValue(null)!;
        var key = TableName(tableId).ToLowerInvariant();

        lock (extIds) extIds[key] = new List<int> { StandInExtensionId };
        extTypes[StandInExtensionId] = typeof(FieldTriggerCallSiteExtension);
        try { body(); }
        finally
        {
            lock (extIds) extIds.Remove(key);
            extTypes.TryRemove(StandInExtensionId, out _);
        }
    }

    /// <summary>Sets cached reflection statics to <paramref name="value"/> for the duration of
    /// <paramref name="body"/> and restores them in a finally — the state a BC build whose
    /// layout moved would leave EnsureFieldTriggerReflection in. Nothing in production is made
    /// settable for the test's benefit.</summary>
    private static void WithStatics(IReadOnlyList<string> names, object? value, Action body)
    {
        var fields = names.Select(Static).ToList();
        var originals = fields.Select(f => f.GetValue(null)).ToList();
        foreach (var f in fields) f.SetValue(null, value);
        try { body(); }
        finally
        {
            for (var i = 0; i < fields.Count; i++) fields[i].SetValue(null, originals[i]);
        }
    }

    private static Type ArrangementRecordType(string arrangement) => arrangement switch
    {
        "base" => typeof(FieldTriggerCallSiteRecord),
        "base-sync-only" => typeof(FieldTriggerCallSiteSyncOnlyRecord),
        "base-async-only" => typeof(FieldTriggerCallSiteAsyncOnlyRecord),
        "ext" => typeof(FieldTriggerCallSiteTriggerlessRecord),
        _ => throw new ArgumentOutOfRangeException(nameof(arrangement), arrangement, "unknown arrangement"),
    };

    // ── 1. THE FIFTEEN INJECTABLE CALL SITES ───────────────────────────────────────────
    //
    // Columns: site label · arrangement · static to poison · injected value · table id ·
    //          expected BcShapeGapException.Surface · expected .Member · a Detail fragment.
    //
    // "decoy" means FieldTriggerCallSiteDecoyHandler<> rather than null: the two constructor
    // guards are only reachable when the TYPE resolves and its constructors do not.

    public static IEnumerable<object[]> InjectableSites() => new[]
    {
        // — the scan, which refuses for any table (deliberately not proportional) —
        new object[] { "scan/FieldTriggerType", "base", "_tFieldTriggerType", "null", 93970,
            "AL field trigger installation (table 93970)",
            "Microsoft.Dynamics.Nav.Runtime.FieldTriggerType",
            "every field trigger in the bundle would silently never fire" },

        // — the base-table install loop —
        new object[] { "base/EventTriggerData", "base", "_tEventTriggerData", "null", 93971,
            "AL field trigger installation (table 93971)",
            "NCLMetaField.EventTriggerData",
            "nested type not found on this BC build" },
        new object[] { "base/EventTriggerDataValue", "base", "_fEventTriggerDataValueBacking", "null", 93972,
            "AL field trigger installation (table 93972)",
            "NCLMetaField.EventTriggerDataValue",
            "<EventTriggerDataValue>k__BackingField" },
        new object[] { "base/ValidateHandler", "base", "_fValidateHandlerBacking", "null", 93973,
            "AL field trigger installation (table 93973, field 1)",
            "NCLMetaField.EventTriggerData.ValidateHandler",
            "<ValidateHandler>k__BackingField" },
        new object[] { "base/LookupHandler", "base", "_fLookupHandlerBacking", "null", 93974,
            "AL field trigger installation (table 93974, field 2)",
            "NCLMetaField.EventTriggerData.LookupHandler",
            "<LookupHandler>k__BackingField" },

        // — the tableextension install loop: the SAME two members as the two rows above, told
        //   apart only by the arrangement that reaches them —
        new object[] { "ext/EventTriggerData", "ext", "_tEventTriggerData", "null", 93975,
            "AL field trigger installation (table 93975)",
            "NCLMetaField.EventTriggerData",
            "nested type not found on this BC build" },
        new object[] { "ext/EventTriggerDataValue", "ext", "_fEventTriggerDataValueBacking", "null", 93976,
            "AL field trigger installation (table 93976)",
            "NCLMetaField.EventTriggerDataValue",
            "<EventTriggerDataValue>k__BackingField" },
        new object[] { "ext/OnBeforeValidateHandlers", "ext", "_pOnBeforeValidateHandlers", "null", 93977,
            "AL field trigger installation (table 93977, field 1)",
            "NCLMetaField.EventTriggerData.OnBeforeValidateHandlers",
            "OnBeforeValidateHandlers field triggers cannot be installed" },
        new object[] { "ext/OnAfterValidateHandlers", "ext", "_pOnAfterValidateHandlers", "null", 93978,
            "AL field trigger installation (table 93978, field 1)",
            "NCLMetaField.EventTriggerData.OnAfterValidateHandlers",
            "OnAfterValidateHandlers field triggers cannot be installed" },
        new object[] { "ext/ValidateHandler", "ext", "_fValidateHandlerBacking", "null", 93979,
            "AL field trigger installation (table 93979, field 2)",
            "NCLMetaField.EventTriggerData.ValidateHandler",
            "<ValidateHandler>k__BackingField" },
        new object[] { "ext/LookupHandler", "ext", "_fLookupHandlerBacking", "null", 93980,
            "AL field trigger installation (table 93980, field 2)",
            "NCLMetaField.EventTriggerData.LookupHandler",
            "<LookupHandler>k__BackingField" },
        new object[] { "ext/handler-list type", "ext", "_tFieldTriggerHandlerListClosed", "null", 93981,
            "AL field trigger installation (table 93981, field 1)",
            "List<FieldTriggerHandler<NavApplicationObjectBase>>",
            "the handler-list type could not be closed on this BC build" },

        // — BuildFieldTriggerHandler, whose Surface names the AL method rather than the table —
        new object[] { "build/FieldTriggerHandler`1", "base-sync-only", "_tFieldTriggerHandler1", "null", 93982,
            "AL field trigger FieldTriggerCallSiteSyncOnlyRecord.OnValidateSyncOnly",
            "Microsoft.Dynamics.Nav.Runtime.FieldTriggerHandler`1",
            "type not found on this BC build" },
        new object[] { "build/ctor(Type, Func<T, ValueTask>)", "base-async-only", "_tFieldTriggerHandler1", "decoy", 93983,
            "AL field trigger FieldTriggerCallSiteAsyncOnlyRecord.OnValidateAsyncOnly",
            "FieldTriggerHandler<NavApplicationObjectBase>..ctor(Type, Func<T, ValueTask>)",
            "constructor not found on this BC build" },
        new object[] { "build/ctor(Type, Action<T>)", "base-sync-only", "_tFieldTriggerHandler1", "decoy", 93984,
            "AL field trigger FieldTriggerCallSiteSyncOnlyRecord.OnValidateSyncOnly",
            "FieldTriggerHandler<NavApplicationObjectBase>..ctor(Type, Action<T>)",
            "constructor not found on this BC build" },
    };

    [SkippableTheory]
    [MemberData(nameof(InjectableSites))]
    public void MovedMember_RefusesNamingTheSite_InsteadOfSkippingTheInstall(
        string site, string arrangement, string staticName, string injection, int tableId,
        string expectedSurface, string expectedMember, string expectedDetail)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // MUST run before anything reads the statics: they are populated lazily, and reading
        // them first made sibling tests pass or NRE depending on run order.
        EnsureReflection.Invoke(null, null);

        var table = Arrange(tableId, ArrangementRecordType(arrangement));
        object? injected = injection == "decoy" ? typeof(FieldTriggerCallSiteDecoyHandler<>) : null;

        bool? returned = null;
        Exception? thrown = null;

        void Act() => WithStatics(new[] { staticName }, injected,
            () => thrown = Record.Exception(() => returned = Wire(table, tableId)));

        if (arrangement == "ext") WithExtensionRegistered(tableId, Act); else Act();

        // The failure being fixed is SILENCE, so the message reports what the un-guarded code
        // did rather than the bare xUnit "expected an exception" — which would be true of any
        // throw that has not been written yet.
        Assert.True(thrown != null,
            $"[{site}] WireFieldTriggerHandlers returned {returned} with RecordPatches.{staticName} " +
            $"{(injection == "decoy" ? "pointing at a type whose constructors moved" : "unreadable")}. " +
            "That is the silent skip #3026 replaced: the AL field trigger is never installed, nothing " +
            "is printed, and the caller is told the table was wired — so AL depending on the trigger " +
            "runs without it and still passes. Reverting this guard to `&& " + staticName + " != null` " +
            "restores exactly that, and until #3047 nothing failed when it did.");

        var gap = BcShapeGapException.Find(thrown);
        Assert.True(gap != null,
            $"[{site}] expected a BcShapeGapException, got {thrown!.GetType().Name}: {thrown.Message}");

        // Surface and Member are asserted EXACTLY, not by Contains: several rows poison the same
        // static, and only the pair distinguishes the base-table install loop from the
        // tableextension one, or one field from another.
        Assert.Equal(expectedSurface, gap!.Surface);
        Assert.Equal(expectedMember, gap.Member);
        Assert.Contains(expectedDetail, gap.Detail, StringComparison.Ordinal);
        Assert.StartsWith(BcShapeGapException.Prefix, gap.Message, StringComparison.Ordinal);
        Assert.EndsWith(" — see " + BcShapeGapException.DefaultDoc, gap.Message, StringComparison.Ordinal);

        // A shape gap is a property of which BC build is on disk, so no expect-oos manifest
        // entry may absorb it: it must not read as an out-of-scope signal.
        Assert.Null(OutOfScopeMessage.FromException(gap));
    }

    // ── 2. THE POSITIVE CONTROL ────────────────────────────────────────────────────────
    // Without it every row above could be satisfied by refusing unconditionally.

    [SkippableFact]
    public void WithNothingPoisoned_BaseAndExtensionTriggersAreBothActuallyInstalled()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EnsureReflection.Invoke(null, null);

        var etdValue = (FieldInfo)Static("_fEventTriggerDataValueBacking").GetValue(null)!;
        var validate = (FieldInfo)Static("_fValidateHandlerBacking").GetValue(null)!;
        var lookup = (FieldInfo)Static("_fLookupHandlerBacking").GetValue(null)!;
        var before = (PropertyInfo)Static("_pOnBeforeValidateHandlers").GetValue(null)!;
        var after = (PropertyInfo)Static("_pOnAfterValidateHandlers").GetValue(null)!;

        const int tableId = 93969;
        var table = Arrange(tableId, typeof(FieldTriggerCallSiteRecord));

        WithExtensionRegistered(tableId, () =>
            Assert.True(Wire(table, tableId), "wiring a table whose Record type resolved must report success"));

        // Field 1: the base table's own OnValidate, plus the extension's before/after lists.
        Assert.True(table.TryGetFieldByNo(ValidateFieldNo, out var f1));
        var etd1 = etdValue.GetValue(f1);
        Assert.NotNull(etd1);
        Assert.NotNull(validate.GetValue(etd1));
        Assert.Single((System.Collections.IEnumerable)before.GetValue(etd1)!);
        Assert.Single((System.Collections.IEnumerable)after.GetValue(etd1)!);

        // Field 2: the base table's OnLookup, and the extension's own OnValidate on the field it adds.
        Assert.True(table.TryGetFieldByNo(LookupFieldNo, out var f2));
        var etd2 = etdValue.GetValue(f2);
        Assert.NotNull(etd2);
        Assert.NotNull(lookup.GetValue(etd2));
        Assert.NotNull(validate.GetValue(etd2));
    }

    // ── 3. PROPORTIONALITY — #3041's central claim, previously untested ────────────────
    //
    // The regression this change invites is the OPPOSITE of the silent skip: over-refusing a
    // table that never needed the moved member. Each row poisons one member that #3026 moved
    // out of the method-top guard and drives a table with no field trigger and no extension.

    public static IEnumerable<object[]> ProportionalMembers() => new[]
    {
        new object[] { "_tEventTriggerData", 93985 },
        new object[] { "_fEventTriggerDataValueBacking", 93986 },
        new object[] { "_fValidateHandlerBacking", 93987 },
        new object[] { "_fLookupHandlerBacking", 93988 },
        new object[] { "_pOnBeforeValidateHandlers", 93989 },
        new object[] { "_pOnAfterValidateHandlers", 93990 },
        new object[] { "_tFieldTriggerHandlerListClosed", 93991 },
        new object[] { "_tFieldTriggerHandler1", 93992 },
    };

    [SkippableTheory]
    [MemberData(nameof(ProportionalMembers))]
    public void TriggerlessTableStillWires_WhenAMemberItNeverReadsHasMoved(string staticName, int tableId)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EnsureReflection.Invoke(null, null);
        var table = Arrange(tableId, typeof(FieldTriggerCallSiteTriggerlessRecord));

        bool? returned = null;
        Exception? thrown = null;
        WithStatics(new[] { staticName }, null,
            () => thrown = Record.Exception(() => returned = Wire(table, tableId)));

        Assert.True(thrown == null,
            $"a table with no field trigger was refused for RecordPatches.{staticName}, which its " +
            $"wiring never reads: {thrown?.GetType().Name}: {thrown?.Message}. #3026 moved this guard " +
            "out of the method-top precisely so it could not fire for a table with nothing to install; " +
            "the top guard was both louder than the fact warranted and, being an early return, silent " +
            "anyway. See docs/limitations.md#bc-shape-gaps.");
        Assert.True(returned, "a table whose Record CLR type resolved must still report success");
    }

    [SkippableFact]
    public void TriggerlessTableStillWires_WhenEveryProportionalMemberHasMovedAtOnce()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EnsureReflection.Invoke(null, null);
        const int tableId = 93993;
        var table = Arrange(tableId, typeof(FieldTriggerCallSiteTriggerlessRecord));

        var all = ProportionalMembers().Select(r => (string)r[0]).ToArray();
        Assert.Equal(8, all.Length);

        bool? returned = null;
        Exception? thrown = null;
        WithStatics(all, null, () => thrown = Record.Exception(() => returned = Wire(table, tableId)));

        Assert.True(thrown == null,
            "on a build where EVERY proportional member moved, a table with no field trigger must " +
            $"still wire — it skipped nothing. Got {thrown?.GetType().Name}: {thrown?.Message}");
        Assert.True(returned, "a table whose Record CLR type resolved must still report success");
    }

    [SkippableFact]
    public void TriggerlessTableIsStillRefusedForAScanType_BecauseThatOneIsNotProportional()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The control arm for the three tests above: without it they would all pass on a runner
        // that had stopped refusing anything at all. FieldTriggerType is what the SCAN reads, so
        // on a build missing it there is no "nothing to install" answer to give — the runner
        // cannot tell whether this table declares a trigger, and every field trigger in the
        // bundle would go quiet. It refuses for every table on purpose.
        EnsureReflection.Invoke(null, null);
        const int tableId = 93994;
        var table = Arrange(tableId, typeof(FieldTriggerCallSiteTriggerlessRecord));

        Exception? thrown = null;
        WithStatics(new[] { "_tFieldTriggerType" }, null,
            () => thrown = Record.Exception(() => Wire(table, tableId)));

        var gap = BcShapeGapException.Find(thrown);
        Assert.True(gap != null,
            $"a scan-type gap must refuse even for a trigger-less table, got {thrown?.GetType().Name}");
        Assert.Equal("Microsoft.Dynamics.Nav.Runtime.FieldTriggerType", gap!.Member);
        Assert.Equal($"AL field trigger installation (table {tableId})", gap.Surface);
    }

    // ── 4. BLAST RADIUS: a scan-type refusal aborts BUNDLE LOAD ───────────────────────

    [SkippableFact]
    public void ScanTypeGap_AbortsBundleLoad_NamingTheMember()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EnsureReflection.Invoke(null, null);

        // A table in _metaTableCache that no previous call recorded as wired, so the walk below
        // has at least one entry to reach. RequireScanType is checked BEFORE FindRecordType, so
        // the very first unwired entry refuses — which table that is does not matter and is not
        // asserted; the MEMBER is.
        var table = Arrange(AbortTableId, typeof(FieldTriggerCallSiteTriggerlessRecord));
        Assert.NotNull(table);

        Exception? thrown = null;
        WithStatics(new[] { "_tFieldTriggerType" }, null,
            () => thrown = Record.Exception(RecordPatches.WireFieldTriggerHandlersAll));

        var gap = BcShapeGapException.Find(thrown);
        Assert.True(gap != null,
            "WireFieldTriggerHandlersAll runs at bundle load (BcRuntime.SetTestAssembly, Program.cs), " +
            "so a scan-type refusal must tear out of it as a RUN-LEVEL ABORT rather than being " +
            $"absorbed into a per-table false. Got {thrown?.GetType().Name}: {thrown?.Message}. " +
            "This is the blast radius docs/limitations.md#bc-shape-gaps now states.");
        Assert.Equal("Microsoft.Dynamics.Nav.Runtime.FieldTriggerType", gap!.Member);
        Assert.Contains("every field trigger in the bundle would silently never fire", gap.Detail,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public void WithNothingPoisoned_ThePerTableEntryPointWiresWithoutRefusing()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The control for the abort test: it must not pass because these entry points always
        // throw. Scoped to the PER-TABLE entry point on this test's own table rather than the
        // bundle-wide walk, which would wire every table any sibling test left in the cache.
        EnsureReflection.Invoke(null, null);
        var table = Arrange(PerTableControlTableId, typeof(FieldTriggerCallSiteRecord));

        var wireForTable = typeof(RecordPatches).GetMethod("WireFieldTriggerHandlersForTable",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("RecordPatches.WireFieldTriggerHandlersForTable not found.");

        wireForTable.Invoke(null, new object[] { PerTableControlTableId, table });

        var etdValue = (FieldInfo)Static("_fEventTriggerDataValueBacking").GetValue(null)!;
        var validate = (FieldInfo)Static("_fValidateHandlerBacking").GetValue(null)!;
        Assert.True(table.TryGetFieldByNo(ValidateFieldNo, out var f1));
        Assert.NotNull(validate.GetValue(etdValue.GetValue(f1)!));
    }

    // ── 5. THE CENSUS — the two sites with no injectable fault, and drift for all 17 ───

    /// <summary>Every FieldTriggerShapeGap helper and how many call sites it must have on the
    /// install path. Reverting a guard to `&amp;&amp; _fXxx != null` deletes one of these calls,
    /// which is the mutation #3047 is about.</summary>
    public static readonly IReadOnlyDictionary<string, int> ExpectedCallSites = new Dictionary<string, int>
    {
        ["RequireScanType"] = 2,                       // FieldTriggerHandlerAttribute, FieldTriggerType
        ["RequireEventTriggerDataType"] = 2,           // base-table loop, tableextension loop
        ["RequireEventTriggerDataValueBacking"] = 2,   // base-table loop, tableextension loop
        ["RequireHandlerBacking"] = 4,                 // validate + lookup, in each of the two loops
        ["RequireHandlerListProperty"] = 2,            // OnBeforeValidateHandlers, OnAfterValidateHandlers
        ["RequireHandlerListType"] = 1,                // ToHandlerList
        ["HandlerConstruction"] = 4,                   // 2 absent types + 2 absent constructors
    };

    private static string InstallPathCode()
    {
        var path = Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.NclMetaTableBuilder.cs");
        Assert.True(File.Exists(path), $"{path} not found — was it renamed?");
        // Comments are stripped first: the file's headers quote the OLD `&& _fXxx != null`
        // wording on purpose, and the claim under test is about CODE.
        return string.Join('\n', File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void AllSeventeenCallSitesStillStand_SoNoneDriftedBackToASilentNullCheck()
    {
        var code = InstallPathCode();
        var actual = Regex.Matches(code, @"FieldTriggerShapeGap\.([A-Za-z]+)\(")
            .Select(m => m.Groups[1].Value)
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (helper, expected) in ExpectedCallSites)
            Assert.True(actual.TryGetValue(helper, out var found) && found == expected,
                $"FieldTriggerShapeGap.{helper} has {(actual.TryGetValue(helper, out var f) ? f : 0)} call " +
                $"site(s) on the install path, expected {expected}. A site DELETED rather than moved means " +
                "a precondition went back to being read as a default — the silent skip #3026 replaced.");

        Assert.Equal(ExpectedCallSites.Values.Sum(), actual.Values.Sum());
        Assert.Equal(17, actual.Values.Sum());

        // No helper may appear that this census does not know about, or a new site could ship
        // with no coverage and no row above.
        Assert.Empty(actual.Keys.Except(ExpectedCallSites.Keys));
    }

    [Fact]
    public void TheTwoUninjectableSitesAreStillTheOnlyTwo_AndStillReResolveTheirType()
    {
        // Why exactly two of the seventeen have no behavioural row: each reads a static that is
        // RE-RESOLVED from Ncl.dll the moment it is null, so nulling it cannot simulate a moved
        // layout. If either ever stops re-resolving, the fault becomes injectable and this test
        // says so rather than leaving the gap unnoticed.
        var code = InstallPathCode();

        // EnsureFieldTriggerReflection's own sentinel: null it and the next call repopulates it.
        Assert.Contains("if (_tFieldTriggerHandlerAttr != null) return;", code, StringComparison.Ordinal);

        // BuildFieldTriggerHandler re-resolves NavApplicationObjectBase on every null.
        Assert.Contains("if (_tNavApplicationObjectBase == null)", code, StringComparison.Ordinal);
        Assert.Contains(
            "_tNavApplicationObjectBase = navNcl.GetType(\"Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase\");",
            code, StringComparison.Ordinal);

        // Every OTHER static the seventeen guards read is written in exactly one place —
        // EnsureFieldTriggerReflection — so nulling it stays nulled for the duration of one
        // wiring call. That property is what makes the fifteen behavioural rows above possible,
        // so it is asserted rather than assumed: a second assignment anywhere would silently
        // turn one of those rows into a no-op that still passes.
        foreach (var once in new[]
                 {
                     "_tFieldTriggerType", "_tFieldTriggerHandler1", "_tEventTriggerData",
                     "_fEventTriggerDataValueBacking", "_fValidateHandlerBacking", "_fLookupHandlerBacking",
                     "_pOnBeforeValidateHandlers", "_pOnAfterValidateHandlers", "_tFieldTriggerHandlerListClosed",
                 })
        {
            var assignments = Regex.Matches(code, $@"(?<![A-Za-z0-9_]){Regex.Escape(once)}\s*=(?!=)").Count;
            Assert.True(assignments == 1,
                $"RecordPatches.{once} is assigned {assignments} time(s); the fault-injection rows above " +
                "rely on it being resolved exactly once, in EnsureFieldTriggerReflection. A second " +
                "assignment would re-resolve it mid-call and turn a negative row into a silent no-op.");
        }
    }

    // ── 6. THE WIRE FORMAT OF BOTH FACTORY FAMILIES ───────────────────────────────────

    public static IEnumerable<object[]> ShapeGapFactories() => new[]
    {
        new object[] { "RequireHandlerBacking" },
        new object[] { "RequireHandlerListProperty" },
        new object[] { "RequireHandlerListType" },
        new object[] { "RequireEventTriggerDataType" },
        new object[] { "RequireEventTriggerDataValueBacking" },
        new object[] { "RequireScanType" },
        new object[] { "HandlerConstruction" },
    };

    private static BcShapeGapException BuildShapeGap(string factory)
    {
        var target = typeof(FieldTriggerCallSiteSyncOnlyRecord).GetMethod("OnValidateSyncOnly")!;
        return factory switch
        {
            "RequireHandlerBacking" => Assert.Throws<BcShapeGapException>(
                () => FieldTriggerShapeGap.RequireHandlerBacking(null, "ValidateHandler", 42, 7)),
            "RequireHandlerListProperty" => Assert.Throws<BcShapeGapException>(
                () => FieldTriggerShapeGap.RequireHandlerListProperty(null, "OnBeforeValidateHandlers", 42, 7)),
            "RequireHandlerListType" => Assert.Throws<BcShapeGapException>(
                () => FieldTriggerShapeGap.RequireHandlerListType(null, 42, 7)),
            "RequireEventTriggerDataType" => Assert.Throws<BcShapeGapException>(
                () => FieldTriggerShapeGap.RequireEventTriggerDataType(null, 42)),
            "RequireEventTriggerDataValueBacking" => Assert.Throws<BcShapeGapException>(
                () => FieldTriggerShapeGap.RequireEventTriggerDataValueBacking(null, 42)),
            // The site with no injectable fault: its MESSAGE is pinned here even though no
            // arrangement can reach its call site on a supported build.
            "RequireScanType" => Assert.Throws<BcShapeGapException>(
                () => FieldTriggerShapeGap.RequireScanType(null, "FieldTriggerHandlerAttribute", 42)),
            // The other one, likewise.
            "HandlerConstruction" => FieldTriggerShapeGap.HandlerConstruction(
                "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase", target, "type not found"),
            _ => throw new ArgumentOutOfRangeException(nameof(factory), factory, "unknown factory"),
        };
    }

    [Theory]
    [MemberData(nameof(ShapeGapFactories))]
    public void EveryFieldTriggerShapeGapFactory_SpellsTheShapeGapWireFormat(string factory)
    {
        var ex = BuildShapeGap(factory);

        Assert.StartsWith(BcShapeGapException.Prefix, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" — see " + BcShapeGapException.DefaultDoc, ex.Message, StringComparison.Ordinal);

        // Counted on "see docs/", not on the " — see " separator: a Detail ending in its own
        // "see docs/..." leaves the separator count at 1 while rendering the link twice.
        Assert.Equal(1, ex.Message.Split("see docs/").Length - 1);
        Assert.DoesNotContain("docs/scope.md", ex.Message, StringComparison.Ordinal);

        Assert.NotEmpty(ex.Surface);
        Assert.NotEmpty(ex.Member);
        Assert.Contains(ex.Surface, ex.Message, StringComparison.Ordinal);
        Assert.Contains(ex.Member, ex.Message, StringComparison.Ordinal);

        // Never absorbable as an out-of-scope surface — a shape gap is a property of the BC
        // build on disk, not of the runner's scope.
        Assert.Null(OutOfScopeMessage.FromException(ex));

        // And it tears through AL's [TryFunction] seam rather than reading as `false`.
        Assert.Throws<BcShapeGapException>(
            () => AlRunner.BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw BuildShapeGap(factory)));
    }

    public static IEnumerable<object[]> InstallGapFactories() => new[]
    {
        new object[] { "FieldUnresolvable" },
        new object[] { "UnsupportedTriggerReturnType" },
    };

    private static RunnerOutOfScopeException BuildInstallGap(string factory)
    {
        var target = typeof(FieldTriggerCallSiteSyncOnlyRecord).GetMethod("OnValidateSyncOnly")!;
        return factory switch
        {
            "FieldUnresolvable" => FieldTriggerInstallGap.FieldUnresolvable(42, 7, "NavNCLFieldNotFoundException: no such field"),
            "UnsupportedTriggerReturnType" => FieldTriggerInstallGap.UnsupportedTriggerReturnType(target, typeof(int)),
            _ => throw new ArgumentOutOfRangeException(nameof(factory), factory, "unknown factory"),
        };
    }

    [Theory]
    [MemberData(nameof(InstallGapFactories))]
    public void EveryFieldTriggerInstallGapFactory_SpellsTheNotYetImplementedWireFormat(string factory)
    {
        // #3058 shipped these two with no claim-test table of their own; this is that table.
        const string doc = "docs/limitations.md#runtime-shape-gaps";
        var ex = BuildInstallGap(factory);

        Assert.EndsWith(" — see " + doc, ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, ex.Message.Split("see docs/").Length - 1);

        // docs/scope.md would assert a permanence that is not true of either: both are in-scope
        // surfaces the runner has not built an answer for.
        Assert.DoesNotContain("docs/scope.md", ex.Message, StringComparison.Ordinal);

        // The anchor is load-bearing, not cosmetic — ApplicationObjectBasePatches
        // .IsPermanentOutOfScope traps a refusal into `false` UNLESS the reason starts with it.
        Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);

        var signal = OutOfScopeMessage.FromException(ex);
        Assert.True(signal is { Typed: true },
            $"{factory} must be recognised as a TYPED out-of-scope signal, not merely a message that " +
            "happens to match the convention.");
        Assert.Equal(ex.Api, signal!.Value.Api);
        Assert.Equal(ex.Reason, signal.Value.Reason);

        // Not a shape gap: neither side of either disagreement is BC's, so "which BC version
        // produced this?" has no answer.
        Assert.Null(BcShapeGapException.Find(ex));

        // And it tears through [TryFunction] rather than reading as a clean `if not TryX()`.
        Assert.Throws<RunnerOutOfScopeException>(
            () => AlRunner.BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw BuildInstallGap(factory)));
    }

    // ── 7. THE DOCS COUNT THAT WENT STALE ─────────────────────────────────────────────

    [Fact]
    public void DocsCountOfRuntimeShapeGapGuards_MatchesTheCallSitesThatExist()
    {
        // docs/limitations.md#runtime-shape-gaps carried a hard-coded "Nine further guards" with
        // nothing pinning it. #3048 added three more call sites and the number stayed at nine —
        // which is how an unpinned count in prose goes stale. The counting rule is CALL SITES,
        // the same rule the original nine were counted under, and it is asserted here so the
        // next addition cannot leave the prose behind.
        var guardFiles = new[]
        {
            "UserTableTriggerPatches.cs",            // 3 — no session, no metadata, no such field
            "RecordPatches.InstallBaseline.cs",      // 1 — table not backed by TempTableDataProvider
            "RunnerTestClientSession.cs",            // 1 — modal handler asked for an unknown form handle
            "RunnerModalDispatch.cs",                // 2 — null test-execution context, null request
            "NavReportSync.cs",                      // 2 — report object, request page
        };

        var runnerShapeGapSites = guardFiles.Sum(f => Regex.Matches(
            File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Patches", f)),
            @"throw (?:AlRunner\.Patches\.)?RunnerShapeGap\.").Count);

        var installGapSites = Regex.Matches(InstallPathCode(), @"throw FieldTriggerInstallGap\.").Count;
        var total = runnerShapeGapSites + installGapSites;

        Assert.Equal(9, runnerShapeGapSites);
        Assert.Equal(3, installGapSites);
        Assert.Equal(12, total);

        var limitations = File.ReadAllText(Path.Combine(RepoRoot, "docs", "limitations.md"));
        Assert.Contains($"{total} further guards raise `RunnerOutOfScopeException`", limitations,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DocsStateTheBundleLoadBlastRadius_SoTheAbortIsNotASurprise()
    {
        var limitations = File.ReadAllText(Path.Combine(RepoRoot, "docs", "limitations.md"));
        Assert.Contains("WireFieldTriggerHandlersAll", limitations, StringComparison.Ordinal);
    }
}
