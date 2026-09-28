// DefaultTestToolPinGuardTests — #4905. Holds DefaultTestToolPin in place: the test host pins the
// default Test Runner load off for every runner it spawns, and no spawn site sets, clears or
// removes that pin except through DefaultTestToolPin's own helpers.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class DefaultTestToolPinGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string TestsDir = Path.Combine(RepoRoot, "AlRunner.Tests");

    // The only two files allowed to name the variable or write it.
    private static readonly string[] Owners = { "DefaultTestToolPin.cs", "DefaultTestToolPinGuardTests.cs" };

    [Fact]
    public void TestHost_PinsTheDefaultTestToolOff_ForEveryRunnerItSpawns()
    {
        Assert.Equal(ProgramSupport.DefaultTestToolEnvVar, DefaultTestToolPin.EnvVar);
        Assert.Equal("off", Environment.GetEnvironmentVariable(DefaultTestToolPin.EnvVar));
        // What a spawn site that sets nothing hands its child.
        Assert.Equal("off", new ProcessStartInfo("dotnet").Environment[DefaultTestToolPin.EnvVar]);
    }

    [Fact]
    public void LoadFrom_RefusesACacheTheRunDoesNotName()
    {
        var dir = TestScratch.Dir("al-runner-4905-pin-load-from");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Test Runner.app"), Array.Empty<byte>());

        var named = new ProcessStartInfo("dotnet", $"run --package-cache \"{dir}\" bundle");
        DefaultTestToolPin.LoadFrom(named, dir);
        Assert.Equal("on", named.Environment[DefaultTestToolPin.EnvVar]);

        var unnamed = new ProcessStartInfo("dotnet", "run bundle");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => DefaultTestToolPin.LoadFrom(unnamed, dir));
        Assert.Equal("off", unnamed.Environment[DefaultTestToolPin.EnvVar]);

        var empty = TestScratch.Dir("al-runner-4905-pin-load-from-empty");
        Directory.CreateDirectory(empty);
        var holdsNothing = new ProcessStartInfo("dotnet", $"run --package-cache \"{empty}\" bundle");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => DefaultTestToolPin.LoadFrom(holdsNothing, empty));
        Assert.Equal("off", holdsNothing.Environment[DefaultTestToolPin.EnvVar]);
    }

    private static readonly Regex[] GoesAround =
    {
        new(Regex.Escape(DefaultTestToolPin.EnvVar), RegexOptions.CultureInvariant),
        new(@"DefaultTestToolPin\s*\.\s*EnvVar", RegexOptions.CultureInvariant),
        // The runner-side constant, qualified or through `using static AlRunner.ProgramSupport`.
        new(@"\bDefaultTestToolEnvVar\b", RegexOptions.CultureInvariant),
        new(@"\.Environment(Variables)?\s*\.\s*Clear\s*\(", RegexOptions.CultureInvariant),
    };

    internal static bool GoesAroundThePin(string line) => GoesAround.Any(r => r.IsMatch(line));

    // Every spelling of the variable's name a text scan can see (#4927). A name assembled at run
    // time ("AL_RUNNER_DEFAULT_" + "TEST_TOOL") is out of reach of any text scan.
    [Theory]
    [InlineData("psi.Environment[\"AL_RUNNER_DEFAULT_TEST_TOOL\"] = \"on\";")]
    [InlineData("psi.Environment.Remove(DefaultTestToolPin.EnvVar);")]
    [InlineData("psi.Environment[ProgramSupport.DefaultTestToolEnvVar] = \"on\";")]
    [InlineData("psi.Environment[AlRunner.ProgramSupport.DefaultTestToolEnvVar] = \"on\";")]
    [InlineData("psi.Environment.Remove(DefaultTestToolEnvVar);")]
    [InlineData("psi.EnvironmentVariables.Clear();")]
    public void Scan_CatchesEverySpellingOfThePin(string line)
        => Assert.True(GoesAroundThePin(line), $"the scan misses: {line}");

    // Controls: sanctioned helpers, and a sibling member sharing the name's prefix.
    [Theory]
    [InlineData("DefaultTestToolPin.LoadFrom(psi, testApps);")]
    [InlineData("DefaultTestToolPin.Unpin(psi);")]
    [InlineData("var enabled = ProgramSupport.DefaultTestToolEnabled();")]
    [InlineData("psi.Environment[\"AL_RUNNER_OTHER\"] = \"on\";")]
    public void Scan_LeavesSanctionedAndUnrelatedLinesAlone(string line)
        => Assert.False(GoesAroundThePin(line), $"the scan flags a line that does not go around the pin: {line}");

    [Fact]
    public void NoSpawnSite_SetsClearsOrRemovesThePin_OutsideDefaultTestToolPin()
    {
        var files = Directory.Exists(TestsDir)
            ? Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList()
            : new List<string>();
        // Could not measure: a scan over nothing would pass whatever the spawn sites do.
        Assert.True(files.Count > 100 && files.Any(f => Path.GetFileName(f) == Owners[0]),
            $"scanned {files.Count} file(s) under '{TestsDir}', which is not the test project's source; the guard measured nothing.");

        var hits = new StringBuilder();
        foreach (var f in files.Where(f => !Owners.Contains(Path.GetFileName(f))))
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                if (GoesAroundThePin(lines[i]))
                    hits.AppendLine($"{Path.GetRelativePath(RepoRoot, f)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(hits.Length == 0,
            "a spawn site goes around the default Test Runner pin (#4905). Use DefaultTestToolPin.LoadFrom "
            + "with a cache the run names, or DefaultTestToolPin.Unpin for a test that establishes the "
            + "runner-owned caches first:\n" + hits);
    }

    // A misspelled value must not read as "on", which would bring the box-dependent load back.
    [SkippableFact]
    public void MisspelledValue_IsRefusedBeforeAnyWork()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = TestScratch.Dir("al-runner-4905-misspelled-pin");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "app.json"), """
        { "id": "b4905000-0000-4000-8000-000000004905", "name": "Pin4905", "publisher": "AL Runner",
          "version": "1.0.0.0", "idRanges": [ { "from": 64900, "to": 64909 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(bundle, "Probe.al"), """
        codeunit 64900 "Pin4905 Tests"
        {
            Subtype = Test;
            [Test]
            procedure Runs() begin end;
        }
        """);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + TestBuildConfig.BcVersionArg
                + $" --no-cache --no-auto-provision \"{bundle}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment[DefaultTestToolPin.EnvVar] = "of";
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        var output = stdout + stderr.Result;

        Assert.Contains("AL_RUNNER_DEFAULT_TEST_TOOL='of' is not 'on' or 'off'", output, StringComparison.Ordinal);
        Assert.DoesNotContain(" across ", output, StringComparison.Ordinal);
        Assert.Equal(2, p.ExitCode);
    }
}
