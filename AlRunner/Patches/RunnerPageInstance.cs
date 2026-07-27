// RunnerPageInstance — a live AL page object behind a TestPage.
//
// WHY
//   The runner's TestPage was a record cursor: it mapped only controls bound to a Rec
//   field, and on a miss passed the control's own id to the record as a field number,
//   producing "The supplied field number '1167935535' cannot be found in the 'X' table"
//   — a control-name FNV hash landing where a field number was expected.
//
//   A control does not have to bind to a table field. Binding one to a page global
//   variable is ordinary AL and the standard shape for a mode/filter selector above a
//   repeater. Resolving those needs the page's own control -> value binding table, which
//   only exists on an initialised NavForm: BC publishes it as NavForm.SourceExpressions,
//   keyed "Control{controlId}".
//
// WHAT THIS DOES
//   Constructs the compiled Page{id} (a real NavForm subclass carrying the page's AL
//   triggers as methods), opts it into BC's real initialisation (RunnerFormInit), and runs
//   the three initialisation steps that matter:
//
//     SetSourceTable(record, true)        bind the page to the TestPage's record
//     SetFieldsFromControlsMetadata()     resolve controls against the source table
//     OnMetadataLoaded()                  the page's OWN generated method — this is what
//                                         registers the source expressions
//
//   NOT NavForm.InitializeFromMetadata, which wraps those three in steps the runner cannot
//   satisfy (UpdateAllowedOperationsFromPermissions / customization-control expressions /
//   currency-column registration all reach for service-tier state) and NREs before
//   reaching them. Calling the three directly is not a shortcut around BC's logic — it is
//   BC's logic, minus the parts that need a service tier.
//
// SCOPE
//   Only pages the runner compiled itself have the metadata to build a control tree from
//   (see AlPageMetadataRegistry). For anything else TryCreate returns null and the caller
//   keeps its record-only behaviour, which is exactly what it had before.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunnerV2.Patches;

internal sealed class RunnerPageInstance
{
    private readonly object _form;
    private readonly System.Collections.IDictionary _sourceExpressions;

    private RunnerPageInstance(object form, System.Collections.IDictionary sourceExpressions)
    {
        _form = form;
        _sourceExpressions = sourceExpressions;
    }

    internal object Form => _form;

    /// <summary>
    /// Build and initialise the AL page object for <paramref name="pageId"/>, bound to
    /// <paramref name="record"/>. Returns null when the page has no compiled type or no
    /// real metadata — never a half-initialised instance, because a page whose source
    /// expressions were not registered would answer control lookups with silence rather
    /// than with the page's actual bindings.
    /// </summary>
    internal static RunnerPageInstance? TryCreate(object parent, int pageId, NavRecord record)
    {
        if (RecordPatches.EnsureRealPageMetadata(pageId) == null)
        {
            // stdout on purpose throughout this class: the test-execution child's stderr is
            // not captured, so a Console.Error line would be invisible exactly when needed.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: no emit-captured metadata, so no control tree; "
                    + "TestPage stays record-only");
            return null;
        }

        var pageType = FindPageType(pageId);
        if (pageType == null)
        {
            Console.Out.WriteLine($"[RunnerPageInstance] page {pageId}: no compiled Page{pageId} type found");
            return null;
        }

