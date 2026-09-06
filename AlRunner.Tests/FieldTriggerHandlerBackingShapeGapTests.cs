// FieldTriggerHandlerBackingShapeGapTests — issue #3026.
//
// THE DEFECT
// ----------
// RecordPatches resolves two of BC's private auto-property backing fields once, in
// EnsureFieldTriggerReflection:
//
//     _fValidateHandlerBacking = _tEventTriggerData.GetField("<ValidateHandler>k__BackingField", …);
//     _fLookupHandlerBacking   = _tEventTriggerData.GetField("<LookupHandler>k__BackingField", …);
//
// Both are FieldInfo? and both are null on a BC build whose NCLMetaField.EventTriggerData
// layout is not the shape that reflection was written against.
//
// The READ path over that same state already refuses: RecordPatches.TryHasFieldLookupTrigger
// is three-valued ON PURPOSE and returns null — never "no trigger" — when the read could not
// be performed, and RunnerPageInstance.RaiseSourceFieldOnLookup turns that null into a
// BcShapeGapException (#2999).
//
// The WRITE path defaulted, and did it silently: every handler install was guarded with
// `&& _fValidateHandlerBacking != null` (and the lookup equivalent), so the AL table's
// OnValidate / OnLookup field trigger was NEVER INSTALLED, nothing was printed, and
// WireFieldTriggerHandlers still returned true. AL that depends on the trigger then ran with
// no trigger at all — the silent default .claude/rules/loud-failures.md exists to prevent,
// and worse than a refusal because the test does not fail, it PASSES having skipped the
// trigger.
//
// WHAT THE NEGATIVE TESTS PROVE, AND WHY THEY ARE WORDED THE WAY THEY ARE
// ----------------------------------------------------------------------
// Each negative test captures WireFieldTriggerHandlers' return value and the handler slot's
// contents and puts BOTH into the assertion message, so the RED run reports the silence
// itself ("returned True and installed no ValidateHandler") rather than the bare xUnit
// "expected an exception". Measured on origin/main before the fix, that is exactly what both
// printed.
//
// The positive control is what stops the fix from passing by throwing unconditionally: with
// the statics untouched, the same call must still INSTALL both handlers and return true.
//
// WHY A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE OR A CORPUS TEST
// ---------------------------------------------------------------------
// No AL statement can move a private BC field. The subject is what the runner does when it
// cannot read BC's own layout — the runner's refusal contract, which
// .claude/rules/bc-behavior-tests-go-upstream.md classifies as runner-specific (same shape as
// BcShapeGapConventionTests and RunnerShapeGapClaimTests). On every supported BC build
// (verified on 27.5 and 28.1: both are `internal FieldTriggerHandler<NavApplicationObjectBase>
// { get; set; }` auto-properties, so both k__BackingFields exist) the refusal is unreachable,
// which is why the fault is injected here by nulling the cached FieldInfo rather than by
// finding a BC version that exhibits it.
//
// HOW THE FAULT IS INJECTED
// -------------------------
// The private static FieldInfo is set to null for the duration of one call and restored in a
// finally — the same state a build with a moved layout would leave EnsureFieldTriggerReflection
// in. Nothing in production is made settable for the test's benefit. The class joins
// BcEngineCollection, which is DisableParallelization, so no other test observes the window.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Stand-in for an AL-emitted <c>Record&lt;id&gt;</c> type. Never instantiated — the wiring
/// only reads its <see cref="FieldTriggerHandlerAttribute"/>s, opens a delegate over the
/// annotated methods and casts to it from <c>NavApplicationObjectBase</c>, all of which are
/// type-level operations. It derives from <see cref="NavRecord"/> because
/// <c>BuildSyncWrapper</c> emits <c>(NavApplicationObjectBase x) =&gt; inner((TConcrete)x)</c>
/// and <c>Expression.Convert</c> rejects an unrelated target type.
///
/// <para><c>internal</c>, not public, on purpose: xUnit discovery calls
/// <c>Assembly.GetExportedTypes()</c>, which resolves a PUBLIC type's whole base chain —
/// and <c>NavComplexValue</c> lives in Microsoft.Dynamics.Nav.Types.dll, which is a
/// reference-only assembly here and is not in the test bin dir. Measured: as a public type
/// this took the entire assembly down with "Catastrophic failure: ... Could not load file or
/// assembly 'Microsoft.Dynamics.Nav.Types'" before a single test ran.</para>
/// </summary>
internal sealed class FieldTriggerGapStandInRecord : NavRecord
{
    /// <summary>Never called; the wiring path needs a type, not an instance.</summary>
    internal FieldTriggerGapStandInRecord() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerHandlerBackingShapeGapTests.ValidateFieldNo)]
    public void OnValidateNo() { }

    [FieldTriggerHandler(FieldTriggerType.OnLookup, FieldTriggerHandlerBackingShapeGapTests.LookupFieldNo)]
    public void OnLookupDescription() { }
}

