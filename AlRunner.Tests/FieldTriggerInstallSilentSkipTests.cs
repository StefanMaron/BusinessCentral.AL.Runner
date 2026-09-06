// FieldTriggerInstallSilentSkipTests — issue #3048.
//
// THE DEFECT: TWO SILENT SKIPS LEFT ON THE FIELD-TRIGGER INSTALL PATH
// ------------------------------------------------------------------
// #3026 converted seventeen refusals on this path from silent skips to named
// BcShapeGapExceptions. Two skips on the same rewritten lines survived, because neither is a
// question about BC's layout and so BcShapeGapException is the wrong type for either:
//
//   1. `catch { continue; }` around NCLMetaTable.GetFieldByNo, in BOTH install loops —
//      RecordPatches.WireFieldTriggerHandlers (the base table's own OnValidate/OnLookup) and
//      RecordPatches.WireExtensionValidateHandlers (a tableextension's before/after lists and
//      the OnValidate/OnLookup of fields it ADDS). A field the metatable does not carry was
//      skipped with nothing recorded, and the caller was still told the table was wired.
//
//   2. BuildFieldTriggerHandler returning null for a trigger method whose return type is
//      neither void nor ValueTask. It wrote one line to stderr and returned null; all four
//      call sites then skipped the install with `if (handler != null)` / `if (… == null)
//      continue;` — and WireFieldTriggerHandlers STILL returned true, its contract for "this
//      table is wired, do not retry". A successful-looking install that installed nothing.
//
// WHY THE NEGATIVE TESTS ARE WORDED THE WAY THEY ARE
// --------------------------------------------------
// The failure being fixed is SILENCE, so each negative test captures WireFieldTriggerHandlers'
// return value (and, where the field exists to read, the handler slot) and puts both into the
// assertion message. On origin/main the RED run therefore reports the silence itself —
// "returned True and installed no ValidateHandler" — rather than the bare xUnit "expected an
// exception", which would be true of any not-yet-written throw.
//
// WHAT `catch { continue; }` WAS ACTUALLY ABSORBING
// -------------------------------------------------
// Decompiled from the pristine service-tier Microsoft.Dynamics.Nav.Ncl.dll (28.1.49838.54308),
// NCLMetaTable.GetFieldByNo(int fieldNo, bool trapError = false) has exactly three outcomes
// when trapError is false: it returns the field (or a DisabledFields entry, which is also
// non-null), it throws NavNCLFieldNotFoundException naming the field and the table caption, or
// it throws InvalidOperationException("field is null") for a hole in AllFields. It CANNOT
// return null — so the `if (metaField == null) continue;` that followed each catch was dead on
// every supported build, and the catch itself could only ever be swallowing one of those two
// throws. Neither is a legitimate condition: both mean the runner's own metatable does not
// carry a field its own emitted AL declares a trigger for.
//
// Measured rather than assumed: with the three sites instrumented to print instead of skip,
// the al-language corpus (2599 tests) and tests/runner-extras (298 tests) executed 253 table
// wirings covering 2,592 base-table field installs and 55 tableextension field installs, and
// produced ZERO hits on any of the three. Nothing legitimate reaches them, which is why the
// fix is a refusal rather than a narrower catch.
//
// WHY RunnerOutOfScopeException AND NOT BcShapeGapException
// ---------------------------------------------------------
// Neither refusal is a property of the BC build on disk — a shape gap's message asks the
// reader "which BC version produced this?", and that is the wrong question for both. An
// unresolvable field is the runner's own metatable disagreeing with the runner's own emitted
// AL; an unsupported return type is a property of the AL the runner EMITTED. Both are in-scope
// surfaces the runner has not built an answer for, which is the case
// .claude/rules/loud-failures.md assigns to RunnerOutOfScopeException with the reason anchor
// `not-yet-implemented` — the anchor that stops an AL [TryFunction] absorbing the gap into
// `false`.
//
// WHY A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE OR A CORPUS TEST
// ----------------------------------------------------------------------
// The subject is the runner's own install path over its own emitted AL, and the refusal
// contract it must honour when it cannot install a trigger. No statement about Business
// Central is being made, so there is nothing for a service tier to adjudicate — the same
// classification .claude/rules/bc-behavior-tests-go-upstream.md gives
// FieldTriggerHandlerBackingShapeGapTests, whose harness this file reuses.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Extensions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Stand-in <c>Record&lt;id&gt;</c> whose two triggers use the two return types
/// <c>BuildFieldTriggerHandler</c> supports — <c>void</c> and <c>ValueTask</c>. The positive
/// control: without it the fix could pass by refusing unconditionally.
///
/// <para><c>internal</c>, not public, for the reason spelled out on
/// <c>FieldTriggerGapStandInRecord</c>: xUnit discovery resolves a PUBLIC type's whole base
/// chain, and <c>NavComplexValue</c> lives in a reference-only assembly that is not in the
/// test bin dir.</para>
/// </summary>
internal sealed class FieldTriggerInstallSupportedReturnsRecord : NavRecord
{
    internal FieldTriggerInstallSupportedReturnsRecord() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerInstallSilentSkipTests.ValidateFieldNo)]
    public void OnValidateNo() { }

    [FieldTriggerHandler(FieldTriggerType.OnLookup, FieldTriggerInstallSilentSkipTests.LookupFieldNo)]
    public ValueTask OnLookupDescription() => default;
}

