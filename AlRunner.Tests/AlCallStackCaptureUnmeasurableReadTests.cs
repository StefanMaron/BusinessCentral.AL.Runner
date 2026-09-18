// AlCallStackCaptureUnmeasurableReadTests — #4345.
//
// AlCallStackCapture.GetIsTrigger had five exits answering `false`, none of which was a
// read that succeeded. `false` there is not a neutral sentinel: it is the load-bearing
// claim "this frame is not a trigger", and the observable is AL-visible call-stack text.
// GetRelativeLine four lines down had the same shape with `-1`, but only SOME of its
// exits are unmeasurable — two of them are a frame genuinely carrying no source spans,
// which must stay the "omit the line number" pass (guards-need-a-third-state.md:
// "a genuinely absent thing must stay a pass").
//
// THE SEAM. The five exits are decided by process-global statics that EnsureReflInit sets
// once, and all of them resolve on every provisioned build, so no input reaches them.
// These tests drive the private FormatFrame directly against a hand-built NavMethodScope
// (RuntimeHelpers.GetUninitializedObject + backing-field pokes — the technique
// NavRecordGetCallerRecordTests already uses) with the cached statics pointed at real
// NavMethodScope members, then null ONE static to force the unmeasurable state. No BC
// engine, no Cecil rewrite, no bc-engine-serial membership: FormatFrame reads everything
// through those statics, so a hand-built scope is enough.
//
// WHY THE ASSERTIONS CHECK SURVIVAL, NOT JUST THE MARKER. The obvious fix — throw from
// GetIsTrigger — is a strict regression, and a test asserting only "marker present" would
// pass on it just as well as on the real fix. FormatFrame's catch (line 274) returns null,
// BuildStack appends a frame only `if (line != null)` with no marker and no counter, so a
// throw renders a stack that is complete and well-formed with a frame missing from the
// middle. Every test here therefore asserts the object name AND the method name are still
// in the rendered frame — i.e. the frame SURVIVED — alongside the marker.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// The AL object the hand-built frame reports. ParseObjectTypeAndId walks DeclaringType to
// the OUTERMOST type and parses its name, so this must be top level and named exactly
// `Codeunit<id>`. It must also be `internal`, not `public`: xUnit calls GetExportedTypes()
// during discovery, and a PUBLIC type deriving from NavRecord makes that load
// Microsoft.Dynamics.Nav.Types, which is not in the test output directory — measured, it
// aborts the whole assembly with "Catastrophic failure ... Could not load file or assembly
// 'Microsoft.Dynamics.Nav.Types'" and every test in AlRunner.Tests stops being discovered.
// `file` scoping does not work either: it mangles the metadata name to
// `<AlCallStackCaptureUnmeasurableReadTests>F8F0...__Codeunit70345`, which parses to ("?", 0).
internal sealed class Codeunit70345 : NavRecord
{
    // Never called — GetUninitializedObject skips constructors. It exists only because
    // NavRecord declares no parameterless constructor.
    private Codeunit70345() : base(null!, 0, default) { }
}

public sealed class AlCallStackCaptureUnmeasurableReadTests
{
    private const string ObjectNameInFrame = "Table 0";   // NavRecord.ObjectName on a blank record
    private const int ObjectIdInFrame = 70345;

    private static FieldInfo Static(string name)
        => typeof(AlCallStackCapture).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException(
               $"AlCallStackCapture.{name} not found — the cached-handle field was renamed.");