[Collection(BcEngineCollection.Name)]
public sealed class FieldTriggerHandlerBackingShapeGapTests : IDisposable
{
    internal const int ValidateFieldNo = 1;
    internal const int LookupFieldNo = 2;

    // One table id per test so no test observes state a sibling installed. Chosen outside
    // every other id in AlRunner.Tests (these land in the process-wide static _parsedTables
    // / _metaTableCache the whole assembly shares).
    private const int PositiveControlTableId = 93950;
    private const int ValidateGapTableId = 93951;
    private const int LookupGapTableId = 93952;

    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public FieldTriggerHandlerBackingShapeGapTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-3026-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────

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

    /// <summary>The metatable for a freshly written one-off AL table, with the stand-in
    /// Record type registered for it so <c>FindRecordType</c> resolves.</summary>
    private NCLMetaTable Arrange(int tableId)
    {
        var dir = Path.Combine(_root, tableId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Table.al"), $$"""
            table {{tableId}} "FieldTriggerGap {{tableId}}"
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

        EnsureReflection.Invoke(null, null);

        var cache = (ConcurrentDictionary<int, Type>)Static("_recordTypeCache").GetValue(null)!;
        cache[tableId] = typeof(FieldTriggerGapStandInRecord);

        return table;
    }

    /// <summary>Reads a handler slot straight off BC's own EventTriggerData, using FieldInfos
    /// captured before any fault injection, so a nulled static cannot hide the answer.</summary>
    private static object? ReadHandler(NCLMetaTable table, int fieldNo, FieldInfo etdValueBacking, FieldInfo handlerBacking)
    {
        if (!table.TryGetFieldByNo(fieldNo, out var metaField)) return null;
        var etd = etdValueBacking.GetValue(metaField);
        return etd == null ? null : handlerBacking.GetValue(etd);
    }

    // ── 1. POSITIVE CONTROL ─────────────────────────────────────────────────────────────
    // Without it the fix could pass by refusing unconditionally.

    [SkippableFact]
    public void WithBothBackingFieldsResolved_BothHandlersAreActuallyInstalled()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // MUST run before the statics below are read: they are populated lazily, and reading
        // them first made two of these tests pass or NRE depending on which sibling had
        // already run.
        EnsureReflection.Invoke(null, null);

        var etdValue = (FieldInfo)Static("_fEventTriggerDataValueBacking").GetValue(null)!;
        var validate = (FieldInfo)Static("_fValidateHandlerBacking").GetValue(null)!;
        var lookup = (FieldInfo)Static("_fLookupHandlerBacking").GetValue(null)!;

        var table = Arrange(PositiveControlTableId);

        Assert.True(Wire(table, PositiveControlTableId),
            "wiring a table whose Record type resolved must report success");

        Assert.NotNull(ReadHandler(table, ValidateFieldNo, etdValue, validate));
        Assert.NotNull(ReadHandler(table, LookupFieldNo, etdValue, lookup));
    }

    // ── 2. THE VALIDATE GAP ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public void ValidateHandlerBackingUnreadable_RefusesLoudly_InsteadOfSkippingTheInstall()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // MUST run before the statics below are read: they are populated lazily, and reading
        // them first made two of these tests pass or NRE depending on which sibling had
        // already run.
        EnsureReflection.Invoke(null, null);

