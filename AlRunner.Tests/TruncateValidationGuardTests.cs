// TruncateValidationGuardTests — issue #4371.
//
// NavRecord.ValidateTruncateSupport is the whole precondition check for Record.Truncate().
// The runner replaced it with NoOp_OneArg, so all seven of BC's guards were discarded and
// Truncate() succeeded inside a try function where real BC refuses it. The replacement now
// keeps six of them and skips only the security-filter guard, which reads skeleton state that
// is not populated.
//
// The AL-OBSERVABLE half — "Record.Truncate() inside a try function raises
// 'Truncate is not supported in try functions.'" — is adjudicated upstream by a real service
// tier (corpus codeunit 60923, Truncate_NestedInsideATryFunction_IsRefused and its two
// siblings), because it is a statement about BC that would be meaningful without the runner
// existing. What is proven HERE is only what is about the runner's own replacement:
//
//   - the registered Cecil target is the faithful helper, not a no-op, so the guards run
//     at all;
//   - EnterTryScope sets IsInTryScope and ExitTryScope restores EXACTLY the prior flags,
//     so one try function cannot leave the rest of the run inside a try scope;
//   - a nested EnterTryScope does not clear the outer one's bit on exit.
//
// The scope helpers are pure functions of the reflected flags field, so these drive them
// through a hand-built scope object rather than a live session: no BC engine needed, and no
// bc-engine-serial membership.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TruncateValidationGuardTests
{
    private static FieldInfo FlagsField()
        => typeof(NavMethodScope).GetField("flags", BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new InvalidOperationException("NavMethodScope.flags not found — Ncl shape changed.");

    private static object Flag(string name) => Enum.Parse(FlagsField().FieldType, name);

    private static long Ordinal(object flags) => Convert.ToInt64(flags);

    // ── The Cecil registration: the guards must actually be wired to run ────────────────────

    // Without this the whole fix is inert: the helper below can be perfect and every guard
    // still never executes, because the rewrite still points ValidateTruncateSupport at a
    // no-op. This reads the replacement the runner exposes rather than the rewrite table,
    // which is what a caller ends up invoking.
    [Fact]
    public void ValidateTruncateSupport_IsReplacedByTheFaithfulHelper_NotANoOp()
    {
        var helper = typeof(BcRuntime).GetMethod(
            "NavRecord_ValidateTruncateSupport",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(helper);
        // One argument, matching ValidateTruncateSupport(NavRecord) — a static replacement for
        // a static one-arg method takes no extra receiver slot.
        Assert.Single(helper!.GetParameters());
        Assert.Equal(typeof(void), helper.ReturnType);

        // A no-op body is what this fix removes, so pin that the helper is not one. The IL of a
        // `{ }` method body is 1 byte (ret); anything that reads a guard is larger.
        var il = helper.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        Assert.True(il!.Length > 1,
            $"NavRecord_ValidateTruncateSupport has a {il.Length}-byte body — that is a no-op, " +
            "which is the defect #4371 fixed.");
    }

    // ── EnterTryScope / ExitTryScope ────────────────────────────────────────────────────────

    private static MethodInfo Private(string name)
        => typeof(BcRuntime).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"BcRuntime.{name} not found.");

    // BC's TryInvoke wraps the body in `using (…GetTryMethodScope())`, so the flag is set for the
    // body and cleared on the way out. The runner sets it on the scope already current, and the
    // restore has to put back exactly what was there — a scope carries other flags (IsStackFrame,
    // IsTest, …) and clearing the field wholesale would drop them.
    [Fact]
    public void ExitTryScope_RestoresTheExactPriorFlags_NotJustTheTryBit()
    {
        var flagsField = FlagsField();
        var scope = BareScope();
        var before = Flag("IsStackFrame");
        flagsField.SetValue(scope, before);

        using var session = SessionCurrentScope(scope);
        var token = EnterOn(scope);
        var during = flagsField.GetValue(scope)!;
        ExitOn(token);
        var after = flagsField.GetValue(scope)!;

        // The bit went on for the body...
        Assert.Equal(Ordinal(before) | Ordinal(Flag("IsInTryScope")), Ordinal(during));
        // ...and the scope came back byte-for-byte, keeping IsStackFrame.
        Assert.Equal(Ordinal(before), Ordinal(after));
        Assert.NotEqual(Ordinal(during), Ordinal(after));
    }

    // A try function calling another try function: the inner exit must not clear a bit the OUTER
    // one still owns, or the remainder of the outer body silently leaves the try scope and
    // Record.Truncate() stops being refused there.
    [Fact]
    public void ANestedEnterTryScope_DoesNotClearTheOuterBit_OnExit()
    {
        var flagsField = FlagsField();
        var scope = BareScope();
        flagsField.SetValue(scope, Flag("IsStackFrame"));

        using var session = SessionCurrentScope(scope);
        var outer = EnterOn(scope);
        var inner = EnterOn(scope);   // already in a try scope: nothing to do, nothing to restore
        ExitOn(inner);

        var afterInner = flagsField.GetValue(scope)!;
        Assert.True((Ordinal(afterInner) & Ordinal(Flag("IsInTryScope"))) != 0,
            "the inner try function's exit cleared the outer one's IsInTryScope bit");

        ExitOn(outer);
        Assert.Equal(Ordinal(Flag("IsStackFrame")), Ordinal(flagsField.GetValue(scope)!));
    }

    // The nested case is a no-op by construction, and that is what makes the test above hold.
    // Pinned separately so a future EnterTryScope that "helpfully" returns a live token — and
    // therefore restores on the inner exit — fails here rather than only in the AL corpus.
    [Fact]
    public void EnterTryScope_ReturnsNothingToRestore_WhenAlreadyInATryScope()
    {
        var flagsField = FlagsField();
        var scope = BareScope();
        flagsField.SetValue(scope, Enum.ToObject(
            flagsField.FieldType,
            Ordinal(Flag("IsStackFrame")) | Ordinal(Flag("IsInTryScope"))));

        using var session = SessionCurrentScope(scope);
        Assert.Null(EnterOn(scope));
    }

    // ── Guards 5 and 7 (#4374): the SHAPE these guards bind, not the BC claim ───────────────

    // The BC-observable claims -- "a delete-event subscriber refuses Truncate()" and "a filter on
    // a FlowField refuses Truncate()" -- are adjudicated upstream by a real service tier (corpus
    // codeunit 60518). What is runner-specific, and what this pins, is the RESOLUTION: guards 5
    // and 7 read BC members that must be bound before they can answer, and a bind that silently
    // fails answers "no subscriber / no filter", which permits a Truncate() BC refuses.

    // The defect this caught, and the reason the resolution moved: NCLMetaTable was resolved from
    // `recordType.Assembly`. For an AL-emitted record that is the emitted business-application
    // assembly, which contains no BC types, so the lookup returned null and guards 2, 5 and 6 --
    // every guard that reads the metatable -- were silently inert for AL tables. Measured with a
    // diagnostic in the resolver: `metaTableType=NULL` for every AL record.
    //
    // A record type declared OUTSIDE Ncl is what reproduces it, which is exactly what an
    // AL-emitted record is. NavRecord itself would pass whatever the resolver did, because its own
    // assembly IS Ncl -- so a test written against NavRecord could not have found this.
    [Fact]
    public void MetaTableShape_ResolvesForARecordTypeDeclaredOutsideNcl()
    {
        // This test's own assembly stands in for an emitted AL assembly: it is not Ncl, and it
        // has no Microsoft.Dynamics.Nav.Runtime.NCLMetaTable in it.
        Assert.Null(typeof(TruncateValidationGuardTests).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable"));

        // The MetaTable property's declared type is a BC type whatever assembly the record came
        // from, which is what the resolver now keys on.
        var metaTableProperty = typeof(NavRecord).GetProperty(
            "MetaTable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(metaTableProperty);
        Assert.Equal("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable",
            metaTableProperty!.PropertyType.FullName);

        // ...and that type really does carry the two members guards 2 and 6 read, so a resolver
        // keyed on it binds them rather than silently answering null.
        Assert.NotNull(metaTableProperty.PropertyType.GetProperty("SupportsTruncation"));
        Assert.NotNull(metaTableProperty.PropertyType.GetProperty("MediaFieldCount"));
    }

    // Guard 5 binds IsEventSubscribed off the metatable. It is declared on the BASE type
    // NCLMetaApplicationObject, so a lookup that does not walk the hierarchy binds nothing --
    // measured: the first run of this guard refused with `IsEventSubscribed=False` for exactly
    // that reason. Pinned here because the failure mode is a bind returning null, which is
    // indistinguishable from "BC removed the member" without this.
    [Fact]
    public void Guard5_IsEventSubscribed_IsDeclaredOnABaseTypeAndNeedsAHierarchyWalk()
    {
        var metaTable = typeof(NavRecord).GetProperty("MetaTable",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.PropertyType;

        bool Declares(Type t) => t.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == "IsEventSubscribed"
                      && m.ReturnType == typeof(bool)
                      && m.GetParameters().Length == 2
                      && m.GetParameters()[0].ParameterType.IsEnum);

        Assert.False(Declares(metaTable),
            "NCLMetaTable now declares IsEventSubscribed itself — the hierarchy walk in " +
            "ResolveGuard5Shape can be simplified, but check nothing else depended on the base.");

        var declaring = EnumerateHierarchy(metaTable).FirstOrDefault(Declares);
        Assert.NotNull(declaring);
        Assert.Equal("NCLMetaApplicationObject", declaring!.Name);
    }

    // Guard 5's two ordinals are OnBeforeDeleteEvent and OnAfterDeleteEvent. They are written as
    // the literals 5 and 6, matching EventSubscriberPatches.ResolveEventOrdinalFromName, which is
    // what stamps the subscriptions this guard then reads. Pinned so the two cannot drift apart:
    // if they did, the guard would read a DIFFERENT event's scope and answer false for a table
    // that does have a delete subscriber -- a silent permit.
    [Fact]
    public void Guard5_DeleteEventOrdinals_MatchWhatTheSubscriberRegistryStampsThemAs()
    {
        var metaTable = typeof(NavRecord).GetProperty("MetaTable",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.PropertyType;
        var isEventSubscribed = EnumerateHierarchy(metaTable)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .First(m => m.Name == "IsEventSubscribed"
                        && m.ReturnType == typeof(bool)
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType.IsEnum);

        var triggerEventType = isEventSubscribed.GetParameters()[0].ParameterType;

        Assert.Equal("OnBeforeDeleteEvent", Enum.GetName(triggerEventType, 5));
        Assert.Equal("OnAfterDeleteEvent", Enum.GetName(triggerEventType, 6));
    }

    // Guard 7 reads RecordImplementation.TableState.FiltersAndMarks and then two members off it.
    // Every step is bound from the PREVIOUS step's declared property type, so this pins the whole
    // chain: a rename anywhere along it must fail here rather than make the guard answer "no
    // marks, no FlowField filters" and permit a Truncate() BC refuses.
    [Fact]
    public void Guard7_FiltersAndMarksChain_BindsEveryStepItReads()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var impl = typeof(NavRecord).GetProperty("RecordImplementation", any);
        Assert.NotNull(impl);

        var tableState = impl!.PropertyType.GetProperty("TableState", any);
        Assert.NotNull(tableState);

        var filtersAndMarks = tableState!.PropertyType.GetProperty("FiltersAndMarks", any);
        Assert.NotNull(filtersAndMarks);

        var marked = filtersAndMarks!.PropertyType.GetProperty("MarkedRecords", any);
        var filters = filtersAndMarks.PropertyType.GetProperty("Filters", any);
        Assert.NotNull(marked);
        Assert.NotNull(filters);

        // The two booleans the guard actually asserts on.
        Assert.NotNull(marked!.PropertyType.GetProperty("IsCompleteExpressionLarge", any));
        Assert.NotNull(filters!.PropertyType.GetProperty("AnyFiltersOnFlowFields", any));
    }

    private static IEnumerable<Type> EnumerateHierarchy(Type? t)
    {
        for (; t != null; t = t.BaseType) yield return t;
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────

    // EnterOn/ExitOn invoke the PRODUCTION EnterTryScope/ExitTryScope. They do not reimplement
    // the flag transition: a helper that recomputed it here would pass with the real methods
    // deleted, which is a pin that reads its own copy rather than the shipped code.
    //
    // What they supply is only the state those methods read — BcRuntime's reflected-field and
    // skeleton-session statics, which ApplyAllPatches normally populates against a live engine.
    // Pointing _skeletonSession at a stand-in whose CurrentMethodScope is the caller's scope is
    // what lets these run without one.
    // Trap: ExitTryScope reads _fMsFlags too, so the swap must stay in place across BOTH calls.
    // Disposing it between them makes the restore a silent no-op — the field reads null and the
    // method returns — which surfaces as "the flags were never restored" and looks exactly like
    // a defect in ExitTryScope. Each test therefore runs its whole enter/exit sequence inside
    // one WithSession block.
    private static (object Scope, object Original)? EnterOn(object scope)
        => ((object Scope, object Original)?)Private("EnterTryScope")
            .Invoke(null, Array.Empty<object>());

    private static void ExitOn((object Scope, object Original)? token)
        => Private("ExitTryScope").Invoke(null, new object?[] { token });

    /// <summary>
    /// Points BcRuntime's <c>_fMsFlags</c> / <c>_fSessCurrentScope</c> / <c>_skeletonSession</c>
    /// statics at a one-field stand-in holding <paramref name="scope"/>, and restores every one
    /// of them on dispose so a test cannot leak session state into its neighbours.
    /// </summary>
    private static IDisposable SessionCurrentScope(object scope)
    {
        var holder = new ScopeHolder { CurrentMethodScope = scope };
        return new StaticSwap(
            (Static("_fMsFlags"), FlagsField()),
            (Static("_fSessCurrentScope"), typeof(ScopeHolder).GetField(nameof(ScopeHolder.CurrentMethodScope))!),
            (Static("_skeletonSession"), holder));
    }

    private sealed class ScopeHolder { public object? CurrentMethodScope; }

    private static FieldInfo Static(string name)
        => typeof(BcRuntime).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"BcRuntime.{name} not found.");

    private sealed class StaticSwap : IDisposable
    {
        private readonly (FieldInfo Field, object? Previous)[] _saved;

        public StaticSwap(params (FieldInfo Field, object? Value)[] assignments)
        {
            _saved = assignments.Select(a => (a.Field, a.Field.GetValue(null))).ToArray();
            foreach (var (field, value) in assignments) field.SetValue(null, value);
        }

        public void Dispose()
        {
            foreach (var (field, previous) in _saved) field.SetValue(null, previous);
        }
    }

    private static object BareScope()
        => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(NavMethodScope<NavRecord>));
}
