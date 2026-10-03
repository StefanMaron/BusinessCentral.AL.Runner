// #5132 / #5299 — what the CLI does with a dependency package that carries no SymbolReference.json,
// and what `--per-suite` does with a suite none of whose objects emit.
//
// The bundled loop and --server compile such a package (BcCompiler.ScopeSymbolBearingDepsOnly keeps
// it out of the spec list BC's package scanner reads, where it would answer AL1023 then AL1022).
// Four more emit sites ran outside that scope and failed on AL1022: the --per-suite loop, --precompile,
// and the two pre-passes that compile a source dependency's symbols (sibling source app, layered impl).
// Runner-only claim: which site applies the scope is the runner's own wiring, and the bundled run of
// a suite declaring the package is the reference.
//
// Every test spawns the real CLI, because the wiring lives in Program.cs and SiblingCompile.cs.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class PerSuiteSymbolLessDependencyTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string SuiteId = "d0105132-aaaa-4b22-8c33-d44455566677";
    private const string DepId = "d0105132-dddd-4b22-8c33-d44455566677";
    private const string DepPublisher = "Fabrikam ISV";
    private const string DepName = "Fabrikam Dep Y";
    private const string DepVersion = "1.0.0.0";

    // The shipped System Application, which carries SymbolReference.json: the dependency the scope must KEEP
    // beside the symbol-less package (#5303). Its code is a real value to assert (Base64 Convert), so a scope
    // that dropped every dependency fails on AL0185 instead of passing. The minimum is the oldest major a
    // leg runs; the runner resolves the one the engine was built for.
    private const string SystemApplicationDep =
        """{ "id": "63ca2fa4-4f03-4f2b-a480-172fef340d3f", "name": "System Application", "publisher": "Microsoft", "version": "27.0.0.0" }""";

    private const string PassingCodeunit = """
        codeunit 60795 "PerSuite Probe"
        {
            Subtype = Test;

            [Test]
            procedure ProbeWorks()
            begin
            end;
        }
        """;

    private const string Base64Codeunit = """
        codeunit 60795 "PerSuite Probe"
        {
            Subtype = Test;

            [Test]
            procedure ProbeWorks()
            var
                B64: Codeunit "Base64 Convert";
            begin
                if B64.ToBase64('A') <> 'QQ==' then
                    Error('wrong encoding');
            end;
        }
        """;

    // A variable of a codeunit nobody declares: BC answers AL0185 and drops the object.
    private const string BrokenCodeunit = """
        codeunit 60795 "PerSuite Probe"
        {
            Subtype = Test;

            [Test]
            procedure ProbeWorks()
            var
                Api: Codeunit "PerSuite Does Not Exist";
            begin
                Api.Foo();
            end;
        }
        """;

    private readonly string _scratch = TestScratch.Dir("al-runner-persuite-symbolless");

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// The suite declares a package whose .app has no SymbolReference.json. Bundled mode compiles
    /// and passes it; --per-suite used to fail the suite with AL1022. The second spawn shares the
    /// first one's --cache root, so a state the scope left behind (the spec list, the dependency
    /// caches) would show up on the warm run.
    /// </summary>
    [SkippableFact]
    public void PerSuite_SuiteDeclaringASymbolLessPackage_CompilesAndPasses_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("symbol-less-pass", PassingCodeunit, withPackage: true);

        var cold = Run(fx, perSuite: true);
        AssertPassedOne(cold);
        Assert.DoesNotContain("AL1022", cold.Output);

        var warm = Run(fx, perSuite: true);
        AssertPassedOne(warm);
        Assert.DoesNotContain("AL1022", warm.Output);
    }

    /// <summary>
    /// The scope drops a package from the compiler's list; it must not turn a genuinely absent
    /// one into a pass. With the .app missing from the cache the run is refused before any
    /// compile, naming the package — the same refusal the bundled run gives.
    /// </summary>
    [SkippableFact]
    public void PerSuite_DeclaredPackageMissingFromTheCache_IsStillRefusedByName()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("package-missing", PassingCodeunit, withPackage: false);

        var run = Run(fx, perSuite: true);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("A required dependency package is missing", run.Output);
        Assert.Contains($"{DepPublisher}/{DepName}", run.Output);
        Assert.DoesNotMatch(@"passed\s+1\b", run.Output);
    }

    /// <summary>
    /// The scope hides the package, not the suite's own errors: with the symbol-less package
    /// declared and a real AL0185 in the suite, --per-suite must still fail on that error.
    /// </summary>
    [SkippableFact]
    public void PerSuite_SymbolLessPackageDeclared_AGenuineAlErrorInTheSuiteStillFails()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("symbol-less-real-error", BrokenCodeunit, withPackage: true);

        var run = Run(fx, perSuite: true);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("AL0185", run.Output);
        Assert.Contains("PerSuite Does Not Exist", run.Output);
        Assert.DoesNotContain("AL1022", run.Output);
    }

    /// <summary>
    /// #5299: a suite none of whose objects emit used to report "Tests: 0 ... PASSED, exit code 0"
    /// under --per-suite while the bundled run answered EMIT-ZERO with exit 3. No dependency in
    /// this fixture, so the claim is independent of the scope above.
    /// </summary>
    [SkippableFact]
    public void PerSuite_EveryObjectFailsToEmit_FailsInsteadOfPassingZeroTests()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("zero-emit", BrokenCodeunit, withPackage: false, declareDependency: false);

        var perSuite = Run(fx, perSuite: true);

        Assert.Equal(3, perSuite.ExitCode);
        Assert.Contains("EMIT-ZERO", perSuite.Output);
        Assert.Contains("AL0185", perSuite.Output);
        Assert.DoesNotContain("PASSED", perSuite.Output);

        // The reference the issue names: the bundled run fails the same suite the same way.
        var bundled = Run(fx, perSuite: false);
        Assert.Equal(perSuite.ExitCode, bundled.ExitCode);
        Assert.Contains("EMIT-ZERO", bundled.Output);
    }

    /// <summary>
    /// A source dependency sitting next to the suite (a sibling directory with its own app.json)
    /// declares the symbol-less package itself. The pre-pass that compiles its symbols used to
    /// fail on AL1022 for a package nothing it compiles needs.
    /// </summary>
    [SkippableFact]
    public void SiblingSourceDependency_DeclaringASymbolLessPackage_CompilesAndPasses()
    {
        TestArtifacts.SkipIfMissing();
        var fx = ArrangeLibrary("sibling-source-dep", withPackage: true);

        var run = Run(fx, perSuite: false);

        AssertPassedOne(run);
        Assert.DoesNotContain("AL1022", run.Output);
        Assert.Contains("[source-dep] Lib B", run.Output); // the pre-pass under test ran

        // Warm, same --cache root: what the cold run wrote is served, and still passes.
        var warm = Run(fx, perSuite: false);
        AssertPassedOne(warm);
        Assert.Contains("[source-dep] cache HIT Lib B", warm.Output);
    }

    /// <summary>The same dependency handed to the CLI as a second bundle: the layered pre-pass.</summary>
    [SkippableFact]
    public void LayeredImplementation_DeclaringASymbolLessPackage_CompilesAndPasses()
    {
        TestArtifacts.SkipIfMissing();
        var fx = ArrangeLibrary("layered-impl", withPackage: true);

        var run = Run(fx, perSuite: false, fx.Library!);

        AssertPassedOne(run);
        Assert.DoesNotContain("AL1022", run.Output);
        Assert.Contains("[layered] pre-built 1 impl package(s)", run.Output); // the pre-pass under test ran

        var warm = Run(fx, perSuite: false, fx.Library!);
        AssertPassedOne(warm);
        Assert.Contains("[layered] cache HIT Lib B", warm.Output);
    }

    /// <summary>
    /// The scope in the pre-passes drops the package from the compiler's list, not from
    /// resolution: with the .app absent the source dependency's own closure is unresolvable and
    /// the run still fails, naming the package.
    /// </summary>
    [SkippableFact]
    public void SiblingSourceDependency_DeclaredPackageMissingFromTheCache_StillFailsByName()
    {
        TestArtifacts.SkipIfMissing();
        var fx = ArrangeLibrary("sibling-package-missing", withPackage: false);

        var run = Run(fx, perSuite: false);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(DepName, run.Output);
        Assert.DoesNotMatch(@"passed\s+1\b", run.Output);
    }

    /// <summary>
    /// --precompile of a source .app that declares the symbol-less package: the emit used to fail
    /// the module on AL1022 and write no DLL.
    /// </summary>
    [SkippableFact]
    public void Precompile_AppDeclaringASymbolLessPackage_WritesTheDll()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("precompile-pass", PassingCodeunit, withPackage: true, declareDependency: false);
        var app = WriteSourceApp(fx.PkgDir, GoodLibrarySource);

        var run = RunPrecompile(fx, app);

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.Output}");
        Assert.DoesNotContain("AL1022", run.Output);
        Assert.True(new FileInfo(Path.Combine(fx.CacheDir, "out.dll")).Length > 0, run.Output);
    }

    /// <summary>The scope hides the package, not the app's own errors: --precompile still fails on AL0185.</summary>
    [SkippableFact]
    public void Precompile_SymbolLessPackageDeclared_AGenuineAlErrorStillFails()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("precompile-real-error", PassingCodeunit, withPackage: true, declareDependency: false);
        var app = WriteSourceApp(fx.PkgDir, BrokenLibrarySource);

        var run = RunPrecompile(fx, app);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("AL0185", run.Output);
        Assert.False(File.Exists(Path.Combine(fx.CacheDir, "out.dll")), "a DLL was written for a module that did not compile");
    }

    // ── the scope keeps a symbol-bearing dependency (#5303) ───────────────────────────────

    /// <summary>
    /// The scope drops the symbol-less package and ONLY that: the System Application, declared beside it,
    /// carries symbols and must stay, or the suite's own call into it fails to bind. A scope that dropped
    /// every dependency passes every test above, because none of them has a symbol-bearing one.
    /// </summary>
    [SkippableFact]
    public void PerSuite_ASymbolBearingDependencyBesideASymbolLessOne_StaysInTheCompile()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("keep-per-suite", Base64Codeunit, withPackage: true, systemApplication: true);

        var run = Run(fx, perSuite: true);

        AssertPassedOne(run);
        Assert.DoesNotContain("AL1022", run.Output);
        Assert.DoesNotContain("AL0185", run.Output);
    }

    /// <summary>The same keep side at the sibling pre-pass: the source dependency's own call into the System Application binds.</summary>
    [SkippableFact]
    public void SiblingSourceDependency_ASymbolBearingDependencyBesideASymbolLessOne_StaysInTheCompile()
    {
        TestArtifacts.SkipIfMissing();
        var fx = ArrangeLibrary("keep-sibling", withPackage: true, systemApplication: true);

        var run = Run(fx, perSuite: false);

        AssertPassedOne(run);
        Assert.Contains("[source-dep] Lib B", run.Output); // the pre-pass under test ran
    }

    /// <summary>The same keep side at the layered pre-pass.</summary>
    [SkippableFact]
    public void LayeredImplementation_ASymbolBearingDependencyBesideASymbolLessOne_StaysInTheCompile()
    {
        TestArtifacts.SkipIfMissing();
        var fx = ArrangeLibrary("keep-layered", withPackage: true, systemApplication: true);

        var run = Run(fx, perSuite: false, fx.Library!);

        AssertPassedOne(run);
        Assert.Contains("[layered] pre-built 1 impl package(s)", run.Output); // the pre-pass under test ran
    }

    /// <summary>The same keep side under --precompile: the DLL is written, which it is not when the call cannot bind.</summary>
    [SkippableFact]
    public void Precompile_ASymbolBearingDependencyBesideASymbolLessOne_StaysInTheCompile()
    {
        TestArtifacts.SkipIfMissing();
        // --precompile does not provision: the System Application comes from the platform package cache.
        TestArtifacts.SkipIfDirectoryMissing(TestArtifacts.PlatformAppsDir(), "the platform package cache");
        var fx = Arrange("keep-precompile", PassingCodeunit, withPackage: true, declareDependency: false);
        var app = WriteSourceApp(fx.PkgDir, Base64LibrarySource, systemApplication: true);

        var run = RunPrecompile(fx, app, TestArtifacts.PlatformAppsDir());

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.Output}");
        Assert.DoesNotContain("AL0185", run.Output);
        Assert.True(new FileInfo(Path.Combine(fx.CacheDir, "out.dll")).Length > 0, run.Output);
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    private sealed record Fixture(string Suite, string PkgDir, string CacheDir, string? Library = null);

    /// <summary>
    /// One suite plus a package cache. No <c>application</c> or <c>platform</c> property, so the
    /// resolved closure is exactly what this test declares
    /// (.claude/rules/no-base-app-in-csharp-tests.md).
    /// </summary>
    private Fixture Arrange(string name, string codeunit, bool withPackage, bool declareDependency = true,
        bool systemApplication = false)
    {
        var root = Path.Combine(_scratch, name);
        var suite = Path.Combine(root, "suite");
        var pkgDir = Path.Combine(root, "pkg");
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(suite);
        Directory.CreateDirectory(pkgDir);
        Directory.CreateDirectory(cacheDir);

        var deps = string.Join(", ", new[]
        {
            declareDependency
                ? $$"""{ "id": "{{DepId}}", "name": "{{DepName}}", "publisher": "{{DepPublisher}}", "version": "{{DepVersion}}" }"""
                : "",
            systemApplication ? SystemApplicationDep : "",
        }.Where(d => d.Length > 0));
        File.WriteAllText(Path.Combine(suite, "app.json"), $$"""
        {
          "id": "{{SuiteId}}",
          "name": "PerSuite SymbolLess Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ {{deps}} ],
          "idRanges": [ { "from": 60795, "to": 60796 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(suite, "Probe.Codeunit.al"), codeunit);
        if (withPackage) WriteSymbolLessApp(pkgDir);
        return new Fixture(suite, pkgDir, cacheDir);
    }


    private const string LibraryId = "d0105132-bbbb-4b22-8c33-d44455566677";

    /// <summary>
    /// <c>suite</c> declares a dependency on <c>lib</c>, a source app in the suite's parent
    /// directory; <c>lib</c> is the one declaring the (symbol-less) package, so nothing but the
    /// pre-pass that compiles <c>lib</c>'s symbols ever sees it.
    /// </summary>
    private Fixture ArrangeLibrary(string name, bool withPackage, bool systemApplication = false)
    {
        var fx = Arrange(name, """
            codeunit 60795 "PerSuite Probe"
            {
                Subtype = Test;

                [Test]
                procedure ProbeWorks()
                var
                    Api: Codeunit "PerSuite Lib Api";
                begin
                    if Api.Answer() <> 42 then
                        Error('wrong answer');
                end;
            }
            """, withPackage: false, declareDependency: false);
        var root = Path.GetDirectoryName(fx.Suite)!;
        File.WriteAllText(Path.Combine(fx.Suite, "app.json"), $$"""
        {
          "id": "{{SuiteId}}",
          "name": "PerSuite SymbolLess Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ { "id": "{{LibraryId}}", "name": "Lib B", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "idRanges": [ { "from": 60795, "to": 60796 } ],
          "runtime": "14.0"
        }
        """);
        var lib = Path.Combine(root, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "app.json"), $$"""
        {
          "id": "{{LibraryId}}",
          "name": "Lib B",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ { "id": "{{DepId}}", "name": "{{DepName}}", "publisher": "{{DepPublisher}}", "version": "{{DepVersion}}" }{{(systemApplication ? ", " + SystemApplicationDep : "")}} ],
          "idRanges": [ { "from": 60800, "to": 60810 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(lib, "Lib.Codeunit.al"), systemApplication ? Base64LibrarySource : GoodLibrarySource);
        if (withPackage) WriteSymbolLessApp(fx.PkgDir);
        return fx with { Library = lib };
    }

    /// <summary>A minimal NAVX .app: a manifest and a payload, and no SymbolReference.json.</summary>
    private static void WriteSymbolLessApp(string dir)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepId}" Name="{DepName}" Publisher="{DepPublisher}" Version="{DepVersion}"/>
              <Dependencies />
            </Package>
            """;
        var stamp = new DateTimeOffset(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("NavxManifest.xml", System.IO.Compression.CompressionLevel.NoCompression);
            manifest.LastWriteTime = stamp;
            using (var es = manifest.Open()) es.Write(Encoding.UTF8.GetBytes(xml));
            var payload = zip.CreateEntry("payload.bin", System.IO.Compression.CompressionLevel.NoCompression);
            payload.LastWriteTime = stamp;
            using (var ps = payload.Open()) ps.Write(new byte[4096]);
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(Path.Combine(dir, $"{DepPublisher}_{DepName}_{DepVersion}.app"), result);
    }

    // ── runner invocation ─────────────────────────────────────────────────────────────────

    private static void AssertPassedOne((int ExitCode, string Output) run)
    {
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.Output}");
        // A passing exit code with zero tests is the #5299 shape, so the count is asserted too.
        Assert.True(Regex.IsMatch(run.Output, @"Tests:\s+1\s+passed\s+1\s+failed\s+0\s+errors\s+0"), run.Output);
    }


    private const string GoodLibrarySource = """
        codeunit 60800 "PerSuite Lib Api"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """;

    // The answer comes from the System Application's code, so it is 42 only when that dependency was in the compile.
    private const string Base64LibrarySource = """
        codeunit 60800 "PerSuite Lib Api"
        {
            procedure Answer(): Integer
            var
                B64: Codeunit "Base64 Convert";
            begin
                if B64.ToBase64('A') = 'QQ==' then
                    exit(42);
                exit(0);
            end;
        }
        """;

    private const string BrokenLibrarySource = """
        codeunit 60800 "PerSuite Lib Api"
        {
            procedure Answer(): Integer
            var
                Api: Codeunit "PerSuite Does Not Exist";
            begin
                exit(Api.Foo());
            end;
        }
        """;

    /// <summary>A source-only NAVX .app (manifest plus src/*.al) declaring the symbol-less package.</summary>
    private static string WriteSourceApp(string dir, string source, bool systemApplication = false)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{LibraryId}" Name="Lib B" Publisher="AL Runner" Version="1.0.0.0"/>
              <Dependencies><Dependency Id="{DepId}" Name="{DepName}" Publisher="{DepPublisher}" MinVersion="{DepVersion}"/>{(systemApplication ? """<Dependency Id="63ca2fa4-4f03-4f2b-a480-172fef340d3f" Name="System Application" Publisher="Microsoft" MinVersion="27.0.0.0"/>""" : "")}</Dependencies>
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("NavxManifest.xml").Open(), Encoding.UTF8)) w.Write(xml);
            using (var w = new StreamWriter(zip.CreateEntry("src/Lib.Codeunit.al").Open(), Encoding.UTF8)) w.Write(source);
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        var path = Path.Combine(dir, "AL Runner_Lib B_1.0.0.0.app");
        File.WriteAllBytes(path, result);
        return path;
    }

    private static (int ExitCode, string Output) RunPrecompile(Fixture fx, string app, string? platformApps = null) =>
        // --precompile is a mode, read only as the first argument.
        Spawn(new StringBuilder(TestBuildConfig.RunArgs(ProjectPath))
            .Append($" --precompile \"{app}\" --out \"{Path.Combine(fx.CacheDir, "out.dll")}\"")
            .Append(TestBuildConfig.BcVersionArg)
            .Append($" --package-cache \"{fx.PkgDir}\" --cache \"{fx.CacheDir}\"")
            .Append(platformApps == null ? "" : $" --package-cache \"{platformApps}\"").ToString());

    private static (int ExitCode, string Output) Run(Fixture fx, bool perSuite, params string[] extraPaths)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{fx.Suite}\"");
        foreach (var extra in extraPaths) args.Append($" \"{extra}\"");
        args.Append($" --package-cache \"{fx.PkgDir}\" --cache \"{fx.CacheDir}\"");
        if (perSuite) args.Append(" --per-suite");
        return Spawn(args.ToString());
    }

    private static (int ExitCode, string Output) Spawn(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
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
