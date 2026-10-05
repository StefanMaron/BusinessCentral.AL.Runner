// Issue #4567 (part of #4559): one dependency that cannot be resolved used to print its cause
// four times — DEP-RESOLVE-FAIL, a `[cache] dependency resolution failed` line, a `[cache] NOKEY`
// line, then an EMIT-ZERO block listing an AL0185 per reference into the unresolved app. Only the
// first is the cause. These tests spawn the runner on a bundle whose dependency closure cannot be
// resolved and pin what a default run and a --verbose run print.
//
// #5335: a dependency present only below its minimum version no longer reaches the compile (it stops the
// run with exit 2, MissingDependencyExitCodeTests), and an absent one never did. What still reaches it is
// a resolve failure that is neither: here a dependency cycle between two packages, which the generic
// handler reports with DEP-RESOLVE-FAIL and continues from.
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
    private const string DepVersion = "1.0.0.0";
    private const string PartnerId = "d0104567-eeee-4b22-8c33-d44455566677";
    private const string PartnerName = "Fabrikam Partner";

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

        // The cause, once, naming the cycle's packages.
        Assert.True(CountOf(run.Output, "Dependency cycle detected") == 1,
            $"the unresolved dependency should be reported exactly once\n{run.Output}");
        Assert.Contains($"{DepName} -> {PartnerName} -> {DepName}", run.Output);

        // None of the three restatements of that cause.
        Assert.DoesNotContain("[cache] dependency resolution failed", run.Output);
        Assert.DoesNotContain("[cache] NOKEY", run.Output);

        // No cascade: the compile errors a missing dependency produces are counted, not listed.
        Assert.DoesNotContain("error AL0185", run.Output);
        Assert.Contains("of the kind a missing dependency produces", run.Output);
        Assert.Contains("--verbose", run.Output);
    }

    /// <summary>
    /// Review of PR #4580: an AL error unrelated to the missing dependency must still be listed
    /// on a default run. Only the cascade's own kind (AL0185 "is missing", emit-crash) is hidden.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_StillListsAnUnrelatedAlError()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir) = Arrange("unrelated", probeBody: ProbeWithUnrelatedError);

        var run = Spawn(bundle, pkgDir, cacheDir, verbose: false);

        Assert.True(run.ExitCode == 3, $"exit {run.ExitCode}, expected 3\n{run.Output}");
        Assert.Contains("AL0118", run.Output);
        Assert.Contains("UndeclaredThing", run.Output);
        Assert.DoesNotContain("error AL0185", run.Output);
        Assert.DoesNotContain("follow from the unresolved dependency", run.Output);
        Assert.Contains("of the kind a missing dependency produces", run.Output);
    }

    /// <summary>
    /// Review of PR #4580: with two codeunits failing independently the run reports EMIT-EXCLUDED,
    /// whose diagnostic listing must collapse the same cascade and keep the unrelated error.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_EmitExcluded_HidesTheCascade_AndListsTheUnrelatedError()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir) = Arrange("excluded", extraCodeunit: SecondCodeunitWithUnrelatedError);

        var run = Spawn(bundle, pkgDir, cacheDir, verbose: false);

        Assert.True(run.ExitCode == 3, $"exit {run.ExitCode}, expected 3\n{run.Output}");
        Assert.Contains("EMIT-EXCLUDED", run.Output);
        Assert.Contains("UndeclaredThing", run.Output);
        Assert.DoesNotContain("error AL0185", run.Output);
        Assert.Contains("of the kind a missing dependency produces", run.Output);
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
        Assert.Contains("Dependency cycle detected", run.Output);
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
        // The "Required:" line and the Info line (#5335) name it; the cause itself is not restated.
        Assert.Equal(1, CountOf(text, "Required: Microsoft/System Application Test Library"));
        Assert.Equal(2, CountOf(text, "Microsoft/System Application Test Library"));
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

    /// <summary>
    /// #5335, the owner's "at most an Info about lowering the version": one line, naming the dependency, the
    /// declared minimum and the HIGHEST copy found (the resolver lists copies highest first), and only when
    /// there is a copy to name. An absent package has none, so its report carries no such line.
    /// </summary>
    [Fact]
    public void VersionGap_InfoLine_NamesTheHighestAvailableBuild_AndOnlyThen()
    {
        var two = new AlRunner.Infrastructure.DependencyVersionMismatchException(
            "Acme", "Acme Lib", "2.0.0.0", Guid.NewGuid(), new[] { "/cache" }, "v1.2.0.0, v0.5.0.0");
        var text = two.ToDetailedMessage();

        Assert.Equal(1, CountOf(text, "Info:"));
        Assert.Contains(
            "Info: if this app does not need Acme/Acme Lib v2.0.0.0 or newer, you can lower its version in app.json to v1.2.0.0.",
            text);
        Assert.DoesNotContain("to v0.5.0.0", text);

        // The version list arrives without a "v" in other callers: still one well-formed line.
        var bare = new AlRunner.Infrastructure.DependencyVersionMismatchException(
            "Acme", "Acme Lib", "2.0.0.0", Guid.NewGuid(), Array.Empty<string>(), "1.0.0.0");
        Assert.Contains("to v1.0.0.0.", bare.ToDetailedMessage());

        // Nothing parses: no hint rather than a wrong one. The rest of the report is unchanged.
        var none = new AlRunner.Infrastructure.DependencyVersionMismatchException(
            "Acme", "Acme Lib", "2.0.0.0", Guid.NewGuid(), Array.Empty<string>(), "");
        Assert.DoesNotContain("Info:", none.ToDetailedMessage());
        Assert.Contains("This is a VERSION gap", none.ToDetailedMessage());

        var absent = new AlRunner.Infrastructure.MissingDependencyException(
            "Acme", "Acme Lib", "2.0.0.0", Guid.NewGuid(), new[] { "/cache" });
        Assert.DoesNotContain("lower its version", absent.ToDetailedMessage());
    }

    [Fact]
    public void AlDiagnosticListing_CollapsesOnlyTheMissingDependencyKind_AndNotUnderVerbose()
    {
        var diags = new[]
        {
            "SourceFile(a.al@8:17): error AL0185: Codeunit 'A' is missing",
            "SourceFile(a.al@9:16): error AL0185: Codeunit 'B' is missing",
            "SourceFile(a.al@12:14): error AL0118: The name 'UndeclaredThing' does not exist in the current context.",
            "emit-crash: Codeunit \"P\" :: T() — Unexpected value 'None'",
        };

        Assert.Equal(4, DependencyResolveFailureOutput.AlDiagnosticListing(diags, dependencyUnresolved: false, verbose: false).Count);
        Assert.Equal(4, DependencyResolveFailureOutput.AlDiagnosticListing(diags, dependencyUnresolved: true, verbose: true).Count);

        var collapsed = DependencyResolveFailureOutput.AlDiagnosticListing(diags, dependencyUnresolved: true, verbose: false);
        Assert.Equal(2, collapsed.Count);
        Assert.Contains("AL0118", collapsed[0]);
        Assert.Contains("3 error(s) of the kind a missing dependency produces", collapsed[1]);

        // Nothing of the missing-dependency kind: no count line at all.
        var onlyUnrelated = DependencyResolveFailureOutput.AlDiagnosticListing(new[] { diags[2] }, dependencyUnresolved: true, verbose: false);
        Assert.Contains("AL0118", Assert.Single(onlyUnrelated));
    }

    /// <summary>
    /// NOKEY is hidden on a default run only when it restates this bundle's DEP-RESOLVE-FAIL — the
    /// blocker is the unresolved-closure reason. Any other blocker still prints, and --verbose
    /// always prints. No CLI spawn reaches another blocker (a location-less runner assembly, or a
    /// resolved-but-unhashable package, which #2987 made unreachable), so the gate is pinned here.
    /// </summary>
    [Fact]
    public void ShouldPrintNokey_HidesOnlyARestatementOfTheDependencyFailure()
    {
        var unresolved = new ProgramSupport.OrderedDependencyIds(
            new[] { "unresolved:DependencyVersionMismatchException:too old" }, "the dependency closure of '/x' could not be resolved");
        var degraded = new ProgramSupport.OrderedDependencyIds(
            new[] { "id:1.0.0.0:unhashable:/p:absent" }, "package /p could not be hashed");

        Assert.False(DependencyResolveFailureOutput.ShouldPrintNokey(false, true, null, unresolved));
        Assert.True(DependencyResolveFailureOutput.ShouldPrintNokey(true, true, null, unresolved));
        Assert.True(DependencyResolveFailureOutput.ShouldPrintNokey(false, false, null, unresolved));
        Assert.True(DependencyResolveFailureOutput.ShouldPrintNokey(false, true, "runner has no location", unresolved));
        Assert.True(DependencyResolveFailureOutput.ShouldPrintNokey(false, true, null, degraded));
    }

    private const string DefaultProbe = """
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
        """;

    // The default probe plus one error that has nothing to do with the dependency (AL0118).
    private const string ProbeWithUnrelatedError = """
        codeunit 60797 "DepResolve Probe"
        {
            Subtype = Test;

            [Test]
            procedure UsesTheDependency()
            var
                Helper: Codeunit "Fabrikam Helper";
                Other: Codeunit "Fabrikam Other Helper";
                n: Integer;
            begin
                Helper.DoIt();
                Other.DoIt();
                n := UndeclaredThing;
            end;
        }
        """;

    // A second codeunit carrying only the unrelated error: two objects fail independently, so
    // the run takes the EMIT-EXCLUDED path rather than EMIT-ZERO.
    private const string SecondCodeunitWithUnrelatedError = """
        codeunit 60798 "DepResolve Second"
        {
            Subtype = Test;

            [Test]
            procedure HasItsOwnError()
            var
                n: Integer;
            begin
                n := UndeclaredThing;
            end;
        }
        """;

    private static int CountOf(string haystack, string needle) =>
        Regex.Matches(haystack, Regex.Escape(needle)).Count;

    private (string Bundle, string PkgDir, string CacheDir) Arrange(
        string name, string? probeBody = null, string? extraCodeunit = null)
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
            { "id": "{{DepId}}", "name": "{{DepName}}", "publisher": "{{DepPublisher}}", "version": "{{DepVersion}}" }
          ],
          "idRanges": [ { "from": 60797, "to": 60798 } ],
          "runtime": "14.0"
        }
        """);
        // Two references into the dependency, so the default run has a cascade to hide.
        File.WriteAllText(Path.Combine(bundle, "Probe.Codeunit.al"), probeBody ?? DefaultProbe);
        if (extraCodeunit != null)
            File.WriteAllText(Path.Combine(bundle, "Extra.Codeunit.al"), extraCodeunit);

        // The two packages each name the other as a dependency: present at the required version, so no
        // version or absence gap, and the resolver throws InvalidOperationException ("Dependency cycle").
        WriteApp(pkgDir, DepId, DepName, DepVersion, PartnerId, PartnerName);
        WriteApp(pkgDir, PartnerId, PartnerName, DepVersion, DepId, DepName);
        return (bundle, pkgDir, cacheDir);
    }

    /// <summary>A minimal NAVX <c>.app</c> declaring one dependency.</summary>
    private static void WriteApp(string dir, string id, string name, string version, string depId, string depName)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{id}" Name="{name}" Publisher="{DepPublisher}" Version="{version}"/>
              <Dependencies>
                <Dependency Id="{depId}" Name="{depName}" Publisher="{DepPublisher}" MinVersion="{DepVersion}"/>
              </Dependencies>
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
        File.WriteAllBytes(Path.Combine(dir, $"{DepPublisher}_{name}_{version}.app"), result);
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