/// <summary>Declares a trigger for a field number the table does not have.</summary>
internal sealed class FieldTriggerInstallUnresolvableFieldRecord : NavRecord
{
    internal FieldTriggerInstallUnresolvableFieldRecord() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerInstallSilentSkipTests.GhostFieldNo)]
    public void OnValidateGhost() { }
}

/// <summary>Declares a trigger the runner cannot wrap: the return type is neither void nor ValueTask.</summary>
internal sealed class FieldTriggerInstallBadReturnTypeRecord : NavRecord
{
    internal FieldTriggerInstallBadReturnTypeRecord() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnValidate, FieldTriggerInstallSilentSkipTests.ValidateFieldNo)]
    public int OnValidateNo() => 0;
}

/// <summary>No field triggers of its own, so wiring falls straight through to the
/// tableextension path — which is where this record's table is interesting.</summary>
internal sealed class FieldTriggerInstallNoTriggerRecord : NavRecord
{
    internal FieldTriggerInstallNoTriggerRecord() : base(null!, 0) { }
}

/// <summary>
/// Stand-in <c>TableExtension&lt;id&gt;</c>, declaring a <c>modify(field)</c>-style
/// <c>OnBeforeValidate</c> for a field number the base table does not have. Never
/// instantiated — <c>WireExtensionValidateHandlers</c> only reads the type's attributes and
/// opens delegates over the annotated methods.
/// </summary>
internal sealed class FieldTriggerInstallStandInExtension : NavRecordExtension
{
    internal FieldTriggerInstallStandInExtension() : base(null!, 0) { }

    [FieldTriggerHandler(FieldTriggerType.OnBeforeValidate, FieldTriggerInstallSilentSkipTests.GhostFieldNo)]
    public void OnBeforeValidateGhost() { }
}

[Collection(BcEngineCollection.Name)]
public sealed class FieldTriggerInstallSilentSkipTests : IDisposable
{
    internal const int ValidateFieldNo = 1;
    internal const int LookupFieldNo = 2;

    /// <summary>A field number no table written by this class declares.</summary>
    internal const int GhostFieldNo = 4242;

    // One table id per test so no test observes state a sibling installed. Chosen outside every
    // other id in AlRunner.Tests (these land in the process-wide static _parsedTables /
    // _metaTableCache the whole assembly shares).
    private const int SupportedReturnsTableId = 93960;
    private const int UnresolvableFieldTableId = 93961;
    private const int BadReturnTypeTableId = 93962;
    private const int ExtensionGhostFieldTableId = 93963;
    private const int StandInExtensionId = 93964;

    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public FieldTriggerInstallSilentSkipTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-3048-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── plumbing (same shape as FieldTriggerHandlerBackingShapeGapTests) ────────────────

