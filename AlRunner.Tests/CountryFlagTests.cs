// Issue #2236: al-runner only ever downloaded the w1 (worldwide) BC artifact, so a
// country-localized codebase (a real US customer extension depending on "IRS Forms") could
// not be auto-provisioned at all. This file proves the CLI-level half of the fix: --country
// is a recognized flag (not rejected as "Unknown option"), it is visibly plumbed into the
// process-wide selection (the "[bc] selected BC ..." startup line names it), and it does
// not disturb an ordinary w1 run when omitted. The download/selection-logic half is proven
// network-free in ArtifactDownloaderCountryTests and ProvisioningCheckTests; the real
// end-to-end country download is verified manually against the live CDN (see the PR
// description) rather than in the default test run — see
// .claude/rules/local-test-scope.md ("do not add a network dependency to the default
// unit-test run").
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CountryFlagTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // A dependency-free bundle: --country only changes WHICH artifact set would be
    // downloaded for a MISSING Microsoft dependency, so a bundle with none must compile
    // and run identically regardless of --country — proving --country does not disturb an
    // ordinary run is as important as proving it is recognized at all.
    private static readonly string MinimalBundle =
        Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp-table");

    private static (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var a in args) sb.Append(' ').Append(a);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 120s.");
        }
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void Country_IsARecognizedFlag_NotRejectedAsUnknownOption()
    {
        var (exit, _, stderr) = Run("--country", "us", "--no-auto-provision", $"\"{MinimalBundle}\"");

        Assert.DoesNotContain("Unknown option '--country'", stderr, StringComparison.Ordinal);
        Assert.True(exit == 0, $"expected a clean run (country only changes what a MISSING " +
            $"Microsoft dep would be fetched from; this bundle has none). exit={exit}\n{stderr}");
    }

    [Fact]
    public void Country_Us_IsNamedInTheSelectedBcVersionLine()
    {
        var (exit, _, stderr) = Run("--country", "us", "--no-auto-provision", $"\"{MinimalBundle}\"");
        Assert.True(exit == 0, $"exit={exit}\n{stderr}");

        var m = Regex.Match(stderr, @"\[bc\] selected BC \S+ \([^)]*\) \[country: (\S+)\]");
        Assert.True(m.Success, $"expected the '[bc] selected BC ...' line to name the country. stderr:\n{stderr}");
        Assert.Equal("us", m.Groups[1].Value);
    }

    [Fact]
    public void Country_Omitted_DefaultsToW1_AndPrintsNoCountrySuffix()
    {
        var (exit, _, stderr) = Run("--no-auto-provision", $"\"{MinimalBundle}\"");
        Assert.True(exit == 0, $"exit={exit}\n{stderr}");

        // The pre-#2236 line shape, byte-for-byte: no [country: ...] suffix at all for the
        // invisible w1 default — a w1 run must read exactly as it always has.
        Assert.Matches(new Regex(@"\[bc\] selected BC \S+ \([^)]*\)\r?$", RegexOptions.Multiline), stderr);
        Assert.DoesNotContain("[country:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Country_IsCaseInsensitive_UppercaseUsAndLowercaseUsSelectTheSameChannel()
    {
        var (exitUpper, _, stderrUpper) = Run("--country", "US", "--no-auto-provision", $"\"{MinimalBundle}\"");
        var (exitLower, _, stderrLower) = Run("--country", "us", "--no-auto-provision", $"\"{MinimalBundle}\"");

        Assert.True(exitUpper == 0, stderrUpper);
        Assert.True(exitLower == 0, stderrLower);

        var mUpper = Regex.Match(stderrUpper, @"\[country: (\S+)\]");
        var mLower = Regex.Match(stderrLower, @"\[country: (\S+)\]");
        Assert.True(mUpper.Success && mLower.Success, $"upper:\n{stderrUpper}\nlower:\n{stderrLower}");
        Assert.Equal("us", mUpper.Groups[1].Value);
        Assert.Equal(mLower.Groups[1].Value, mUpper.Groups[1].Value);
    }
}
