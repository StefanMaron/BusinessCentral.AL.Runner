// ConsoleOutputEncodingTests — #3738: the runner's non-ASCII console output is transliterated
// away when stdout is redirected on Windows, so every consumer that parses it — a CI log, a
// test, LethAL — reads different bytes from the ones the runner wrote.
//
// Measured on this box (Windows 11, ANSI cp1252, OEM cp850). A redirected child's stdout
// defaults to the OEM code page, and .NET's encoder silently best-fits what that page lacks:
//
//   U+2014 —  em dash    -> 0x2D '-'
//   U+2192 →  arrow      -> 0x1A     (SUB, a control character — not a rendering of anything)
//   U+2026 …  ellipsis   -> 0x2E '.'
//   U+2500 ─  box line   -> 0xC4     (survives; cp850 has it)
//
// The runner writes all four (13 Console.Write* statements). The arrow is the one that matters
// beyond cosmetics: 0x1A is data loss, and it lands in `Classification → <path>` and
// `JUnit XML → <path>`, two lines a consumer reads to find an output file.
//
// It surfaced as SuiteEnumerationTests failing on Windows only: its regex matched the em dash
// the runner writes, and the test host read back a hyphen. That test's own fix is not to depend
// on typography (see SuiteEnumerationTests' suite-count reader); this class pins the runner
// side, which is what makes the bytes a consumer sees the bytes the runner wrote.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ConsoleOutputEncodingTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public ConsoleOutputEncodingTests()
    {
        _root = TestScratch.Dir("al-runner-console-encoding");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteBundle(string dir)
    {
        Directory.CreateDirectory(dir);
        // No "application" property — see .claude/rules/no-base-app-in-csharp-tests.md.
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "5a3f0b11-3738-4a02-8002-000000003738",
          "name": "COE Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 63950, "to": 63959 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "T.Codeunit.al"), """
        codeunit 63950 "COE Probe Tests"
        {
            Subtype = Test;

            [Test]
            procedure Trivial()
            begin
            end;
        }
        """);
    }

    /// <summary>Runs the runner with stdout redirected and returns the RAW bytes — never a
    /// decoded string, because the decode is the thing under test.</summary>
    private byte[] RunAndCaptureRawStdout(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-auto-provision");
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append(" \"").Append(Path.Combine(_root, "bundle")).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        using var stdout = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        var errTask = p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        copy.GetAwaiter().GetResult();
        errTask.GetAwaiter().GetResult();
        return stdout.ToArray();
    }

    /// <summary>
    /// RED on Windows before the fix: the per-bundle "— N suites" line arrived as
    /// <c>- N suites</c>, the em dash best-fitted to 0x2D by cp850. GREEN: the bytes are the
    /// UTF-8 encoding of the character the runner wrote. Green on Linux either way, where the
    /// default is already UTF-8 — the point is that a consumer no longer has to know which.
    /// </summary>
    [SkippableFact]
    public void RedirectedStdout_IsUtf8_SoAnEmDashSurvives()
    {
        TestArtifacts.SkipIfMissing();
        WriteBundle(Path.Combine(_root, "bundle"));

        var raw = RunAndCaptureRawStdout();

        var text = Encoding.UTF8.GetString(raw);
        Assert.Contains("suites", text, StringComparison.Ordinal);
        // The exact bytes, not a decoded comparison: 0xE2 0x80 0x94 is U+2014 in UTF-8.
        Assert.Contains("— 1 suites", text, StringComparison.Ordinal);
        Assert.True(Contains(raw, new byte[] { 0xE2, 0x80, 0x94 }),
            "no UTF-8 em dash in the runner's redirected stdout:\n" + Preview(raw));
    }

    /// <summary>
    /// The half that is not cosmetic. U+2192 has no cp850 representation at all, so the encoder
    /// emits 0x1A (SUB) — a control character, in `Classification → &lt;path&gt;` and
    /// `JUnit XML → &lt;path&gt;`, both of which name a file a consumer goes on to read. No
    /// control byte may appear in the runner's stdout.
    /// </summary>
    [SkippableFact]
    public void RedirectedStdout_CarriesNoSubstituteControlBytes()
    {
        TestArtifacts.SkipIfMissing();
        WriteBundle(Path.Combine(_root, "bundle"));
        var outPath = Path.Combine(_root, "classification.json");

        var raw = RunAndCaptureRawStdout($"--out \"{outPath}\"");

        var text = Encoding.UTF8.GetString(raw);
        Assert.Contains("Classification → ", text, StringComparison.Ordinal);
        Assert.False(Array.IndexOf(raw, (byte)0x1A) >= 0,
            "0x1A (SUB) in stdout — a character was dropped by the console codec:\n" + Preview(raw));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var hit = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return true;
        }
        return false;
    }

    private static string Preview(byte[] raw) =>
        Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 3000));
}
