// CoveragePackagedSiblingSourceTests — #4991. The #4973 layout: App.Test/.alpackages/App.app is
// what runs, and App/ (its source) sits next to the test app. A statement the package executes
// may be reported against App/ only when App/ holds the text the package was compiled from;
// otherwise it is reported against the package's own embedded source.
// docs/coverage-attribution.md#a-sibling-source-folder-next-to-a-package-4991
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CoveragePackagedSiblingSourceTests : IDisposable
{
    private const int SpawnTimeoutMs = 240_000;
    private static readonly string ProjectPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner"));
    private static readonly Guid AppId = Guid.Parse("4d1f6a0e-8b5c-4f0d-9a77-5e2c1b3d4991");
    private static readonly Guid TestAppId = Guid.Parse("7a2e9c41-0f3b-4d6e-8c15-2b9d6e0f4991");

    private const string ExecutedText = "exit(Value * 2);";
    private const int ExecutedLineInPackage = 5;

    // Line 5 is the executed statement. The edited copy pushes it to line 8 and leaves a comment
    // on line 5, which is what a report keyed to the package's text names there.
    private const string PackagedHelper = """
        codeunit 79891 "PkgSib Helper"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * 2);
            end;
        }
        """;

    private const string EditedHelper = """
        codeunit 79891 "PkgSib Helper"
        {
            procedure Twice(Value: Integer): Integer
            begin
                // edited after App.app was built, and not repackaged
                // so every line below this one moved down by three

                exit(Value * 2);
            end;
        }
        """;

    private const string TestsSource = """
        codeunit 79892 "PkgSib Tests"
        {
            Subtype = Test;

            [Test]
            procedure CallsApp()
            var
                H: Codeunit "PkgSib Helper";
            begin
                if H.Twice(21) <> 42 then
                    Error('CallsApp: the app returned %1', H.Twice(21));
            end;
        }
        """;

    private readonly string _root = TestScratch.Dir("al-runner-coverage-packaged-sibling");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteManifest(string dir, Guid id, string name, bool dependsOnApp)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), JsonSerializer.Serialize(new
        {
            id,
            name,
            publisher = "AL Runner",
            version = "1.0.0.0",
            platform = "1.0.0.0",
            runtime = "14.0",
            idRanges = new[] { new { from = 79890, to = 79899 } },
            dependencies = dependsOnApp
                ? new[] { new { id = AppId, name = "PkgSib App", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        }));
    }

    /// <summary>App/, App.Test/, and App.Test/.alpackages/App.app built from <see cref="PackagedHelper"/>;
    /// then App/'s file is replaced with <paramref name="siblingHelper"/>, not repackaged.</summary>
    private (string App, string TestApp) Layout(string siblingHelper)
    {
        var app = Path.Combine(_root, "App");
        var testApp = Path.Combine(_root, "App.Test");
        WriteManifest(app, AppId, "PkgSib App", dependsOnApp: false);
        var helperFile = Path.Combine(app, "src", "Helper.Codeunit.al");
        Directory.CreateDirectory(Path.GetDirectoryName(helperFile)!);
        File.WriteAllText(helperFile, PackagedHelper);
        WriteManifest(testApp, TestAppId, "PkgSib App Test", dependsOnApp: true);
        File.WriteAllText(Path.Combine(testApp, "Tests.Codeunit.al"), TestsSource);

        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(app, "app.json"))!;
        var packages = Path.Combine(testApp, ".alpackages");
        Directory.CreateDirectory(packages);
        var symbols = JsonSerializer.SerializeToUtf8Bytes(new
        {
            AppId, Name = "PkgSib App", Publisher = "AL Runner", Version = "1.0.0.0", RuntimeVersion = "14.0",
            Codeunits = new[] { new { Id = 79891, Name = "PkgSib Helper", Methods = new[] { new {
                Id = 1516892452, Name = "Twice", ReturnTypeDefinition = new { Name = "Integer" },
                Parameters = new[] { new { Name = "Value", TypeDefinition = new { Name = "Integer" } } }
            } } } },
        });
        InProcessAppPackager.EmitAppPackageToFile(app, identity,
            Path.Combine(packages, "AL Runner_PkgSib App_1.0.0.0.app"), symbols);

        File.WriteAllText(helperFile, siblingHelper);
        return (app, testApp);
    }

    private (string Output, int Exit) Spawn(params string[] args)
    {
        var sb0 = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        foreach (var a in args) sb0.Append(" \"").Append(a).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = sb0.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(SpawnTimeoutMs), $"runner did not exit within {SpawnTimeoutMs / 1000}s");
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>The Helper's reported file (absolute) and the lines carrying a hit.</summary>
    private (string File, int[] HitLines, string Raw) CliCoverage(string testApp)
    {
        var covPath = Path.Combine(_root, "coverage.xml");
        var args = new List<string> { testApp, "--cache", Path.Combine(_root, "cache"), "--coverage", "--coverage-out", covPath };
        args.AddRange(TestBuildConfig.BcVersionArg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var (output, exit) = Spawn(args.ToArray());
        Assert.True(exit == 0, $"the one test must pass, exit was {exit}.\n{output}");
        var doc = XDocument.Load(covPath);
        var helper = doc.Descendants("class").FirstOrDefault(c =>
            c.Attribute("filename")!.Value.Replace('\\', '/').EndsWith("/Helper.Codeunit.al", StringComparison.Ordinal));
        var files = string.Join(", ", doc.Descendants("class").Select(c => c.Attribute("filename")!.Value));
        Assert.True(helper is not null, $"the packaged Helper is absent from the report. Files: {files}\n{output}");
        var hits = helper!.Descendants("line").Where(l => (int)l.Attribute("hits")! > 0)
            .Select(l => (int)l.Attribute("number")!).ToArray();
        return (Path.GetFullPath(Path.Combine(_root, helper.Attribute("filename")!.Value)), hits, $"Files: {files}\n{output}");
    }

    private static bool IsUnder(string file, string dir) =>
        Path.GetFullPath(file).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Every hit line of the reported file holds the statement that ran.</summary>
    private static void AssertHitLinesHoldTheExecutedStatement(string file, int[] hitLines, string raw)
    {
        Assert.True(hitLines.Length > 0, $"the Helper's executed statement carries no hit.\n{raw}");
        var lines = File.ReadAllLines(file);
        foreach (var line in hitLines)
            Assert.True(line <= lines.Length && lines[line - 1].Contains(ExecutedText, StringComparison.Ordinal),
                $"{file} line {line} carries a hit but reads '{(line <= lines.Length ? lines[line - 1].Trim() : "<past end of file>")}', "
                + $"not the statement that ran ({ExecutedText}).\n{raw}");
    }

    [SkippableFact]
    public void SiblingSourceMatchingThePackage_IsWhereTheStatementIsReported()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp) = Layout(PackagedHelper);

        var (file, hits, raw) = CliCoverage(testApp);

        Assert.True(IsUnder(file, app), $"App/ holds the package's text, so the Helper must be reported there, not at {file}.\n{raw}");
        Assert.Equal(new[] { ExecutedLineInPackage }, hits);
        AssertHitLinesHoldTheExecutedStatement(file, hits, raw);
    }

    [SkippableFact]
    public void SiblingSourceEditedAfterPackaging_IsNotWhereTheStatementIsReported()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp) = Layout(EditedHelper);

        var (file, hits, raw) = CliCoverage(testApp);

        Assert.False(IsUnder(file, app), $"App/ was edited after packaging, so its lines are not the ones that ran; reported at {file}.\n{raw}");
        Assert.Equal(new[] { ExecutedLineInPackage }, hits);
        AssertHitLinesHoldTheExecutedStatement(file, hits, raw);
    }

    [SkippableFact]
    public async Task Server_CoverageAndPerTestCoverage_ReportTheLinesThatRan_WhenTheSiblingWasEdited()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp) = Layout(EditedHelper);
        await using var server = await CliServer.StartAsync(new[] { "--cache", Path.Combine(_root, "server-cache") });
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { testApp },
            packagePaths = Array.Empty<string>(),
            coverage = true,
            perTestCoverage = true,
        }), TimeSpan.FromSeconds(240));
        var raw = string.Join("\n", lines) + "\n--- stderr ---\n" + server.StdErr;
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        Assert.True(summary.GetProperty("failed").GetInt32() == 0, raw);

        var helper = summary.GetProperty("coverage").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(f =>
            f!.Value.GetProperty("file").GetString()!.EndsWith("/Helper.Codeunit.al", StringComparison.Ordinal));
        Assert.True(helper is not null, $"the packaged Helper is absent from the coverage table.\n{raw}");
        var file = helper!.Value.GetProperty("file").GetString()!;
        var hits = helper.Value.GetProperty("statements").EnumerateArray()
            .Where(s => s.GetProperty("hits").GetInt32() > 0).Select(s => s.GetProperty("line").GetInt32()).ToArray();

        Assert.False(IsUnder(file, app), $"coverage names the edited App/ file {file}.\n{raw}");
        AssertHitLinesHoldTheExecutedStatement(file, hits, raw);
        // perTestCoverage shares the map; no per-test row may name the edited App/ file either.
        var appPrefix = Path.GetFullPath(app).Replace('\\', '/') + "/";
        Assert.DoesNotContain(appPrefix + "src/Helper.Codeunit.al", string.Join("\n", lines), StringComparison.Ordinal);
    }
}

