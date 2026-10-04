// #5302 — `--precompile` of a source .app whose manifest declares a dependency that no package cache
// can serve. The bundled run, the sibling pre-pass and the layered pre-pass refuse that by name
// (the provisioning-gap report); --precompile called the same resolver with no handler, so the
// exception reached Main and the process aborted with SIGABRT (exit 134), which a caller cannot tell
// from a crash and which writes a heap dump on a CI leg exporting DOTNET_DbgEnableMiniDump.
//
// Runner-only claim: how the runner reports a resolution failure and which exit code it carries. Every
// spawn is the real CLI, because the wiring is ProgramSupport.RunPrecompile.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompileDependencyRefusalTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string GhostId = "d0105132-eeee-4b22-8c33-d44455566677";
    private const string GhostPublisher = "Nobody";
    private const string GhostName = "Ghost Pkg";
    private const string AppId = "71000000-0000-4000-8000-0000000000de";

    private readonly string _scratch = TestScratch.Dir("al-runner-precompile-dep-refusal");

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// The reported shape: the declared package is in no cache. The refusal names it, carries the
    /// provisioning-gap report the other paths print, exits 2 (not 134, not 3), and writes no DLL.
    /// </summary>
    [SkippableFact]
    public void Precompile_DeclaredDependencyAbsentFromEveryCache_IsRefusedByNameWithExit2()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("absent");
        var app = WriteSourceApp(fx.AppDir, (GhostId, GhostPublisher, GhostName, "1.0.0.0"));

        var run = RunPrecompile(fx, app);

        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}\n{run.Output}");
        Assert.DoesNotContain("Unhandled exception", run.Output);
        Assert.Contains("A required dependency package is missing from your package cache.", run.Output);
        Assert.Contains($"Missing: {GhostPublisher}/{GhostName} v1.0.0.0", run.Output);
        Assert.Contains($"App ID:  {GhostId}", run.Output);
        Assert.DoesNotContain("DEP-RESOLVE-FAIL", run.Output);
        Assert.False(File.Exists(fx.OutDll), "a DLL was written for an app whose dependency could not be resolved");
    }

    /// <summary>
    /// The other exception the resolver raises for the same call: the package IS in the cache, but only
    /// below the declared minimum. It is a version gap, reported as one, and refused the same way.
    /// </summary>
    [SkippableFact]
    public void Precompile_DeclaredDependencyOnlyBelowTheMinimumVersion_IsRefusedAsAVersionGap()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("too-old");
        WritePackage(fx.PkgDir, GhostId, GhostPublisher, GhostName, "0.5.0.0");
        var app = WriteSourceApp(fx.AppDir, (GhostId, GhostPublisher, GhostName, "1.0.0.0"));

        var run = RunPrecompile(fx, app);

        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}\n{run.Output}");
        Assert.DoesNotContain("Unhandled exception", run.Output);
        // The detailed version-gap report, not the one-line DEP-RESOLVE-FAIL the catch-all prints.
        Assert.Contains("This is a VERSION gap", run.Output);
        Assert.Contains($"Required: {GhostPublisher}/{GhostName} v1.0.0.0 or newer", run.Output);
        Assert.Contains("Available (all too old): v0.5.0.0", run.Output);
        Assert.DoesNotContain("DEP-RESOLVE-FAIL", run.Output);
        // Not the "add the missing package" advice: the package is present.
        Assert.DoesNotContain("A required dependency package is missing", run.Output);
        Assert.False(File.Exists(fx.OutDll), "a DLL was written for an app whose dependency could not be resolved");
    }

    /// <summary>
    /// A failure of the same call that is neither provisioning nor version: two cached packages
    /// that depend on each other. Named and refused, never an abort.
    /// </summary>
    [SkippableFact]
    public void Precompile_DependencyCycleInTheCache_FailsNamedWithExit2()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("cycle");
        const string aId = "d0105132-c0a0-4b22-8c33-d44455566677", bId = "d0105132-c0b0-4b22-8c33-d44455566677";
        WritePackage(fx.PkgDir, aId, "Cycle ISV", "Cycle A", "1.0.0.0", (bId, "Cycle ISV", "Cycle B", "1.0.0.0"));
        WritePackage(fx.PkgDir, bId, "Cycle ISV", "Cycle B", "1.0.0.0", (aId, "Cycle ISV", "Cycle A", "1.0.0.0"));
        var app = WriteSourceApp(fx.AppDir, (aId, "Cycle ISV", "Cycle A", "1.0.0.0"));

        var run = RunPrecompile(fx, app);

        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}\n{run.Output}");
        Assert.DoesNotContain("Unhandled exception", run.Output);
        Assert.Contains("--precompile: DEP-RESOLVE-FAIL", run.Output);
        Assert.Contains("Dependency cycle detected", run.Output);
        Assert.False(File.Exists(fx.OutDll), "a DLL was written for an app whose dependency could not be resolved");
    }

    /// <summary>
    /// The control the three refusals need: the same app with its dependency present resolves and
    /// compiles, so a handler that refused every declared dependency would fail here.
    /// </summary>
    [SkippableFact]
    public void Precompile_DeclaredDependencyPresentInTheCache_StillWritesTheDll()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("present");
        WritePackage(fx.PkgDir, GhostId, GhostPublisher, GhostName, "1.0.0.0");
        var app = WriteSourceApp(fx.AppDir, (GhostId, GhostPublisher, GhostName, "1.0.0.0"));

        var run = RunPrecompile(fx, app);

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.Output}");
        Assert.DoesNotContain("A required dependency package is missing", run.Output);
        Assert.True(File.Exists(fx.OutDll) && new FileInfo(fx.OutDll).Length > 0, run.Output);
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    private sealed record Fixture(string AppDir, string PkgDir, string CacheDir, string OutDll);

    private Fixture Arrange(string name)
    {
        var root = Path.Combine(_scratch, name);
        var appDir = Path.Combine(root, "app");
        var pkgDir = Path.Combine(root, "pkg");
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(pkgDir);
        Directory.CreateDirectory(cacheDir);
        return new Fixture(appDir, pkgDir, cacheDir, Path.Combine(cacheDir, "out.dll"));
    }

    private const string LibrarySource = """
        codeunit 71301 "P98 Lib B64"
        {
            procedure Enc(T: Text): Text
            begin
                exit(T);
            end;
        }
        """;

    private static byte[] NavxZip(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) fill(zip);
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        w.Write(text);
    }

    private static string DependenciesXml(params (string Id, string Publisher, string Name, string MinVersion)[] deps) =>
        "<Dependencies>" + string.Concat(deps.Select(d =>
            $"""<Dependency Id="{d.Id}" Name="{d.Name}" Publisher="{d.Publisher}" MinVersion="{d.MinVersion}"/>""")) + "</Dependencies>";

    /// <summary>A package a cache can hold: a manifest declaring its own dependencies, and no sources or symbols.</summary>
    private static void WritePackage(string dir, string id, string publisher, string name, string version,
        params (string Id, string Publisher, string Name, string MinVersion)[] deps)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{id}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
              {DependenciesXml(deps)}
            </Package>
            """;
        File.WriteAllBytes(Path.Combine(dir, $"{publisher}_{name}_{version}.app"),
            NavxZip(zip => WriteEntry(zip, "NavxManifest.xml", xml)));
    }

    /// <summary>The app handed to --precompile: a manifest declaring <paramref name="deps"/> plus one source file.</summary>
    private static string WriteSourceApp(string dir,
        params (string Id, string Publisher, string Name, string MinVersion)[] deps)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{AppId}" Name="P98 PreMissing" Publisher="Rev62" Version="1.0.0.0"/>
              {DependenciesXml(deps)}
            </Package>
            """;
        var path = Path.Combine(dir, "Rev62_P98 PreMissing_1.0.0.0.app");
        File.WriteAllBytes(path, NavxZip(zip =>
        {
            WriteEntry(zip, "NavxManifest.xml", xml);
            WriteEntry(zip, "src/Lib.al", LibrarySource);
        }));
        return path;
    }

    // ── runner invocation ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One --precompile child. Before the fix the refusal cases abort (exit 134), and the C# CI legs
    /// export <c>DOTNET_DbgEnableMiniDump</c> (.github/workflows/bc-tests.yml), which the child would
    /// inherit: a regression to the abort would write a heap dump of several hundred MB into the artifact
    /// real crashes are diagnosed from. The child's own copy turns it off.
    /// </summary>
    [ExpectedRunnerAbort]
    internal static ProcessStartInfo BuildChildStartInfo(string app, string outDll, string pkgDir, string cacheDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // --precompile is a mode, read only as the first argument.
            Arguments = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath))
                .Append($" --precompile \"{app}\" --out \"{outDll}\"")
                .Append(TestBuildConfig.BcVersionArg)
                .Append($" --package-cache \"{pkgDir}\" --cache \"{cacheDir}\"").ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        return psi.SwitchOff();
    }

    /// <summary>
    /// The children are started with the heap-dump variable turned off even when the host has it on.
    /// Pure: it reads the start info and starts nothing.
    /// </summary>
    [Fact]
    public void Children_AreStartedWithTheCrashDumpSwitchOff()
    {
        var psi = BuildChildStartInfo("/unused.app", "/unused.dll", "/unused-pkg", "/unused-cache");
        Assert.Equal("0", psi.Environment[CrashDump.SwitchVariable]);
    }

    private static (int ExitCode, string Output) RunPrecompile(Fixture fx, string app)
    {
        var sb = new StringBuilder();
        using var p = Process.Start(BuildChildStartInfo(app, fx.OutDll, fx.PkgDir, fx.CacheDir))!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
