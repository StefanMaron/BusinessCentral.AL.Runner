// PrecompileEmitAppVersionHelpTextTests — issue #2227.
//
// --precompile and --emit-app dispatch before the bundle-run flow's --bc-version handling, so
// the EXECUTION section's --bc-version default does not describe them. These assertions pin the
// --help text for both subcommands to what they measurably do: --precompile selects the input
// .app's own version (then latest-in-cache) and, since #2190, the matching engine variant;
// --emit-app only packages sources and needs no BC artifacts at all (measured: exit 0 with
// AL_RUNNER_ARTIFACTS_ROOT pointing at an empty directory).
using System.Text.RegularExpressions;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompileEmitAppVersionHelpTextTests
{
    /// <summary>--help with each wrapped continuation line joined, so a sentence can be asserted
    /// regardless of where the column wrap falls.</summary>
    private static string FlatHelp()
    {
        var w = new StringWriter();
        ProgramSupport.PrintHelp(w);
        return Regex.Replace(w.ToString(), @"\r?\n {20,}", " ");
    }

    private static string Entry(string help, string startsWith)
    {
        var lines = help.Split('\n');
        var line = lines.Single(l => l.StartsWith(startsWith, StringComparison.Ordinal));
        return Regex.Replace(line, @"\s+", " ");
    }

    [Fact]
    public void Help_PrecompileEntry_StatesItsOwnVersionDefault()
    {
        var entry = Entry(FlatHelp(), "  --precompile <input.app> --out <output.dll> [--package-cache PATH ...] ");

        Assert.Contains("Takes no --bc-version", entry);
        Assert.Contains("equal to the .app's own manifest Version", entry);
        Assert.Contains("falls back to the latest version in the artifacts cache", entry);
        Assert.Contains("exits 2 when none is shipped for that version", entry);
    }

    [Fact]
    public void Help_EmitAppEntry_SaysItCompilesNothingAndSelectsNoVersion()
    {
        var entry = Entry(FlatHelp(), "  --emit-app <bundleDir> <outPath> [--package-cache PATH ...] ");

        Assert.DoesNotContain("Compile a bundle dir", entry);
        Assert.Contains("Compiles nothing and selects no BC version", entry);
    }

    [Fact]
    public void Help_BcVersionEntry_IsScopedToTheBundleRun()
    {
        var entry = Entry(FlatHelp(), "  --bc-version X ");

        Assert.Contains("for a bundle run", entry);
        Assert.Contains("--precompile and --emit-app do not read it", entry);
    }
}