    private static MethodInfo FormatFrameMethod()
        => typeof(AlCallStackCapture).GetMethod("FormatFrame", BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException("AlCallStackCapture.FormatFrame not found.");

    /// <summary>
    /// A scope whose ApplicationObject is a Codeunit70345, with the cached reflection
    /// statics pointed at the real NavMethodScope members EnsureReflInit would bind.
    /// Returns the scope plus a restorer for every static it overwrote.
    /// </summary>
    private static (NavMethodScope Scope, FieldInfo FlagsField, IDisposable Restore) BuildFrame()
    {
        var appObject = (NavRecord)RuntimeHelpers.GetUninitializedObject(typeof(Codeunit70345));
        var scope = (NavMethodScope)RuntimeHelpers.GetUninitializedObject(typeof(NavTriggerMethodScope<NavRecord>));

        // NavMethodScope.ApplicationObject reads NavMethodScope<TParent>.Parent.
        var parentBackingField = typeof(NavMethodScope<NavRecord>).GetField(
            "<Parent>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "NavMethodScope<TParent>.<Parent>k__BackingField not found — Ncl shape changed.");
        parentBackingField.SetValue(scope, appObject);

        var scopeType = typeof(NavMethodScope);
        PropertyInfo Prop(string n) => scopeType.GetProperty(
            n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"NavMethodScope.{n} not found — Ncl shape changed.");

        var flagsField = scopeType.GetField("flags", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NavMethodScope.flags not found — Ncl shape changed.");

        // The span-attribute handles EnsureReflInit binds out of Ncl. They must be seeded even
        // for the trigger tests: without them GetRelativeLine reports its own third state for
        // every frame, which would mask the line-number rows below.
        var nclAssembly = typeof(NavMethodScope).Assembly;
        Type NclType(string n) => nclAssembly.GetType(n)
            ?? throw new InvalidOperationException($"{n} not found — Ncl shape changed.");
        var sourceSpansAttr = NclType("Microsoft.Dynamics.Nav.Runtime.SourceSpansAttribute");
        var signatureSpanAttr = NclType("Microsoft.Dynamics.Nav.Runtime.SignatureSpanAttribute");

        var seeded = new (string Name, object? Value)[]
        {
            ("_piApplicationObject", Prop("ApplicationObject")),
            ("_piScopeName",         Prop("ScopeName")),
            ("_piStatementNumber",   Prop("StatementNumber")),
            ("_fiMsFlags",           flagsField),
            ("_tMethodScopeFlags",   flagsField.FieldType),
            ("_piObjectName", typeof(NavApplicationObjectBase).GetProperty(
                "ObjectName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)),
            ("_tSourceSpansAttr",   sourceSpansAttr),
            ("_tSignatureSpanAttr", signatureSpanAttr),
            ("_piEncodedSpans", sourceSpansAttr.GetProperty(
                "EncodedSpans", BindingFlags.Public | BindingFlags.Instance)),
            ("_piSigEncodedSpan", signatureSpanAttr.GetProperty(
                "EncodedSpan", BindingFlags.Public | BindingFlags.Instance)),
        };

        var previous = seeded.Select(s => (s.Name, Old: Static(s.Name).GetValue(null))).ToArray();
        foreach (var (name, value) in seeded) Static(name).SetValue(null, value);

        return (scope, flagsField,
            new Restore(() => { foreach (var (name, old) in previous) Static(name).SetValue(null, old); }));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _action;
        public Restore(Action action) => _action = action;
        public void Dispose() => _action();
    }

    private static void SetTriggerFlag(NavMethodScope scope, FieldInfo flagsField, bool isTrigger)
    {
        var isTriggerValue = Convert.ToInt32(
            flagsField.FieldType.GetField("IsTrigger")!.GetRawConstantValue());
        flagsField.SetValue(scope, Enum.ToObject(flagsField.FieldType, isTrigger ? isTriggerValue : 0));
    }

    private static string? Render(NavMethodScope scope) =>
        (string?)FormatFrameMethod().Invoke(null, new object?[] { scope });

    // ── The two measured answers must keep their exact BC spellings ──────────────

    [Fact]
    public void AFrameWhoseFlagsSayTrigger_RendersBcsOwnTriggerSuffix()
    {
        var (scope, flagsField, restore) = BuildFrame();
        using (restore)
        {
            SetTriggerFlag(scope, flagsField, isTrigger: true);

            var frame = Render(scope);

            Assert.NotNull(frame);
            Assert.Contains($"(CodeUnit {ObjectIdInFrame})", frame);
            Assert.Contains("(Trigger)", frame);
            Assert.DoesNotContain(AlCallStackCapture.UnknownTriggerMarker, frame);
        }
    }

    [Fact]
    public void AFrameWhoseFlagsSayNotATrigger_RendersNoSuffixAtAll()
    {
        var (scope, flagsField, restore) = BuildFrame();
        using (restore)
        {
            SetTriggerFlag(scope, flagsField, isTrigger: false);

            var frame = Render(scope);

            Assert.NotNull(frame);
            Assert.Contains($"(CodeUnit {ObjectIdInFrame})", frame);
            Assert.DoesNotContain("(Trigger)", frame);
            Assert.DoesNotContain(AlCallStackCapture.UnknownTriggerMarker, frame);
        }
    }

    // ── The third state: unmeasurable, and the frame still survives ──────────────

    // The core #4345 case. Nulling _tMethodScopeFlags models "BC's MethodScopeFlags type
    // was not resolved" — one of the five exits that used to answer `false`. The frame
    // must carry the marker AND still name the object and the method: asserting only the
    // marker would also pass on the throw-from-GetIsTrigger regression, where FormatFrame's
    // catch drops the frame entirely and BuildStack silently omits it.
    [Theory]
    [InlineData("_tMethodScopeFlags")]
    [InlineData("_fiMsFlags")]
    public void AnUnresolvedHandle_MarksTheFrameUnknown_AndTheFrameSurvives(string handleField)
    {
        var (scope, flagsField, restore) = BuildFrame();
        using (restore)
        {
            // Seed the flag to the value a naive `false` default would coincide with, so a
            // regression to that default cannot be mistaken for a correct measured answer.
            SetTriggerFlag(scope, flagsField, isTrigger: true);
            Static(handleField).SetValue(null, null);

            var frame = Render(scope);

            // SURVIVAL — the half that discriminates the fix from the throw regression.
            Assert.NotNull(frame);
            Assert.Contains(ObjectNameInFrame, frame);
            Assert.Contains($"(CodeUnit {ObjectIdInFrame})", frame);
            Assert.Contains("NavTriggerMethodScope", frame);   // the method name, still rendered

            // CLASSIFICATION — unmeasurable is neither of BC's two answers.
            Assert.Contains(AlCallStackCapture.UnknownTriggerMarker, frame);
            Assert.DoesNotContain("(Trigger)", frame);
        }
    }

    // The marker must not be mistakable for something BC itself emits. BC writes
    // `MethodName(Trigger)`; a spelling like `(Trigger?)` would read as BC's own answer.
    [Fact]
    public void TheUnknownMarker_IsUnmistakablyRunnerSide()
    {
        var marker = AlCallStackCapture.UnknownTriggerMarker;

        Assert.Contains("al-runner", marker, StringComparison.Ordinal);
        Assert.DoesNotContain("(Trigger)", marker);
        // A bare "(Trigger" prefix would still read as BC output with a typo.
        Assert.False(marker.StartsWith("(Trigger", StringComparison.Ordinal),
            $"The unknown-trigger marker '{marker}' reads as a BC-emitted trigger suffix.");
    }

    // The diagnostic fires ONCE per process, not once per frame: the cause is a
    // process-global cached handle, so a per-frame write would print once per frame per
    // exception. Asserting the latch exists and is a single bool is what pins that.
    [Fact]
    public void TheUnknownTriggerDiagnostic_IsLatchedOncePerProcess()
    {
        var latch = typeof(AlCallStackCapture).GetField(
            "_warnedUnknownTrigger", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(latch);
        Assert.Equal(typeof(bool), latch!.FieldType);
    }

    // ── GetRelativeLine: only the UNMEASURABLE rows become the third state ───────

    // guards-need-a-third-state.md's constraint, executed rather than asserted in prose:
    // a scope type carrying no SourceSpansAttribute is GENUINELY ABSENT — it really has no
    // line number — so it must stay the existing "omit the line number" pass, with no
    // marker and no diagnostic. Collapsing it into the unmeasurable rows would swap a false
    // green for a false red. NavTriggerMethodScope<NavRecord> carries no such attribute,
    // so this is that row, reached through the ordinary path with both handles resolved.
    [Fact]
    public void AScopeTypeWithNoSourceSpans_OmitsTheLineNumber_WithNoUnknownMarker()
    {
        var (scope, flagsField, restore) = BuildFrame();
        using (restore)
        {
            SetTriggerFlag(scope, flagsField, isTrigger: false);

            var frame = Render(scope);

            Assert.NotNull(frame);
            Assert.Contains($"(CodeUnit {ObjectIdInFrame})", frame);
            Assert.DoesNotContain(" line ", frame);
            Assert.DoesNotContain(AlCallStackCapture.UnknownLineMarker, frame);
        }
    }

    // The unmeasurable row of the same method: a cached attribute-type handle that did not
    // resolve. Same survival requirement as the trigger case.
    [Theory]
    [InlineData("_tSourceSpansAttr")]
    [InlineData("_tSignatureSpanAttr")]
    public void AnUnresolvedSpanAttributeHandle_MarksTheLineUnknown_AndTheFrameSurvives(string handleField)
    {
        var (scope, flagsField, restore) = BuildFrame();
        using (restore)
        {
            SetTriggerFlag(scope, flagsField, isTrigger: false);

            var previous = Static(handleField).GetValue(null);
            try
            {
                Static(handleField).SetValue(null, null);

                var frame = Render(scope);

                Assert.NotNull(frame);
                Assert.Contains(ObjectNameInFrame, frame);
                Assert.Contains($"(CodeUnit {ObjectIdInFrame})", frame);
                Assert.Contains(AlCallStackCapture.UnknownLineMarker, frame);
                Assert.DoesNotContain(" line ", frame);
            }
            finally { Static(handleField).SetValue(null, previous); }
        }
    }

    [Fact]
    public void TheUnknownLineMarker_IsUnmistakablyRunnerSide()
    {
        var marker = AlCallStackCapture.UnknownLineMarker;

        Assert.Contains("al-runner", marker, StringComparison.Ordinal);
        // BC writes " line 12"; a marker that merely looked like one with a missing number
        // would read as BC output rather than as a runner-side classification.
        Assert.NotEqual(" line ", marker);
        Assert.False(marker.StartsWith(" line ", StringComparison.Ordinal),
            $"The unknown-line marker '{marker}' reads as a BC-emitted line number.");
    }
}
