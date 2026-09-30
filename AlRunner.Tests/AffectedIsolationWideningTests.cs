// AffectedIsolationWideningTests — #5035: which tests share state with a selected one, per isolation.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class AffectedIsolationWideningTests
{
    private static readonly string[] Discovered =
    {
        "Codeunit50100.A1", "Codeunit50100.A2", "Codeunit50100.A3",
        "Codeunit50101.B1", "Codeunit50101.B2",
        "Codeunit50102.C1",
    };

    [Fact]
    public void Codeunit_AddsEveryTestOfEachSelectedCodeunit_AndNoOther()
    {
        var selected = new HashSet<string> { "Codeunit50100.A3", "Codeunit50102.C1" };
        Assert.Equal(2, AffectedIsolationWidening.Widen(Discovered, selected, TestIsolation.Codeunit));
        Assert.Equal(new[] { "Codeunit50100.A1", "Codeunit50100.A2", "Codeunit50100.A3", "Codeunit50102.C1" },
            selected.OrderBy(x => x, StringComparer.Ordinal));
    }

    // #4826: TestIsolation = Function runs a codeunit's tests on one instance on BC (corpus PR #517,
    // Windows nightly run 36727084058), so a codeunit with AL globals or an OnRun carries state
    // across its tests; one with neither does not, and keeps per-test selection.
    [Fact]
    public void Test_WidensOnlyTheCodeunitsThatCarryState()
    {
        var selected = new HashSet<string> { "Codeunit50100.A3", "Codeunit50101.B2" };
        Assert.Equal(2, AffectedIsolationWidening.Widen(Discovered, selected, TestIsolation.Test,
            c => c == "Codeunit50100"));
        Assert.Equal(new[] { "Codeunit50100.A1", "Codeunit50100.A2", "Codeunit50100.A3", "Codeunit50101.B2" },
            selected.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Test_WithoutAnAnswer_AssumesEveryCodeunitCarriesState()
    {
        var selected = new HashSet<string> { "Codeunit50101.B2" };
        Assert.Equal(1, AffectedIsolationWidening.Widen(Discovered, selected, TestIsolation.Test));
        Assert.Equal(new[] { "Codeunit50101.B1", "Codeunit50101.B2" }, selected.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Disabled_AnySelectionIsTheWholeBundle()
    {
        var selected = new HashSet<string> { "Codeunit50101.B2" };
        Assert.Equal(5, AffectedIsolationWidening.Widen(Discovered, selected, TestIsolation.Disabled));
        Assert.Equal(Discovered.OrderBy(x => x, StringComparer.Ordinal), selected.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(TestIsolation.Codeunit)]
    [InlineData(TestIsolation.Test)]
    [InlineData(TestIsolation.Disabled)]
    public void EmptySelection_StaysEmpty(TestIsolation isolation)
    {
        var selected = new HashSet<string>();
        Assert.Equal(0, AffectedIsolationWidening.Widen(Discovered, selected, isolation));
        Assert.Empty(selected);
    }

    [Fact]
    public void EnvironmentKey_DiffersPerIsolation()
    {
        var keys = new[] { TestIsolation.Codeunit, TestIsolation.Test, TestIsolation.Disabled }
            .Select(i => AffectedIsolationWidening.EnvironmentKey("28.4|dir|pkg", i)).ToList();
        Assert.Equal(3, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.StartsWith("28.4|dir|pkg|", k, StringComparison.Ordinal));
    }
}
