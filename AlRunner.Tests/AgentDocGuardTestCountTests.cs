using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins the test count `.claude/agents/impl-agent.md` states beside the
/// `FullyQualifiedName~GuardTests` filter it tells every implementation agent to run.
///
/// The number drifted to a stale `# 280 tests` while the filter matched 290, and three
/// separate agents reported three different figures for it (283, 287, 290) — #4340. Two of
/// those were honest readings of something else: 287 is the PASS count on a box where the
/// engine bootstrap has not run, and a filter that also matches another class gives a
/// fourth number again.
///
/// WHY THIS IS A C# TEST AND NOT AN ENTRY IN tools/test_agent_doc_guard_counts.py
///   That guard counts FILES, which `os.listdir` settles exactly. This is a count of xunit
///   TEST CASES, and only xunit's own discovery settles it: the 27 matching classes carry
///   211 [Fact], 21 [Theory] with 75 [InlineData] — and 3 [MemberData] theories whose row
///   count exists only at runtime. A static source count gives 286, so a Python sibling
///   would have shipped a number wrong by 4 on the day it landed, which is the exact defect
///   that guard's own docstring was written about.
///
/// WHY IT ASSERTS AGAINST REFLECTION RATHER THAN A LITERAL 290
///   A hardcoded expected count is a ratchet: every added guard test reds it, and the
///   remedy looks like "raise the number" rather than "update the doc". Counting what the
///   filter actually matches means the doc is compared against the tree, so the only
///   remedy is to correct the doc — and the assertion cannot be satisfied by editing the
///   test alone.
///
/// WHAT MOVES THIS NUMBER AND WHAT DOES NOT
///   Bootstrap state does NOT. Measured on one box, one commit: un-bootstrapped Debug gives
///   `Failed: 1, Passed: 287, Skipped: 2, Total: 290`; bootstrapped Release gives
///   `Failed: 0, Passed: 290, Skipped: 0, Total: 290`. So the doc states — and this pins —
///   the TOTAL. A pin on the pass count would flap on every un-bootstrapped box, which is
///   the wrong pin and would train its readers to ignore it.
/// </summary>
public sealed class AgentDocGuardTestCountTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string Doc => Path.Combine(RepoRoot, ".claude", "agents", "impl-agent.md");

    /// <summary>
    /// The substring `dotnet test --filter FullyQualifiedName~X` matches on. Kept as a
    /// constant so the test and the documented command cannot describe different filters.
    /// </summary>
    private const string Filter = "GuardTests";

    // The documented command, with its trailing `# <N> tests` comment. Written to match the
    // filter string rather than a fixed spelling of it, so a changed filter is UNMEASURABLE
    // (below) instead of silently passing.
    private static readonly Regex DocumentedCountRx = new(
        @"--filter\s+""FullyQualifiedName~" + Filter + @"""\s*#\s*(?<n>\d+)\s+tests",
        RegexOptions.Compiled);

    /// <summary>
    /// What `dotnet test --filter "FullyQualifiedName~GuardTests"` reports as `Total:`.
    ///
    /// xunit expands a [Theory] into one case per data row, so the count is per row, not per
    /// method: [InlineData] rows are read off the attributes, [MemberData] rows by invoking
    /// the named source — which is why no static scan of the source can produce this number.
    /// </summary>
    private static int CountMatchingTestCases()
    {
        var asm = typeof(AgentDocGuardTestCountTests).Assembly;
        var total = 0;

        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || !type.IsPublic) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // xunit matches the filter against the fully-qualified test name.
                var fullyQualifiedName = $"{type.FullName}.{method.Name}";
                if (!fullyQualifiedName.Contains(Filter, StringComparison.Ordinal)) continue;

                total += CountDataRows(method);
            }
        }

        return total;
    }

    /// <summary>
    /// How many test cases one method contributes: 1 for a [Fact], one per data row for a
    /// [Theory], and 0 for a method that is neither.
    ///
    /// A row source this cannot enumerate THROWS. It must not fall back to a guessed 1: an
    /// unresolvable [MemberData] would then silently shrink the total by however many rows it
    /// really has, producing a plausible wrong number rather than a loud one — which is how
    /// the first draft of this test reported 284 against a measured 290
    /// (`guards-need-a-third-state.md`: unknown means refuse, not proceed).
    /// </summary>
    private static int CountDataRows(MethodInfo method)
    {
        var attrs = method.GetCustomAttributes(inherit: true).Cast<Attribute>().ToList();

        // Classify by INHERITANCE, the way xunit's own discoverer does — not by type-name
        // equality. [SkippableFact] derives from FactAttribute and is named neither
        // "FactAttribute" nor "TheoryAttribute", so a name-equality check drops all 6 of this
        // tree's SkippableFacts and reports 284 against a measured 290. An unrecognised shape
        // must not silently contribute 0 (#4340; `guards-need-a-third-state.md`).
        var isTheory = attrs.Any(a => a is TheoryAttribute);
        var isFact = attrs.Any(a => a is FactAttribute);
        if (!isFact) return 0;

        if (!isTheory) return 1;

        var rows = 0;
        var sawDataSource = false;

        foreach (var a in attrs)
        {
            // Again by inheritance: DataAttribute is the base xunit resolves rows through.
            if (a is InlineDataAttribute inline)
            {
                sawDataSource = true;
                rows += Math.Max(1, inline.GetData(method).Count());
                continue;
            }

            if (a is MemberDataAttribute)
            {
                sawDataSource = true;
                // The row count exists only by invoking the source, which is precisely why
                // no static scan of the source can settle this number.
                rows += CountMemberDataRows(a, method);
            }
        }

        // A [Theory] with no recognised data source is a shape this counter does not model.
        // Refuse rather than assume 1 — see the summary above.
        if (!sawDataSource)
        {
            throw new InvalidOperationException(
                $"UNMEASURABLE: [Theory] {method.DeclaringType?.FullName}.{method.Name} carries no "
                + "[InlineData] or [MemberData], so this counter cannot say how many cases it "
                + "expands to. Teach CountDataRows about the attribute rather than guessing.");
        }

        return rows;
    }

    /// <summary>
    /// Rows contributed by one [MemberData], by invoking the named source the way xunit does.
    /// Throws when the member cannot be found or does not enumerate.
    /// </summary>
    private static int CountMemberDataRows(Attribute memberData, MethodInfo testMethod)
    {
        var t = memberData.GetType();
        var memberName = t.GetProperty("MemberName")?.GetValue(memberData) as string;
        var declaring = t.GetProperty("MemberType")?.GetValue(memberData) as Type
                        ?? testMethod.DeclaringType!;

        var where = $"{declaring.FullName}.{memberName}";

        if (string.IsNullOrEmpty(memberName))
        {
            throw new InvalidOperationException(
                $"UNMEASURABLE: [MemberData] on {testMethod.DeclaringType?.FullName}."
                + $"{testMethod.Name} has no MemberName.");
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        // Order matters and is xunit's: a property, then a field, then a no-argument method.
        // TheoryData<> is returned by METHODS here as well as properties, so a property-only
        // lookup silently misses them.
        object? data = declaring.GetProperty(memberName, flags)?.GetValue(null)
                       ?? declaring.GetField(memberName, flags)?.GetValue(null)
                       ?? declaring.GetMethod(memberName, flags, binder: null, types: Type.EmptyTypes, modifiers: null)
                                   ?.Invoke(null, null);

        if (data is null)
        {
            throw new InvalidOperationException(
                $"UNMEASURABLE: [MemberData] source {where} did not resolve to a static "
                + "property, field or no-argument method on that type.");
        }

        if (data is not IEnumerable seq)
        {
            throw new InvalidOperationException(
                $"UNMEASURABLE: [MemberData] source {where} returned {data.GetType().FullName}, "
                + "which does not enumerate.");
        }

        var n = 0;
        foreach (var _ in seq) n++;
        return n;
    }

    [Fact]
    public void ImplAgentDocStatesTheGuardTestCountThisTreeActuallyHas()
    {
        Assert.True(File.Exists(Doc), $"impl-agent.md not found: {Doc}");
        var text = File.ReadAllText(Doc);

        var m = DocumentedCountRx.Match(text);

        // Third state: a regex matching nothing is NOT a pass. It means the documented
        // command was reworded and this test is measuring nothing — the failure mode
        // tools/test_agent_doc_guard_counts.py calls UNMEASURABLE.
        Assert.True(
            m.Success,
            $"UNMEASURABLE: .claude/agents/impl-agent.md no longer carries a "
            + $"`--filter \"FullyQualifiedName~{Filter}\" # <N> tests` command, so this test "
            + "is pinning nothing. Re-point the regex or drop the claim — do not delete this "
            + "assertion to make the suite green.");

        var stated = int.Parse(m.Groups["n"].Value);
        var actual = CountMatchingTestCases();

        Assert.True(
            actual > 0,
            $"UNMEASURABLE: reflection matched no test case containing '{Filter}', which "
            + "cannot be true while this very assembly defines the guard suites. The counting "
            + "method, not the doc, is what broke.");

        Assert.True(
            stated == actual,
            $".claude/agents/impl-agent.md says the `FullyQualifiedName~{Filter}` filter runs "
            + $"{stated} tests; this tree has {actual}. Update the doc — every implementation "
            + "agent reads that number before every push, and a stale one costs a round trip "
            + "deciding whether the difference is a real regression (#4340).\n"
            + "NOTE: this is the Total:, not the pass count. An un-bootstrapped box reports "
            + "fewer PASSES for the same total (BcEngineReadinessGuardTests), which is "
            + "environmental and must not change this number.");
    }
}
