// WindowsLanguageVirtualTableTests — issue #2581.
//
// A RUNNER-MECHANISM test: it proves the route to "Windows Language" (2000000045) works and
// that the columns WITH a real source carry BC's own values. Before the fix the table had no
// provider, so every read answered zero rows and Get(1033) silently returned false.
//
// The row set and three of the columns come from BC's own
// Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper — a runtime-engine type, so driving it is
// allowed — rather than being reimplemented from CultureInfo, which would answer a different
// list than a service tier.
//
// THE STUBBED COLUMNS ARE NOT ASSERTED HERE. Six license-derived and four installed-resource
// columns have no source on the runner and carry chosen values; "the runner answers permitted"
// is a runner claim, not BC behaviour, and it is pinned in
// tests/runner-extras/windows-language-license-stub instead. See docs/limitations.md.
//
// MEASURED on BC 28.1.49838.53910 through BC's own helper: 212 rows; 1033 = English (United
// States) / en-US / ENU / OEM code page 437; 1031 = German (Germany) / de-DE / DEU; 2057 =
// English (United Kingdom) / en-GB / ENG. Note "Primary Language ID" is 1033 for BOTH English
// rows — it is the id of the primary language's default culture, not the bare LANGID 9 — which
// is why the assertion below is structural rather than a hardcoded number.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class WindowsLanguageVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "WindowsLanguageVirtualTable");

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");

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
        // WaitForExit(int) returns as soon as the process exits and does NOT wait for the
        // async BeginOutputReadLine/BeginErrorReadLine callbacks to drain — only the
        // parameterless overload does. Without this the last stdout lines can still be in
        // flight when we read outSb, and an Assert.Contains on a line the runner definitely
        // printed fails intermittently, the more so the more loaded the machine is (#2496).
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void WindowsLanguage_TruthfulColumns_AllFixtureTestsPass()
    {
        var cacheDir = TestScratch.Dir("al-runner-wlv-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (every fixture test must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The route works and the columns carry BC's own values — the direct RED this
            // fixes. Includes the OEM-vs-ANSI code page, which is the one column a plausible
            // reimplementation gets wrong.
            Assert.Contains("PASS  Codeunit60801.WindowsLanguage_Get1033_ReturnsTruthfulColumns", stdout);
            // A second, different row: a provider answering one fixed row passes the above.
            Assert.Contains("PASS  Codeunit60801.WindowsLanguage_Get1031_IsADifferentRow", stdout);
            // Structural rather than a magic number: two English sublanguages share a Primary
            // Language ID and German does not.
            Assert.Contains("PASS  Codeunit60801.WindowsLanguage_PrimaryLanguageId_GroupsSublanguagesTogether", stdout);
            // Negative: an unused id still answers false. Passes against an EMPTY table too,
            // which is why it is not sufficient on its own.
            Assert.Contains("PASS  Codeunit60801.WindowsLanguage_GetOnAnUnusedId_ReturnsFalse", stdout);
            // Negative: filtering discriminates.
            Assert.Contains("PASS  Codeunit60801.WindowsLanguage_FilterOnLanguageId_DiscriminatesBetweenRows", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