        var etdValue = (FieldInfo)Static("_fEventTriggerDataValueBacking").GetValue(null)!;
        var validateStatic = Static("_fValidateHandlerBacking");
        var validate = (FieldInfo)validateStatic.GetValue(null)!;

        var table = Arrange(ValidateGapTableId);

        bool? returned = null;
        Exception? thrown;
        validateStatic.SetValue(null, null);   // BC's layout moved: the read cannot be performed
        try
        {
            thrown = Record.Exception(() => returned = Wire(table, ValidateGapTableId));
        }
        finally
        {
            validateStatic.SetValue(null, validate);
        }

        var installed = ReadHandler(table, ValidateFieldNo, etdValue, validate);
        Assert.True(thrown != null,
            $"WireFieldTriggerHandlers returned {returned} and left EventTriggerData.ValidateHandler " +
            $"{(installed == null ? "null" : "set")} — the silent skip #3026 reports: the AL field " +
            "OnValidate trigger is never installed, nothing is printed, and the caller is told the " +
            "table was wired, so AL depending on that trigger runs with no trigger and still passes.");

        var gap = BcShapeGapException.Find(thrown);
        Assert.True(gap != null,
            $"expected a BcShapeGapException, got {thrown!.GetType().Name}: {thrown.Message}");
        Assert.Equal("NCLMetaField.EventTriggerData.ValidateHandler", gap!.Member);
        Assert.Contains("<ValidateHandler>k__BackingField", gap.Message, StringComparison.Ordinal);
        Assert.Contains(ValidateGapTableId.ToString(), gap.Message, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", gap.Message, StringComparison.Ordinal);

        // A shape gap is never absorbable as an out-of-scope surface — it is a property of
        // which BC build is on disk, not of the runner's scope.
        Assert.Null(OutOfScopeMessage.FromException(gap));

        // And the refusal is not masking a partial install.
        Assert.Null(installed);
    }

    // ── 3. THE LOOKUP GAP — the sibling over the same state ─────────────────────────────

    [SkippableFact]
    public void LookupHandlerBackingUnreadable_RefusesLoudly_InsteadOfSkippingTheInstall()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // MUST run before the statics below are read: they are populated lazily, and reading
        // them first made two of these tests pass or NRE depending on which sibling had
        // already run.
        EnsureReflection.Invoke(null, null);

        var etdValue = (FieldInfo)Static("_fEventTriggerDataValueBacking").GetValue(null)!;
        var lookupStatic = Static("_fLookupHandlerBacking");
        var lookup = (FieldInfo)lookupStatic.GetValue(null)!;

        var table = Arrange(LookupGapTableId);

        bool? returned = null;
        Exception? thrown;
        lookupStatic.SetValue(null, null);
        try
        {
            thrown = Record.Exception(() => returned = Wire(table, LookupGapTableId));
        }
        finally
        {
            lookupStatic.SetValue(null, lookup);
        }

        var installed = ReadHandler(table, LookupFieldNo, etdValue, lookup);
        Assert.True(thrown != null,
            $"WireFieldTriggerHandlers returned {returned} and left EventTriggerData.LookupHandler " +
            $"{(installed == null ? "null" : "set")} — the same silent skip on the lookup half. " +
            "RecordPatches.TryHasFieldLookupTrigger refuses over exactly this FieldInfo; the write " +
            "path must not default where the read path declines to answer.");

        var gap = BcShapeGapException.Find(thrown);
        Assert.True(gap != null,
            $"expected a BcShapeGapException, got {thrown!.GetType().Name}: {thrown.Message}");
        Assert.Equal("NCLMetaField.EventTriggerData.LookupHandler", gap!.Member);
        Assert.Contains("<LookupHandler>k__BackingField", gap.Message, StringComparison.Ordinal);
        Assert.Contains(LookupGapTableId.ToString(), gap.Message, StringComparison.Ordinal);
        Assert.Null(OutOfScopeMessage.FromException(gap));
        Assert.Null(installed);
    }
}
