// PageActionVirtualTableProviderTests — issue #4147, the Page Action table (2000000143).
//
// A RUNNER-MECHANISM test, not a claim about BC. What Page Action ANSWERS is pinned upstream
// (al-language corpus, codeunit 60908). What that corpus codeunit cannot see is the wiring:
// the handout and the Cecil replacement bind BY NAME at runtime, so a BC rename is not a
// compile error — it is an empty table again, or a failure on first read.
//
// The PassesFieldFilters arity test below exists because of a real defect caught in
// development: that method is a STATIC EXTENSION on DataHelper taking SIX parameters, three of
// them C# defaults that MethodInfo.Invoke does not apply. Binding it as an instance member of
// the buffer answered null, an "optional" fallback then passed every row through unfiltered,
// and Record.SetRange silently narrowed nothing — 29 rows in, 29 out, with FindFirst answering
// the page's first row for every query. Nothing about that is visible as an exception.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageActionVirtualTableProviderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string DispatchSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.DataAccessDispatch.cs"));

    private static string RewriteSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.Runtime.cs"));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    private const string HelperName = "PageActionDataProvider_GetValuesWithinRangeForKeyField";
    private const string ProviderTypeName = "Microsoft.Dynamics.Nav.Runtime.PageActionDataProvider";

    [Fact]
    public void TheHandoutForTable2000000143_IsBcsVirtualDataAccess_NotATempStore()
    {
        var src = DispatchSource;
        var at = src.IndexOf("IsPageActionVirtualTable(table)", StringComparison.Ordinal);
        Assert.True(at >= 0, "The Page Action branch is gone from GetDataAccessForTableCore.");

        var branch = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("GetPageActionVirtualDataAccess", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("_mCreateTempDataAccess", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void BcsFactory_StillBuildsPageActionDataProviderFor2000000143()
    {
        var provider = Ncl.GetType(ProviderTypeName);
        Assert.True(provider != null, "PageActionDataProvider is gone from Ncl.");

        var instance = RuntimeHelpers.GetUninitializedObject(provider!);
        var tableId = provider!.GetProperty("TableId", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(instance);
        Assert.Equal(2000000143, tableId);
        Assert.Equal(2000000143, RecordPatches.PageActionVirtualTableId);
    }

    [Fact]
    public void TheKeyFieldWalkIsReplaced_WithAHelperOfMatchingArity()
    {
        Assert.Contains(HelperName, RewriteSource, StringComparison.Ordinal);

        var helper = typeof(RecordPatches).GetMethod(HelperName, BindingFlags.Public | BindingFlags.Static);
        Assert.True(helper != null, $"RecordPatches.{HelperName} is gone.");

        var target = Ncl.GetType(ProviderTypeName)!
            .GetMethod("GetValuesWithinRangeForKeyField", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(target != null, "PageActionDataProvider.GetValuesWithinRangeForKeyField is gone.");

        Assert.Equal(
            new[] { "NCLMetaField", "ReadOnlyRecordBuffer", "Range", "SortOrder", "FilterFieldDictionary" },
            target!.GetParameters().Select(p => p.ParameterType.Name).ToArray());
        Assert.Equal(target.GetParameters().Length + 1, helper!.GetParameters().Length);
    }

    [Fact]
    public void GetActions_IsOneOfTheTwoShapesTheHelperBuildsArgumentsFor()
    {
        // Key field 2 forwards to this rather than rebuilding rows, which is what keeps BC's
        // own nesting, parent ids, option encodings and container auto-ids.
        //
        // ITS PARAMETER LIST DIFFERS ACROSS BC VERSIONS, and an earlier version of this test
        // asserted only the 28.x shape — so it passed on a 28.x dev box while every 27.x CI leg
        // threw TargetParameterCountException. Both shapes are pinned here, and a third would
        // fail rather than being guessed at positionally.
        var m = Ncl.GetType(ProviderTypeName)!
            .GetMethod("GetActions", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(m != null, "PageActionDataProvider.GetActions is gone — key field 2 has no owner.");

        var shape = m!.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        var known = new[]
        {
            new[] { "NavInteger", "Range", "Boolean" },                                     // 28.x
            new[] { "NavInteger", "Range", "SortOrder", "FilterFieldDictionary", "Boolean" }, // 27.x
        };
        Assert.True(known.Any(k => k.SequenceEqual(shape)),
            $"GetActions has an unrecognised shape [{string.Join(", ", shape)}]; "
            + "BuildGetActionsArgs knows the 3-parameter (28.x) and 5-parameter (27.x) forms only.");

        // The last parameter is includeCustomizations on both, and the helper passes false for
        // it explicitly — MethodInfo.Invoke applies no C# defaults.
        Assert.Equal("Boolean", shape[^1]);
        Assert.True(m.GetParameters()[^1].IsOptional, "includeCustomizations is the optional trailing parameter.");
    }

    [Fact]
    public void PassesFieldFilters_IsAStaticExtensionTakingSixParameters()
    {
        // The defect this pins: binding it against the buffer type answers null, and treating
        // that null as "optional" returns every row UNFILTERED — a silent wrong answer, not a
        // failure. MethodInfo.Invoke also does not apply the three C# defaults, so the arity
        // is load-bearing: five arguments throws TargetParameterCountException at runtime.
        var dataHelper = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.DataHelper");
        Assert.True(dataHelper != null, "DataHelper is gone from Ncl.");

        var m = dataHelper!.GetMethod("PassesFieldFilters",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(m != null, "DataHelper.PassesFieldFilters is gone — the filter step has no owner.");
        Assert.True(m!.IsStatic, "PassesFieldFilters is an extension method; an instance bind answers null.");

        var ps = m.GetParameters();
        Assert.Equal(6, ps.Length);
        Assert.Equal("IRecordBuffer", ps[0].ParameterType.Name);
        Assert.Equal("FilterFieldDictionary", ps[1].ParameterType.Name);
        Assert.Equal("ISortingRulesProvider", ps[2].ParameterType.Name);
        // The three the helper must pass explicitly, with the values BC's own call site takes.
        Assert.True(ps[3].IsOptional && (bool)ps[3].DefaultValue!, "includeFlowFields defaults to true.");
        Assert.True(ps[4].IsOptional && !(bool)ps[4].DefaultValue!, "checkAgainstOriginalAndModified defaults to false.");
        Assert.True(ps[5].IsOptional && !(bool)ps[5].DefaultValue!, "bothShouldPass defaults to false.");
    }

    [Fact]
    public void TheHelpersReflectionBind_ResolvesEveryBcMemberItDrives()
    {
        var instance = RuntimeHelpers.GetUninitializedObject(Ncl.GetType(ProviderTypeName)!);

        var ensure = typeof(RecordPatches).GetMethod("EnsurePageActionReflection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var ex = Record.Exception(() => ensure.Invoke(null, new[] { instance }));
        Assert.Null(ex);

        foreach (var field in new[] { "_paGetActions", "_paToBuffer", "_paGetBounds",
                     "_paPassesFieldFilters", "_paBufferType" })
        {
            var value = typeof(RecordPatches).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);
            Assert.True(value != null, $"{field} was not bound.");
        }
    }
}