    private static FieldInfo Static(string name) =>
        typeof(RecordPatches).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? typeof(RecordPatches).GetField(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
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

    private static string TableName(int tableId) => $"FieldTriggerInstall {tableId}";

    /// <summary>The metatable for a freshly written one-off AL table, with <paramref name="recordType"/>
    /// registered as its Record type so <c>FindRecordType</c> resolves.</summary>
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

        // The table must genuinely not carry the ghost field, or the negative tests would be
        // asserting over a table that never reaches the unresolvable-field path at all.
        Assert.False(table.TryGetFieldByNo(GhostFieldNo, out _),
            $"table {tableId} unexpectedly carries field {GhostFieldNo} — the negative tests need it absent.");

        EnsureReflection.Invoke(null, null);

        var cache = (ConcurrentDictionary<int, Type>)Static("_recordTypeCache").GetValue(null)!;
        cache[tableId] = recordType;

        return table;
    }

    /// <summary>Reads a handler slot straight off BC's own EventTriggerData.</summary>
    private static object? ReadHandler(NCLMetaTable table, int fieldNo, FieldInfo etdValueBacking, FieldInfo handlerBacking)
    {
        if (!table.TryGetFieldByNo(fieldNo, out var metaField)) return null;
        var etd = etdValueBacking.GetValue(metaField);
        return etd == null ? null : handlerBacking.GetValue(etd);
    }

    private static void AssertNotYetImplementedRefusal(Exception thrown, string apiFragment, string detailFragment)
    {
        var signal = OutOfScopeMessage.FromException(thrown);
        Assert.True(signal is { Typed: true },
            $"expected a typed RunnerOutOfScopeException, got {thrown.GetType().Name}: {thrown.Message}");
        Assert.Contains(apiFragment, signal!.Value.Api, StringComparison.Ordinal);
        Assert.StartsWith("not-yet-implemented", signal.Value.Reason, StringComparison.Ordinal);
        Assert.Contains(detailFragment, thrown.Message, StringComparison.Ordinal);

        // Not a shape gap: neither refusal is a property of which BC build is on disk, and the
        // shape-gap message would send the reader to "which BC version produced this?".
        Assert.Null(BcShapeGapException.Find(thrown));
    }

    // ── 1. POSITIVE CONTROL — both supported return types still install ─────────────────

    [SkippableFact]
    public void VoidAndValueTaskTriggers_AreBothActuallyInstalled_AndWiringReportsSuccess()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // MUST run before the statics below are read: they are populated lazily.
        EnsureReflection.Invoke(null, null);

        var etdValue = (FieldInfo)Static("_fEventTriggerDataValueBacking").GetValue(null)!;
        var validate = (FieldInfo)Static("_fValidateHandlerBacking").GetValue(null)!;
        var lookup = (FieldInfo)Static("_fLookupHandlerBacking").GetValue(null)!;

        var table = Arrange(SupportedReturnsTableId, typeof(FieldTriggerInstallSupportedReturnsRecord));

        Assert.True(Wire(table, SupportedReturnsTableId),
            "wiring a table whose Record type resolved must report success");

