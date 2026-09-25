// Issue #4567 (part of #4559): one dependency that cannot be resolved used to print its cause
// four times — DEP-RESOLVE-FAIL, a `[cache] dependency resolution failed` line, a `[cache] NOKEY`
// line, then an EMIT-ZERO block listing an AL0185 per reference into the unresolved app. Only the
// first is the cause. These tests spawn the runner on a bundle whose one dependency is present
// only below its declared minimum, and pin what a default run and a --verbose run print.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyResolveFailureOutputTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepId = "d0104567-dddd-4b22-8c33-d44455566677";
    private const string DepPublisher = "Fabrikam ISV";
    private const string DepName = "Fabrikam Lib";
    private const string FoundVersion = "1.0.0.0";
    private const string RequiredVersion = "2.0.0.0";

    private readonly string _scratch;

    public DependencyResolveFailureOutputTests()
    {
        _scratch = TestScratch.Dir("al-runner-dep-resolve-output");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    [SkippableFact]
    public void DefaultRun_StatesTheCauseOnce_AndHidesTheCascade()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir) = Arrange("default");

        var run = Spawn(bundle, pkgDir, cacheDir, verbose: false);

        // Still a failed run, with the same exit code a compile failure has always had.
        Assert.True(run.ExitCode == 3, $"exit {run.ExitCode}, expected 3\n{run.Output}");

        // The cause, once, naming the dependency, the version it needs and the one it found.
        Assert.True(CountOf(run.Output, $"{DepPublisher}/{DepName}") == 1,
            $"the unresolved dependency should be named exactly once\n{run.Output}");
        Assert.Contains($"v{RequiredVersion} or newer", run.Output);
        Assert.Contains($"v{FoundVersion}", run.Output);

        // None of the three restatements of that cause.
        Assert.DoesNotContain("[cache] dependency resolution failed", run.Output);
        Assert.DoesNotContain("[cache] NOKEY", run.Output);

        // No cascade: the compile errors are named as a consequence and counted, not listed.
        Assert.DoesNotContain("AL0185", run.Output);
        Assert.Contains("follow from the unresolved dependency above", run.Output);
        Assert.Contains("--verbose", run.Output);
    }

    [SkippableFact]
    public void VerboseRun_KeepsTheCascadeDetail()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir) = Arrange("verbose");

        var run = Spawn(bundle, pkgDir, cacheDir, verbose: true);

        Assert.True(run.ExitCode == 3, $"exit {run.ExitCode}, expected 3\n{run.Output}");
        Assert.Contains("AL0185", run.Output);
        Assert.Contains("[cache] NOKEY", run.Output);
        Assert.Contains($"v{RequiredVersion} or newer", run.Output);
    }

    /// <summary>The owner's case: a Microsoft app at 28.4 needed on a 28.1 run. Where it comes from.</summary>
    [Fact]
    public void VersionGap_MicrosoftApp_NamesTheBcReleaseThatShipsIt()
    {
        var ex = new AlRunner.Infrastructure.DependencyVersionMismatchException(
            "Microsoft", "System Application Test Library", "28.4.0.0", Guid.NewGuid(),
            new[] { "/cache" }, "v28.1.49838.55128", "Tests-TestLibraries → System Application Test Library");

        var lines = DependencyResolveFailureOutput.DependencyResolveFailureLines("app", ex, "28.1.49838.53910", verbose: false);
        var text = string.Join("\n", lines);

        Assert.Contains("--bc-version 28.4", text);
        Assert.Contains("Dependency chain: Tests-TestLibraries → System Application Test Library", text);
        Assert.Contains("v28.4.0.0 or newer", text);
        Assert.Equal(1, CountOf(text, "Microsoft/System Application Test Library"));
        // The searched directories are verbose-only.
        Assert.DoesNotContain("/cache", text);
        Assert.Contains("/cache", string.Join("\n",
            DependencyResolveFailureOutput.DependencyResolveFailureLines("app", ex, null, verbose: true)));
    }

    [Fact]
    public void VersionGap_ThirdPartyApp_DoesNotSuggestABcRelease()
    {
        var ex = new AlRunner.Infrastructure.DependencyVersionMismatchException(
            DepPublisher, DepName, "28.4.0.0", Guid.NewGuid(), new[] { "/cache" }, "v28.1.0.0");

        Assert.DoesNotContain("--bc-version", ex.ToDetailedMessage());
    }

    [Fact]
    public void AlDiagnosticListing_CollapsesOnlyAfterAnUnresolvedDependency_AndNotUnderVerbose()
    {
        var diags = new[] { "error AL0185: Codeunit 'A' is missing", "error AL0185: Codeunit 'B' is missing" };

        Assert.Equal(2, DependencyResolveFailureOutput.AlDiagnosticListing(diags, dependencyUnresolved: false, verbose: false).Count);
        Assert.Equal(2, DependencyResolveFailureOutput.AlDiagnosticListing(diags, dependencyUnresolved: true, verbose: true).Count);
        var collapsed = Assert.Single(DependencyResolveFailureOutput.AlDiagnosticListing(diags, dependencyUnresolved: true, verbose: false));
        Assert.Contains("These 2 error(s) follow from the unresolved dependency above", collapsed);
    }

    private static int CountOf(string haystack, string needle) =>
        Regex.Matches(haystack, Regex.Escape(needle)).Count;

    private (string Bundle, string PkgDir, string CacheDir) Arrange(string name)
    {
        var root = Path.Combine(_scratch, name);
        var bundle = Path.Combine(root, "bundle");
        var pkgDir = Path.Combine(root, "pkg");
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(pkgDir);
        Directory.CreateDirectory(cacheDir);

        File.WriteAllText(Path.Combine(bundle, "app.json"), $$"""
        {
          "id": "d0104567-aaaa-4b22-8c33-d44455566677",
          "name": "DepResolve Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{DepId}}", "name": "{{DepName}}", "publisher": "{{DepPublisher}}", "version": "{{RequiredVersion}}" }
          ],
          "idRanges": [ { "from": 60797, "to": 60797 } ],
          "runtime": "14.0"
        }
        """);
        // Two references into the dependency, so the default run has a cascade to hide.
        File.WriteAllText(Path.Combine(bundle, "Probe.Codeunit.al"), """
        codeunit 60797 "DepResolve Probe"
        {
            Subtype = Test;

            [Test]
            procedure UsesTheDependency()
            var
                Helper: Codeunit "Fabrikam Helper";
                Other: Codeunit "Fabrikam Other Helper";
            begin
                Helper.DoIt();
                Other.DoIt();
            end;
        }
        """);

        WriteApp(pkgDir);
        return (bundle, pkgDir, cacheDir);
    }

    /// <summary>A minimal NAVX <c>.app</c> declaring the dependency at the too-old version.</summary>
    private static void WriteApp(string dir)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepId}" Name="{DepName}" Publisher="{DepPublisher}" Version="{FoundVersion}"/>
              <Dependencies />
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("NavxManifest.xml");
            using var es = manifest.Open();
            es.Write(Encoding.UTF8.GetBytes(xml));
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(Path.Combine(dir, $"{DepPublisher}_{DepName}_{FoundVersion}.app"), result);
    }

    private static (int ExitCode, string Output) Spawn(
        string bundle, string pkgDir, string cacheDir, bool verbose)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
        args.Append($" --package-cache \"{pkgDir}\"");
        args.Append($" --cache \"{cacheDir}\"");
        if (verbose) args.Append(" --verbose");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment.Remove("AL_RUNNER_VERBOSE");
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