        var ctor = pageType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2
                              && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));
        if (ctor == null)
        {
            Console.Out.WriteLine($"[RunnerPageInstance] page {pageId}: Page{pageId} has no (ITreeObject, NavRecord) ctor");
            return null;
        }

        try
        {
            var form = ctor.Invoke(new object?[] { parent, record });
            // Must precede every step below: the guarded NavForm bodies (GetMasterPage,
            // RegisterSourceExpression, …) check this and no-op for anyone else.
            RunnerFormInit.MarkRealInit(form);

            Invoke(form, "SetSourceTable", new object?[] { record, true });
            Invoke(form, "SetFieldsFromControlsMetadata", Array.Empty<object?>());
            Invoke(form, "OnMetadataLoaded", Array.Empty<object?>());

            var expressions = ReadProperty(form, "SourceExpressions") as System.Collections.IDictionary;
            if (expressions == null)
            {
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: the page object initialised but published no "
                    + "source-expression table; TestPage falls back to record-only access");
                return null;
            }
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[RunnerPageInstance] page {pageId}: built, {expressions.Count} source expression(s): "
                    + string.Join(", ", expressions.Keys.Cast<object>().Select(k => k?.ToString())));
            return new RunnerPageInstance(form, expressions);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            // Loud, but not fatal: the caller falls back to record-only behaviour, which is
            // strictly what it had before this existed. Silence here would turn a page-object
            // failure into "that control does not exist", which is a different and wronger
            // answer than "the runner could not build this page".
            // stdout on purpose: the test-execution child's stderr is not captured, so a
            // Console.Error line here would be invisible exactly when it is needed.
            Console.Out.WriteLine(
                $"[RunnerPageInstance] page {pageId}: could not build the AL page object "
                + $"({inner.GetType().Name}: {inner.Message}); TestPage falls back to record-only access");
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                Console.Out.WriteLine(inner.StackTrace);
            return null;
        }
    }

    /// <summary>
    /// The page's binding for a control id, or null when the control is not one the page
    /// publishes a source expression for (Rec-bound controls are resolved by the caller
    /// against the record instead).
    /// </summary>
    internal object? TryGetSourceExpression(int controlId)
        => _sourceExpressions[SourceExpressionKey(controlId)];

    /// <summary>BC's key convention for a control's source expression.</summary>
    internal static string SourceExpressionKey(int controlId) => "Control" + controlId;

    internal static NavValue? GetValue(object expression)
        => (NavValue?)expression.GetType()
            .GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null)!
            .Invoke(expression, null);

    internal static void SetValue(object expression, NavValue value)
        => expression.GetType()
            .GetMethod("Set", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(NavValue) }, modifiers: null)!
            .Invoke(expression, new object?[] { value });

    /// <summary>
    /// Run the control's OnValidate trigger, if it declares one. The AL compiler emits it
    /// as <c>{ControlName}_a{n}_OnValidate</c> on the page class; the control name comes
    /// from the source expression itself rather than from re-parsing the AL, so this
    /// tracks whatever the compiler actually emitted. A control with no OnValidate simply
    /// has no such method, which is not an error.
    /// </summary>
    internal void RaiseOnValidate(object expression)
    {
        var controlName = ReadProperty(expression, "Name") as string;
        if (string.IsNullOrEmpty(controlName)) return;

        var prefix = controlName + "_";
        var candidates = _form.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.GetParameters().Length == 0
                     && m.Name.EndsWith("_OnValidate", StringComparison.Ordinal)
                     && m.Name.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0) return;
        if (candidates.Count > 1)
            throw new AlRunnerV2.Infrastructure.RunnerOutOfScopeException(
                $"TestPage OnValidate ({controlName})",
                $"testpage-onvalidate — {candidates.Count} methods match '{prefix}*_OnValidate' on "
                + $"{_form.GetType().Name}; the runner cannot tell which trigger belongs to this "
                + "control. See docs/scope.md");

        try { candidates[0].Invoke(_form, null); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // An Error() inside the AL trigger is the trigger's own outcome, not a runner
            // failure — rethrow it unwrapped so the AL stack survives.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    private static Type? FindPageType(int pageId)
    {
        var name = "Page" + pageId;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }
            foreach (var t in types)
                if (t != null && t.Name == name && typeof(NavForm).IsAssignableFrom(t))
                    return t;
        }
        return null;
    }

    private static void Invoke(object form, string methodName, object?[] args)
    {
        for (var t = form.GetType(); t != null; t = t.BaseType)
        {
            var mi = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
            if (mi == null) continue;
            mi.Invoke(form, args);
            return;
        }
        throw new InvalidOperationException(
            $"NavForm.{methodName} not found on {form.GetType().FullName} — BC page shape changed");
    }

    private static object? ReadProperty(object target, string name)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (pi != null) return pi.GetValue(target);
        }
        return null;
    }
}
