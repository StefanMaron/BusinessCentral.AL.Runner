// ExpectedRunnerAbortGuardTests — #5283. A runner child that aborts on purpose must start with the
// CI crash-dump switch off (ExpectedRunnerAbort.cs says why), and this pins it WITHOUT running an abort.
//
// THE SET: every static method carrying [ExpectedRunnerAbort], read by reflection. The guard CALLS each
// one and reads the ProcessStartInfo it returns, so the claim is the start info's own environment, not a
// spelling in the source. Two derivations keep the set honest: reflection must agree with a source count
// of the marker (a spelling reflection cannot see, or a marker on something that is not a method, fails
// instead of dropping out of the set), and a test source that expects exit 134/139 must carry the marker.
//
// NOT CAUGHT: a child that aborts and whose test asserts something looser (`exit != 0`,
// ExplicitEngineMinorWarningOncePerInvocationTests' shape) and is not marked; an unmarked class cannot be
// told from one whose child never aborts. A new such child is found by running its class with
// DOTNET_DbgEnableMiniDump=1 in the host environment and looking for a coredump (docs/test-isolation.md).
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExpectedRunnerAbortGuardTests
{
    private static readonly string TestsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ""));

    // The files that name the marker as a definition or as this guard's own subject; every other
    // mention is a use.
    private static readonly string[] NotUses = { "ExpectedRunnerAbort.cs", "ExpectedRunnerAbortGuardTests.cs" };

    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Regex Marker = new(@"\bExpectedRunnerAbort(Attribute)?\b", RegexOptions.Compiled);

    // A positive expectation of exit 134/139. `Assert.NotEqual(134, ...)` (the `\b` before `Equal` is what excludes it), `!= 134`, a number inside a
    // string, `Codeunit134043` and comments are not one: those tests guard against the abort.
    private static readonly Regex AbortExit = new(
        @"==\s*13[49]\b|\b13[49]\s*==|\bEqual\(\s*13[49]\b|\bis\s+13[49]\b|\bor\s+13[49]\b|\bInlineData\(\s*13[49]\b",
        RegexOptions.Compiled);

    private static string CodeOf(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) return "";
        return Regex.Replace(line, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
    }

    internal static bool ExpectsAbortExit(string line) => AbortExit.IsMatch(CodeOf(line));

    /// <summary>Why <paramref name="psi"/> would let a crash dump be written; null when it would not.</summary>
    internal static string? Violation(ProcessStartInfo psi) =>
        psi.Environment.TryGetValue(CrashDump.SwitchVariable, out var v) && v == "0"
            ? null
            : $"{CrashDump.SwitchVariable} is "
              + (psi.Environment.TryGetValue(CrashDump.SwitchVariable, out var w) ? $"'{w}'" : "absent")
              + " in the child's environment, so it inherits the CI job's value";

    private static List<MethodInfo> MarkedMethods() =>
        typeof(ExpectedRunnerAbortAttribute).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(AllDeclared))
            .Where(m => m.GetCustomAttribute<ExpectedRunnerAbortAttribute>() is not null)
            .OrderBy(m => m.DeclaringType!.FullName).ThenBy(m => m.Name)
            .ToList();

    private static List<(string File, string[] Lines)> SourceFiles()
    {
        var files = Directory.GetFiles(TestsDir, "*.cs", SearchOption.TopDirectoryOnly);
        Assert.True(files.Any(f => Path.GetFileName(f) == "ExpectedRunnerAbortGuardTests.cs"),
            $"{TestsDir} does not hold this guard's own source, so the census would read the wrong directory.");
        return files.Select(f => (Path.GetFileName(f), File.ReadAllLines(f))).ToList();
    }

    private static int Uses(string[] lines) =>
        lines.Sum(l => Marker.Matches(CodeOf(l)).Count);

    private static object?[] DummyArguments(MethodInfo m) =>
        m.GetParameters().Select(p =>
            p.ParameterType == typeof(string) ? (object?)"/unused"
            : p.ParameterType == typeof(string[]) ? Array.Empty<string>()
            : throw new InvalidOperationException(
                $"{m.DeclaringType!.Name}.{m.Name} takes a {p.ParameterType.Name} parameter; a marked builder takes only " +
                "string and string[], because the guard has to call it without a real bundle.")).ToArray();

    [Fact]
    public void EveryMarkedBuilder_StartsItsChildWithTheCrashDumpSwitchOff()
    {
        var marked = MarkedMethods();
        Assert.True(marked.Count > 0,
            "no method carries [ExpectedRunnerAbort], so this guard measured nothing. Either the marker was renamed " +
            "or every abort child was removed; in the second case delete the guard rather than leave it green.");

        var violations = new List<string>();
        foreach (var m in marked)
        {
            var who = $"{m.DeclaringType!.Name}.{m.Name}";
            if (!m.IsStatic || m.ReturnType != typeof(ProcessStartInfo))
            {
                violations.Add($"{who} must be a static method returning ProcessStartInfo.");
                continue;
            }
            var psi = (ProcessStartInfo)m.Invoke(null, DummyArguments(m))!;
            if (Violation(psi) is { } why) violations.Add($"{who}: {why}");
        }
        Assert.True(violations.Count == 0,
            "a runner child meant to abort would write a heap dump into the CI crash-dumps artifact (#5283); call "
            + "`.SwitchOff()` on its start info:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void TheMarkerSeenByReflection_IsTheMarkerWrittenInTheSource()
    {
        var marked = MarkedMethods();
        var perFile = SourceFiles().Where(f => !NotUses.Contains(f.File))
            .Select(f => (f.File, Count: Uses(f.Lines))).Where(f => f.Count > 0).ToList();
        var written = perFile.Sum(f => f.Count);
        Assert.True(written == marked.Count,
            $"reflection found {marked.Count} marked method(s) but the source names the marker {written} time(s) "
            + $"({string.Join(", ", perFile.Select(f => $"{f.File}:{f.Count}"))}). A spelling reflection cannot see, a marker on "
            + "something that is not a method, or an `abstract`/generic member would leave a builder outside the set.");
    }

    [Fact]
    public void ATestThatExpectsAnAbortExitCode_MarksItsStartInfoBuilder()
    {
        var offenders = SourceFiles().Where(f => !NotUses.Contains(f.File))
            .Where(f => f.Lines.Any(ExpectsAbortExit) && Uses(f.Lines) == 0)
            .Select(f => f.File).ToList();
        Assert.True(offenders.Count == 0,
            "these sources expect exit 134/139 from a runner child and mark no start-info builder with "
            + $"[ExpectedRunnerAbort], so the child inherits the CI crash-dump switch: {string.Join(", ", offenders)}");
    }

    // The census above cannot see a child whose test asserts loosely (`exit != 0`), so the children
    // #5283 measured are named: Win32Stubs and the corrupt-Ncl one abort on purpose, and Precompile's
    // refusal path aborted before its fix and would again on a regression. Deleting a marker means
    // deleting its line here, which a reviewer sees.
    private static readonly string[] MeasuredAbortChildren =
    {
        "Win32StubsEnvVarChildTests",
        "PrecompileDependencyRefusalTests",
        "ExplicitEngineMinorWarningOncePerInvocationTests",
    };

    [Fact]
    public void TheMeasuredAbortChildren_StayMarked()
    {
        var marked = MarkedMethods().Select(m => m.DeclaringType!.Name).ToHashSet();
        var unmarked = MeasuredAbortChildren.Where(c => !marked.Contains(c)).ToList();
        Assert.True(unmarked.Count == 0,
            $"these classes build a runner child that aborts on purpose and no longer mark its builder: {string.Join(", ", unmarked)}");
    }

    // ── the instruments, on input that really contains each arm ──────────────────────────────────

    [Theory]
    [InlineData("Assert.True(exit == 134, \"x\");", true)]
    [InlineData("Assert.Equal(134, exit);", true)]
    [InlineData("Assert.Equal( 139, exit);", true)]
    [InlineData("if (exit is 134 or 139) return;", true)]
    [InlineData("[InlineData(139)]", true)]
    [InlineData("var ok = 139 == code;", true)]
    [InlineData("Assert.NotEqual(134, exit);", false)]
    [InlineData("Assert.True(exit != 134);", false)]
    [InlineData("// Assert.True(exit == 134);", false)]
    [InlineData("/// the abort (exit == 134) is the point", false)]
    [InlineData("Assert.Contains(\"SIGABRT (exit == 134)\", output);", false)]
    [InlineData("var cu = Codeunit134043;", false)]
    [InlineData("Assert.True(exit is not 134);", false)]
    public void TheCensusMatcher_SeesAPositiveAbortExpectationAndOnlyThat(string line, bool expected) =>
        Assert.Equal(expected, ExpectsAbortExit(line));

    [Fact]
    public void TheCheck_RefusesAStartInfoThatInheritsTheSwitch_AndAcceptsOneThatTurnsItOff()
    {
        var absent = new ProcessStartInfo();
        absent.Environment.Remove(CrashDump.SwitchVariable);
        Assert.NotNull(Violation(absent));

        var on = new ProcessStartInfo();
        on.Environment[CrashDump.SwitchVariable] = "1";
        Assert.Contains("'1'", Violation(on));

        Assert.Null(Violation(new ProcessStartInfo().SwitchOff()));
    }

    [Fact]
    public void SwitchOff_OverwritesAnInheritedValue_AndLeavesTheHostEnvironmentAlone()
    {
        var psi = new ProcessStartInfo();
        psi.Environment[CrashDump.SwitchVariable] = "1";
        var returned = psi.SwitchOff();
        Assert.Same(psi, returned);
        Assert.Equal("0", psi.Environment[CrashDump.SwitchVariable]);
        // The start info's own copy changed; the host's value is whatever it was.
        Assert.Equal(Environment.GetEnvironmentVariable(CrashDump.SwitchVariable),
            new ProcessStartInfo().Environment.TryGetValue(CrashDump.SwitchVariable, out var v) ? v : null);
    }
}