/// <summary>#4991, in-process: what <see cref="AlCoverageSourceMap.Build"/> does with a sibling root
/// marked as verified against a package's root, per object.</summary>
[Collection(BcEngineCollection.Name)]
public sealed class CoveragePackagedSiblingSourceMapTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root = TestScratch.Dir("al-runner-packaged-sibling-map");

    public CoveragePackagedSiblingSourceMapTests(BcEngineFixture engine) => _engine = engine;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Abs(string p) => Path.GetFullPath(p).Replace('\\', '/');

    private const string Same = "codeunit 79893 \"PkgSib Same\"\n{\n    procedure A()\n    begin\n    end;\n}\n";
    private const string Packaged = "codeunit 79894 \"PkgSib Moved\"\n{\n    procedure A()\n    begin\n    end;\n}\n";
    private const string Edited = "codeunit 79894 \"PkgSib Moved\"\n{\n\n    procedure A()\n    begin\n    end;\n}\n";

    /// <summary>A packaged root holding <c>packagedFiles</c> (or missing when null) and a sibling
    /// root, App/, holding <c>siblingFiles</c>, marked to be verified against the packaged root.</summary>
    private (AlSourceLocationMap Map, string Sibling, string Packaged) Build(
        (string Name, string Text)[]? packagedFiles, (string Name, string Text)[] siblingFiles)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var packaged = Path.Combine(_root, "key.src");
        var sibling = Path.Combine(_root, "App");
        if (packagedFiles != null)
        {
            Write(Path.Combine(packaged, "app.json"), "{}");
            foreach (var (name, text) in packagedFiles) Write(Path.Combine(packaged, "src", name), text);
        }
        Write(Path.Combine(sibling, "app.json"), "{}");
        foreach (var (name, text) in siblingFiles) Write(Path.Combine(sibling, "src", name), text);
        var roots = new CoverageRoots(new[] { packaged, sibling },
            new Dictionary<string, string>(StringComparer.Ordinal) { [sibling] = packaged });
        return (AlCoverageSourceMap.Build(roots, relativeTo: null), sibling, packaged);
    }

    [SkippableFact]
    public void PerObject_TheSiblingKeepsWhatMatchesThePackage_AndThePackageKeepsWhatDoesNot()
    {
        var (map, sibling, packaged) = Build(
            new[] { ("Same.Codeunit.al", Same), ("Moved.Codeunit.al", Packaged) },
            new[] { ("Same.Codeunit.al", Same), ("Moved.Codeunit.al", Edited) });

        Assert.Equal(Abs(Path.Combine(sibling, "src", "Same.Codeunit.al")), map[("CodeUnit", 79893)]);
        Assert.Equal(Abs(Path.Combine(packaged, "src", "Moved.Codeunit.al")), map[("CodeUnit", 79894)]);
        Assert.Equal(Packaged.Split('\n').SkipLast(1), map.ObjectSourceLines("CodeUnit", 79894)!);
    }

    // The third state: the package's AL could not be read, so nothing says what ran. The sibling's
    // object must not be attributed in its place, and the map says it is incomplete.
    [SkippableFact]
    public void AnUnreadablePackageRoot_LeavesTheSiblingsObjectUnmapped_AndTheMapIncomplete()
    {
        var (map, _, packaged) = Build(null, new[] { ("Same.Codeunit.al", Same) });

        Assert.False(map.ContainsKey(("CodeUnit", 79893)),
            $"the package's text is unknown, yet the object was attributed to {(map.TryGetValue(("CodeUnit", 79893), out var p) ? p : "")}");
        Assert.True(map.IsIncomplete);
        Assert.Equal(packaged, Assert.Single(map.ScanFailures).Path);
    }
}