        Assert.NotNull(ReadHandler(table, ValidateFieldNo, etdValue, validate));   // void
        Assert.NotNull(ReadHandler(table, LookupFieldNo, etdValue, lookup));       // ValueTask
    }

    // ── 2. THE UNRESOLVABLE FIELD, BASE-TABLE LOOP ─────────────────────────────────────

    [SkippableFact]
    public void FieldTheMetatableDoesNotCarry_RefusesNamingIt_InsteadOfContinuingPastIt()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EnsureReflection.Invoke(null, null);
        var table = Arrange(UnresolvableFieldTableId, typeof(FieldTriggerInstallUnresolvableFieldRecord));

        bool? returned = null;
        var thrown = Record.Exception(() => returned = Wire(table, UnresolvableFieldTableId));

        Assert.True(thrown != null,
            $"WireFieldTriggerHandlers returned {returned} for a table whose only AL field trigger " +
            $"targets field {GhostFieldNo}, which its metatable does not carry. GetFieldByNo threw " +
            "NavNCLFieldNotFoundException, `catch { continue; }` swallowed it, and the caller was told " +
            "the table was wired — so the OnValidate trigger never fires, nothing is printed, and AL " +
            "depending on it passes having run without it (#3048).");

        AssertNotYetImplementedRefusal(thrown!, $"field {GhostFieldNo}", UnresolvableFieldTableId.ToString());
    }

    // ── 3. THE UNRESOLVABLE FIELD, TABLEEXTENSION LOOP — the sibling over the same state ─

    [SkippableFact]
    public void TableExtensionFieldTheMetatableDoesNotCarry_RefusesNamingIt_InsteadOfContinuingPastIt()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EnsureReflection.Invoke(null, null);
        var table = Arrange(ExtensionGhostFieldTableId, typeof(FieldTriggerInstallNoTriggerRecord));

        // Register a tableextension for this base table without going through AL parsing: the
        // subject is the install loop, not extension discovery. Both statics are process-global,
        // so both are removed again in the finally — the class is DisableParallelization
        // (BcEngineCollection), so no sibling observes the window.
        var extIds = (Dictionary<string, List<int>>)Static("_extensionIdsByBaseTable").GetValue(null)!;
        var extTypes = (ConcurrentDictionary<int, Type>)Static("_tableExtensionTypeCache").GetValue(null)!;
        var key = TableName(ExtensionGhostFieldTableId).ToLowerInvariant();

        bool? returned = null;
        Exception? thrown;
        lock (extIds)
            extIds[key] = new List<int> { StandInExtensionId };
        extTypes[StandInExtensionId] = typeof(FieldTriggerInstallStandInExtension);
        try
        {
            thrown = Record.Exception(() => returned = Wire(table, ExtensionGhostFieldTableId));
        }
        finally
        {
            lock (extIds) extIds.Remove(key);
            extTypes.TryRemove(StandInExtensionId, out _);
        }

        Assert.True(thrown != null,
            $"WireFieldTriggerHandlers returned {returned} for a table whose tableextension declares an " +
            $"OnBeforeValidate for field {GhostFieldNo}, which the base metatable does not carry. The " +
            "second `catch { continue; }` — in WireExtensionValidateHandlers, over the same GetFieldByNo " +
            "call — swallowed the same exception, so issue #1835's shape (an extension trigger that never " +
            "fires) came back silently on this half (#3048).");

        AssertNotYetImplementedRefusal(thrown!, $"field {GhostFieldNo}", ExtensionGhostFieldTableId.ToString());
    }

    // ── 4. THE UNSUPPORTED RETURN TYPE ─────────────────────────────────────────────────

    [SkippableFact]
    public void TriggerReturnTypeTheRunnerCannotWrap_RefusesNamingTheMethod_InsteadOfReportingSuccess()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        EnsureReflection.Invoke(null, null);

        var etdValue = (FieldInfo)Static("_fEventTriggerDataValueBacking").GetValue(null)!;
        var validate = (FieldInfo)Static("_fValidateHandlerBacking").GetValue(null)!;

        var table = Arrange(BadReturnTypeTableId, typeof(FieldTriggerInstallBadReturnTypeRecord));

        bool? returned = null;
        var thrown = Record.Exception(() => returned = Wire(table, BadReturnTypeTableId));

        var installed = ReadHandler(table, ValidateFieldNo, etdValue, validate);
        Assert.True(thrown != null,
            $"WireFieldTriggerHandlers returned {returned} and left EventTriggerData.ValidateHandler " +
            $"{(installed == null ? "null" : "set")}. BuildFieldTriggerHandler declined the Int32-returning " +
            "trigger, wrote one line to stderr, returned null — and every call site skipped the install " +
            "while the caller still reported the table as wired: a successful-looking install that " +
            "installed nothing (#3048).");

        AssertNotYetImplementedRefusal(thrown!, "OnValidateNo", "Int32");

        // And the refusal is not masking a partial install.
        Assert.Null(installed);
    }
}
