// LibraryAssertPlatformlessConsumerTests — #3719: a bundle depending on Microsoft's Library
// Assert failed before any test ran: `[dep-load-fail] Microsoft_Library Assert v28.x: EMIT-ZERO`.
//
// The trigger is the CONSUMER's app.json declaring neither `platform` nor `application` (AL
// requires neither; LethAL's sandbox-data fixture has neither). Library Assert's own manifest
// declares Platform="28.0.0.0" and no <Dependencies>, and DependencyResolver followed only a
// resolved package's <Dependencies>, never its floors — so with no consumer-side `platform`
// root either, System.app was not in the closure Library Assert was source-compiled against:
// BC's emitter saw `Table 'Field' is missing` and `namespace 'Reflection' is unknown` and died
// with `Unexpected value 'None' of type NavTypeKind` at TypeOf(Variant), which the loader
// reports as EMIT-ZERO. Adding `"platform"` to the consumer alone made the fixture pass.
//
// This spawns the runner on that exact shape with the platform and test-toolkit packages
// already on disk and provisioning off, so it proves the RESOLUTION half: the closure must
// carry System.app because the dependency asks for it. The provisioning half (a fresh box must
// download the platform set when the toolkit is needed) is ProvisioningCheckTests.
// Skips (not passes) when no Library Assert package is provisioned on the box.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class LibraryAssertPlatformlessConsumerTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public LibraryAssertPlatformlessConsumerTests()
    {
        _root = TestScratch.Dir("al-runner-libassert-platformless");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// A provisioned test-apps directory holding Microsoft_Library Assert.app, and a platform-apps
    /// directory holding System.app — the runner-owned layout under the artifacts root, or the
    /// legacy ~/.al-runner layout CI populates. Null when either is absent.
    /// </summary>
    private static (string TestApps, string PlatformApps)? FindProvisionedDirs()
    {
        var home = TestArtifacts.HomeDir();
        var candidates = new List<(string TestApps, string PlatformApps)>();
        if (home != null)
            candidates.Add((Path.Combine(home, ".al-runner", "test-apps"), Path.Combine(home, ".al-runner", "platform-apps")));
        var root = AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir;
        if (Directory.Exists(root))
            foreach (var ver in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal))
                candidates.Add((Path.Combine(ver, "test-apps"), Path.Combine(ver, "platform-apps")));

        string? testApps = null, platformApps = null;
        foreach (var c in candidates)
        {
            if (testApps == null && File.Exists(Path.Combine(c.TestApps, "Microsoft_Library Assert.app"))) testApps = c.TestApps;
            if (platformApps == null && File.Exists(Path.Combine(c.PlatformApps, "System.app"))) platformApps = c.PlatformApps;
        }
        return testApps != null && platformApps != null ? (testApps, platformApps) : null;
    }

    private void WriteBundles(out string app, out string tests)
    {
        app = Path.Combine(_root, "app");
        tests = Path.Combine(_root, "tests");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(tests);
        // Deliberately NO "platform" and NO "application" in either manifest — that is the shape.
        File.WriteAllText(Path.Combine(app, "app.json"), """
        {
          "id": "5a3f0b11-3719-4a10-8010-000000003719",
          "name": "LAPC App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 63700, "to": 63709 } ],
          "runtime": "13.0"
        }
        """);
        File.WriteAllText(Path.Combine(app, "Helper.Codeunit.al"), """
        codeunit 63700 "LAPC Helper"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(tests, "app.json"), """
        {
          "id": "5a3f0b11-3719-4a11-8011-000000003719",
          "name": "LAPC Tests",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "dd0be2ea-f733-4d65-bb34-a28f4624fb14", "name": "Library Assert", "publisher": "Microsoft", "version": "28.0.0.0" },
            { "id": "5a3f0b11-3719-4a10-8010-000000003719", "name": "LAPC App", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "idRanges": [ { "from": 63710, "to": 63719 } ],
          "runtime": "13.0"
        }
        """);
        File.WriteAllText(Path.Combine(tests, "T.Codeunit.al"), """
        codeunit 63710 "LAPC Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "Library Assert";

            [Test]
            procedure HelperAnswersThroughLibraryAssert()
            var
                H: Codeunit "LAPC Helper";
            begin
                Assert.AreEqual(42, H.Answer(), 'helper');
            end;
        }
        """);
    }

    private (string Output, int Exit) RunRunner(string app, string tests, string testApps, string platformApps)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache --no-auto-provision --isolation test");
        args.Append($" --package-cache \"{testApps}\" --package-cache \"{platformApps}\"");
        args.Append($" \"{app}\" \"{tests}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// RED before the fix: `[dep-load-fail] Microsoft_Library Assert v…: EMIT-ZERO`, `FATAL:
    /// dependency compile failed`, exit 1, no test ran. After: Library Assert compiles against
    /// System.app, the one test runs through Assert.AreEqual and passes, exit 0.
    /// </summary>
    [SkippableFact]
    public void ConsumerWithoutPlatformKey_LibraryAssertDependency_CompilesAndRuns()
    {
        TestArtifacts.SkipIfMissing();
        var dirs = FindProvisionedDirs();
        Skip.If(dirs == null, "no provisioned Microsoft_Library Assert.app + System.app pair on this box");
        WriteBundles(out var app, out var tests);

        var (output, exit) = RunRunner(app, tests, dirs!.Value.TestApps, dirs.Value.PlatformApps);

        Assert.DoesNotContain("EMIT-ZERO", output);
        Assert.DoesNotContain("dep-load-fail", output);
        Assert.True(Regex.IsMatch(output, @"PASS\s+\S*\bHelperAnswersThroughLibraryAssert\b"),
            $"no PASS line for HelperAnswersThroughLibraryAssert. Output:\n{output}");
        Assert.Equal(0, exit);
    }
}
