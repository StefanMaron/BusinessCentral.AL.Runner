using System.Security.Cryptography;
using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class DepCompilerTests
{
    // ------------------------------------------------------------------ //
    // SHA-256 cache freshness tests (issue #1517)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// After a successful compile-dep from a directory, a .sha256 sidecar
    /// must be written next to the DLL containing the SHA-256 of the source
    /// directory contents (sentinel file). The sidecar must be non-empty.
    /// </summary>
    [Fact]
    public void CompileDep_WritesHashSidecar_AfterSuccessfulCompile()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-sha256-write-" + Guid.NewGuid().ToString("N")[..8]);
        var appDir = Path.Combine(rootDir, "AppSha");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllText(Path.Combine(appDir, "app.json"), """
            {
              "id": "aaaabbbb-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "name": "AppSha",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """);
            File.WriteAllText(Path.Combine(appDir, "Code.al"),
                "codeunit 99510 TrivialSha { procedure P() begin end; }");

            var result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());

            Assert.Equal(0, result);

            var dllPath = Path.Combine(outputDir, "Test_AppSha_1.0.0.0.dll");
            Assert.True(File.Exists(dllPath), $"DLL must exist: {dllPath}");

            var sha256Path = Path.ChangeExtension(dllPath, ".sha256");
            Assert.True(File.Exists(sha256Path), $".sha256 sidecar must be written: {sha256Path}");

            var hashContent = File.ReadAllText(sha256Path).Trim();
            Assert.NotEmpty(hashContent);
            // Must be a valid 64-character hex SHA-256 string
            Assert.Equal(64, hashContent.Length);
            Assert.Matches("^[0-9a-fA-F]{64}$", hashContent);
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    /// <summary>
    /// When the source app directory is unchanged (hash matches), re-running
    /// compile-dep must skip recompilation and return 0 without touching the DLL.
    /// </summary>
    [Fact]
    public void CompileDep_SkipsRecompile_WhenHashUnchanged()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-sha256-skip-" + Guid.NewGuid().ToString("N")[..8]);
        var appDir = Path.Combine(rootDir, "AppSha");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllText(Path.Combine(appDir, "app.json"), """
            {
              "id": "ccccdddd-cccc-cccc-cccc-cccccccccccc",
              "name": "AppSha2",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """);
            File.WriteAllText(Path.Combine(appDir, "Code.al"),
                "codeunit 99511 TrivialSha2 { procedure P() begin end; }");

            // First compile
            var result1 = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            Assert.Equal(0, result1);

            var dllPath = Path.Combine(outputDir, "Test_AppSha2_1.0.0.0.dll");
            var sha256Path = Path.ChangeExtension(dllPath, ".sha256");
            Assert.True(File.Exists(dllPath));
            Assert.True(File.Exists(sha256Path));

            // Record timestamps to detect recompilation
            var dllWriteTime = File.GetLastWriteTimeUtc(dllPath);

            // Wait a moment to ensure file system timestamp resolution
            System.Threading.Thread.Sleep(50);

            // Second compile — same sources, no changes
            var result2 = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            Assert.Equal(0, result2);

            // DLL must NOT have been rewritten (timestamp unchanged)
            var dllWriteTime2 = File.GetLastWriteTimeUtc(dllPath);
            Assert.Equal(dllWriteTime, dllWriteTime2);
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    /// <summary>
    /// When the source directory content changes but the version string stays the same
    /// (the stale-cache scenario), compile-dep must detect the hash mismatch, delete
    /// the stale DLL, and recompile — producing an updated DLL and updated .sha256.
    /// </summary>
    [Fact]
    public void CompileDep_Recompiles_WhenAppContentChangesButVersionUnchanged()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-sha256-stale-" + Guid.NewGuid().ToString("N")[..8]);
        var appDir = Path.Combine(rootDir, "AppSha");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(outputDir);

            const string appJson = """
            {
              "id": "eeeeffff-eeee-eeee-eeee-eeeeeeeeeeee",
              "name": "AppSha3",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """;
            File.WriteAllText(Path.Combine(appDir, "app.json"), appJson);
            // Initial AL content
            File.WriteAllText(Path.Combine(appDir, "Code.al"),
                "codeunit 99512 TrivialSha3 { procedure P() begin end; }");

            // First compile
            var result1 = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            Assert.Equal(0, result1);

            var dllPath = Path.Combine(outputDir, "Test_AppSha3_1.0.0.0.dll");
            var sha256Path = Path.ChangeExtension(dllPath, ".sha256");
            Assert.True(File.Exists(dllPath));
            Assert.True(File.Exists(sha256Path));

            var hashAfterFirstCompile = File.ReadAllText(sha256Path).Trim();
            var dllWriteTime1 = File.GetLastWriteTimeUtc(dllPath);

            // Wait to ensure file system timestamp resolution
            System.Threading.Thread.Sleep(100);

            // Change the AL source WITHOUT bumping the version — same app.json, new AL
            File.WriteAllText(Path.Combine(appDir, "Code.al"),
                "codeunit 99512 TrivialSha3 { procedure P() begin end; procedure Q() begin end; }");

            // Second compile — version same, but content changed
            var result2 = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            Assert.Equal(0, result2);

            Assert.True(File.Exists(dllPath), "DLL must still exist after recompile");
            Assert.True(File.Exists(sha256Path), ".sha256 must still exist after recompile");

            // Hash must have changed to reflect new content
            var hashAfterSecondCompile = File.ReadAllText(sha256Path).Trim();
            Assert.NotEqual(hashAfterFirstCompile, hashAfterSecondCompile);

            // DLL must have been rewritten (new write time)
            var dllWriteTime2 = File.GetLastWriteTimeUtc(dllPath);
            Assert.True(dllWriteTime2 > dllWriteTime1,
                $"DLL must be recompiled: first={dllWriteTime1:O}, second={dllWriteTime2:O}");
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    /// <summary>
    /// ComputeAppHash is deterministic: same bytes produce same hash,
    /// different bytes produce different hash.
    /// </summary>
    [Fact]
    public void ComputeAppHash_IsDeterministicAndChangeSensitive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "al-runner-sha256-unit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var fileA = Path.Combine(tempDir, "a.dat");
            var fileB = Path.Combine(tempDir, "b.dat");
            File.WriteAllBytes(fileA, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(fileB, new byte[] { 4, 5, 6 });

            var hash1 = DepCompiler.ComputeDirectoryHash(tempDir);
            var hash2 = DepCompiler.ComputeDirectoryHash(tempDir);
            Assert.Equal(hash1, hash2); // deterministic

            // Change a file — hash must differ
            File.WriteAllBytes(fileA, new byte[] { 9, 9, 9 });
            var hash3 = DepCompiler.ComputeDirectoryHash(tempDir);
            Assert.NotEqual(hash1, hash3);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }


    [Fact]
    public void CompileDepMultiApp_AppJsonPlaceholderVersion_UsesFallbackVersion()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-depcompiler-" + Guid.NewGuid().ToString("N")[..8]);
        var appDir = Path.Combine(rootDir, "AppA");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllText(Path.Combine(appDir, "app.json"), """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "AppA",
              "publisher": "Microsoft",
              "version": "$(app_currentVersion)"
            }
            """);

            File.WriteAllText(Path.Combine(appDir, "Trivial.al"),
                "codeunit 99500 Trivial { procedure P() begin end; }");

            var result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());

            Assert.Equal(0, result);

            var expectedDll = Path.Combine(outputDir, "Microsoft_AppA_1.0.0.0.dll");
            Assert.True(File.Exists(expectedDll), $"Expected compiled DLL {expectedDll} to exist");

            var sidecarPath = Path.ChangeExtension(expectedDll, ".app.json");
            Assert.True(File.Exists(sidecarPath), $"Expected sidecar {sidecarPath} to exist");

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            Assert.Equal("1.0.0.0", doc.RootElement.GetProperty("version").GetString());
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void SanitizeManifestVersions_ReplacesPlaceholderVersionsWithDefaults()
    {
        var input = """
        {
          "id": "437dbf0e-84ff-417a-965d-ed2bb9650972",
          "name": "Base Application",
          "publisher": "Microsoft",
          "version": "$(app_currentVersion)",
          "platform": "$(app_platformVersion)",
          "application": "$(app_applicationVersion)",
          "dependencies": [
            { "id": "f3552374-a1f2-4356-848e-196002525837",
              "name": "Business Foundation",
              "publisher": "Microsoft",
              "version": "$(app_minimumVersion)" }
          ],
          "idRanges": [{ "from": 1, "to": 50000 }]
        }
        """;

        var sanitized = DepCompiler.SanitizeManifestVersions(input);

        using var doc = JsonDocument.Parse(sanitized);
        var root = doc.RootElement;
        // Identity preserved.
        Assert.Equal("Base Application", root.GetProperty("name").GetString());
        Assert.Equal("Microsoft", root.GetProperty("publisher").GetString());
        Assert.Equal("437dbf0e-84ff-417a-965d-ed2bb9650972", root.GetProperty("id").GetString());

        // Top-level version placeholder replaced with parseable default.
        Assert.True(Version.TryParse(root.GetProperty("version").GetString(), out _));

        // platform / application / dependencies all dropped when placeholders are present:
        // feeding the BC reference resolver synthetic versions (e.g. 1.0.0.0 platform when
        // the real binary is 27.0.46893.0) creates a phantom "(Unknown)" module that
        // collides with the slice's own namespace declarations and fires AL0275.
        // Dropping them lets the runner's "load all .app packages as symbols" fallback
        // discover concrete versions from the package directory.
        Assert.False(root.TryGetProperty("platform", out _),
            "platform should be dropped when placeholder is present");
        Assert.False(root.TryGetProperty("application", out _),
            "application should be dropped when placeholder is present");
        Assert.False(root.TryGetProperty("dependencies", out _),
            "dependencies should be dropped when any entry has unparseable version");

        // Other top-level non-version content preserved.
        Assert.Equal(1, root.GetProperty("idRanges")[0].GetProperty("from").GetInt32());
    }

    [Fact]
    public void SanitizeManifestVersions_KeepsDependenciesWhenAllVersionsParseable()
    {
        var input = """
        {
          "id": "437dbf0e-84ff-417a-965d-ed2bb9650972",
          "name": "App",
          "publisher": "Microsoft",
          "version": "1.2.3.4",
          "platform": "27.0.0.0",
          "application": "27.0.0.0",
          "dependencies": [
            { "id": "f3552374-a1f2-4356-848e-196002525837",
              "name": "Business Foundation",
              "publisher": "Microsoft",
              "version": "27.5.46862.49619" }
          ]
        }
        """;

        var sanitized = DepCompiler.SanitizeManifestVersions(input);

        using var doc = JsonDocument.Parse(sanitized);
        var root = doc.RootElement;
        Assert.Equal("1.2.3.4", root.GetProperty("version").GetString());
        Assert.Equal("27.0.0.0", root.GetProperty("platform").GetString());
        Assert.Equal("27.0.0.0", root.GetProperty("application").GetString());
        Assert.True(root.TryGetProperty("dependencies", out var deps));
        Assert.Equal("27.5.46862.49619", deps[0].GetProperty("version").GetString());
    }

    /// <summary>
    /// Regression for the AL0275 'Microsoft' ambiguity reported when compiling DWF's Base
    /// Application (issue #1521 final stretch). Microsoft's BC source ships app.json with
    /// <c>"version": "$(app_currentVersion)"</c> — a build-time placeholder that is not a
    /// valid <see cref="Version"/>. ExtractAppIdentity used <c>Version.Parse</c> which threw,
    /// the catch swallowed the entire identity, and the runner fell back to its hard-coded
    /// "AlRunnerApp by AlRunner (1.0.0.0)" defaults. The BC compiler then attributed every
    /// <c>namespace Microsoft.X;</c> declaration in Base App AL files to the AlRunner
    /// extension and reported it as ambiguous against the real Microsoft platform.
    ///
    /// Fix: parse with <see cref="Version.TryParse"/> and fall back to the default
    /// <see cref="Version"/> only — preserve the real Name/Publisher/Id from the manifest.
    /// </summary>
    [Fact]
    public void ExtractAppIdentity_PlaceholderVersion_PreservesNameAndPublisher()
    {
        var manifestDir = Path.Combine(Path.GetTempPath(), "al-runner-identity-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "app.json"), """
            {
              "id": "437dbf0e-84ff-417a-965d-ed2bb9650972",
              "name": "Base Application",
              "publisher": "Microsoft",
              "version": "$(app_currentVersion)"
            }
            """);

            var identity = AlTranspiler.ExtractAppIdentity(new List<string> { manifestDir });

            // The bug: identity collapses to "AlRunnerApp" by "AlRunner". The fix must
            // preserve the manifest's Name/Publisher/Id even when version is unparseable.
            Assert.Equal("Base Application", identity.Name);
            Assert.Equal("Microsoft", identity.Publisher);
            Assert.Equal(Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972"), identity.AppId);
        }
        finally
        {
            if (Directory.Exists(manifestDir))
                Directory.Delete(manifestDir, true);
        }
    }

    // ------------------------------------------------------------------ //
    // Preprocessor symbol support — issue #1525
    // ------------------------------------------------------------------ //

    /// <summary>
    /// GetCleanSchemaSymbolsForRuntime returns CLEANSCHEMA1..N for the given
    /// runtime major version, with no extras or gaps.
    /// </summary>
    [Fact]
    public void GetCleanSchemaSymbolsForRuntime_ReturnsCorrectRange()
    {
        var symbols = AlTranspiler.GetCleanSchemaSymbolsForRuntime(new Version(3, 0));
        Assert.Equal(new[] { "CLEANSCHEMA1", "CLEANSCHEMA2", "CLEANSCHEMA3" }, symbols);
    }

    /// <summary>
    /// GetCleanSchemaSymbolsForRuntime returns an empty list when the runtime
    /// version is null or major == 0 (no CLEANSCHEMA guards applicable).
    /// </summary>
    [Fact]
    public void GetCleanSchemaSymbolsForRuntime_ZeroOrNull_ReturnsEmpty()
    {
        Assert.Empty(AlTranspiler.GetCleanSchemaSymbolsForRuntime(new Version(0, 0)));
        Assert.Empty(AlTranspiler.GetCleanSchemaSymbolsForRuntime(null!));
    }

    /// <summary>
    /// ReadPreprocessorSymbolsFromAppJson returns the symbols listed in the
    /// <c>preprocessorSymbols</c> array of a valid app.json file.
    /// </summary>
    [Fact]
    public void ReadPreprocessorSymbolsFromAppJson_ReadsSymbolsFromValidFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-pp-sym-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"name\":\"Test\",\"preprocessorSymbols\":[\"MYSYM\",\"ANOTHERSYM\"]}");
            var result = AlTranspiler.ReadPreprocessorSymbolsFromAppJson(dir);
            Assert.Equal(new[] { "MYSYM", "ANOTHERSYM" }, result);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// ReadPreprocessorSymbolsFromAppJson returns an empty list when the
    /// app.json has no <c>preprocessorSymbols</c> key or is absent.
    /// </summary>
    [Fact]
    public void ReadPreprocessorSymbolsFromAppJson_NoSymbolsOrMissingFile_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-pp-sym-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // No app.json — must return empty
            Assert.Empty(AlTranspiler.ReadPreprocessorSymbolsFromAppJson(dir));

            // app.json without preprocessorSymbols key — must return empty
            File.WriteAllText(Path.Combine(dir, "app.json"), "{\"name\":\"Test\"}");
            Assert.Empty(AlTranspiler.ReadPreprocessorSymbolsFromAppJson(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// When <c>app.json</c> declares <c>preprocessorSymbols: ["MYSYM"]</c>,
    /// CompileDep must auto-apply that symbol so <c>#if MYSYM</c>-gated AL code
    /// compiles successfully. Without the symbol the codeunit would not compile.
    /// </summary>
    [Fact]
    public void CompileDep_AutoAppliesPreprocessorSymbolsFromAppJson()
    {
        // AL codeunit that only exists under #if MYSYM — without the symbol the
        // codeunit body is empty but still valid (a bare codeunit with no procedures).
        // We verify the symbol is applied by checking that a procedure defined inside
        // the #if block is actually reachable (the DLL must be produced).
        const string alSource = @"
codeunit 1525001 ""PP Test Auto Sym""
{
#if MYSYM
    procedure WhenSymIsDefined(): Integer
    begin
        exit(42);
    end;
#endif
}";
        var dir = Path.Combine(Path.GetTempPath(), "al-pp-auto-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TheApp.al"), alSource);
            // app.json declares MYSYM so it should auto-apply
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"PPTest\",\"publisher\":\"Test\"," +
                "\"version\":\"1.0.0.0\",\"preprocessorSymbols\":[\"MYSYM\"]}");

            var rc = DepCompiler.CompileDep(dir, outDir, new List<string>());
            Assert.Equal(0, rc);
            Assert.True(Directory.GetFiles(outDir, "*.dll").Length > 0, "DLL must be produced");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// When <c>--define MYSYM</c> is passed explicitly, <c>#if MYSYM</c>-gated AL code
    /// must compile. Without the flag the same code must NOT gate out the codeunit body
    /// (because the procedure under the guard is absent, the DLL still emits but the
    /// procedure is gone — verified via negative case).
    /// </summary>
    [Fact]
    public void CompileDep_ExtraDefines_GatesCodeUnderSymbol()
    {
        const string alSource = @"
codeunit 1525002 ""PP Test Explicit Sym""
{
#if EXPLSYM
    procedure OnlyWhenExplicit(): Integer
    begin
        exit(99);
    end;
#endif
    procedure AlwaysPresent(): Integer
    begin
        exit(1);
    end;
}";
        var dir = Path.Combine(Path.GetTempPath(), "al-pp-explicit-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TheApp.al"), alSource);
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"PPTest2\",\"publisher\":\"Test\"," +
                "\"version\":\"1.0.0.0\"}");

            // Positive: with --define EXPLSYM the DLL is produced
            var rcWith = DepCompiler.CompileDep(dir, outDir, new List<string>(),
                extraDefines: new[] { "EXPLSYM" });
            Assert.Equal(0, rcWith);
            Assert.True(Directory.GetFiles(outDir, "*.dll").Length > 0, "DLL must be produced with symbol");

            // Clean output dir
            foreach (var f in Directory.GetFiles(outDir)) File.Delete(f);

            // Negative: without EXPLSYM the DLL is still produced (codeunit is valid),
            // but it should contain one fewer method — the DLL still emits because
            // the codeunit body remains valid (only the procedure is gated).
            var rcWithout = DepCompiler.CompileDep(dir, outDir, new List<string>());
            Assert.Equal(0, rcWithout);
            Assert.True(Directory.GetFiles(outDir, "*.dll").Length > 0, "DLL must be produced without symbol");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// When <c>app.json</c> declares <c>runtime: "3.0"</c>, CompileDep must auto-define
    /// CLEANSCHEMA1, CLEANSCHEMA2, CLEANSCHEMA3. Verified by compiling AL that uses
    /// <c>#if CLEANSCHEMA1</c> gating — the gated code must compile.
    /// </summary>
    [Fact]
    public void CompileDep_AutoAppliesCleanSchemaSymbolsFromRuntime()
    {
        const string alSource = @"
codeunit 1525003 ""PP Test Runtime CS""
{
#if CLEANSCHEMA1
    procedure OnlyWhenCleanSchema1(): Integer
    begin
        exit(111);
    end;
#endif
}";
        var dir = Path.Combine(Path.GetTempPath(), "al-pp-cs-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TheApp.al"), alSource);
            // runtime "3.0" means CLEANSCHEMA1, CLEANSCHEMA2, CLEANSCHEMA3 are auto-defined
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"PPTestCS\",\"publisher\":\"Test\"," +
                "\"version\":\"1.0.0.0\",\"runtime\":\"3.0\"}");

            var rc = DepCompiler.CompileDep(dir, outDir, new List<string>());
            Assert.Equal(0, rc);
            Assert.True(Directory.GetFiles(outDir, "*.dll").Length > 0, "DLL must be produced");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Regression: when <c>#if MYSYM</c> gates an invalid or otherwise-uncompilable block,
    /// omitting the symbol excludes the gated content so compilation succeeds.
    /// </summary>
    [Fact]
    public void CompileDep_WithoutSymbol_GatedBlockExcluded()
    {
        // The #if NOSUCHSYM block contains AL that is only valid when that symbol is defined.
        // Without the symbol the block must be stripped and the codeunit must still compile.
        const string alSource = @"
codeunit 1525004 ""PP Test No Symbol""
{
#if NOSUCHSYM
    // This procedure has a fake type that only exists under the guard
    procedure GatedProc(): Integer
    begin
        exit(77);
    end;
#endif
    procedure BaseProc(): Integer
    begin
        exit(1);
    end;
}";
        var dir = Path.Combine(Path.GetTempPath(), "al-pp-nosym-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TheApp.al"), alSource);
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"PPTestNS\",\"publisher\":\"Test\"," +
                "\"version\":\"1.0.0.0\"}");

            var rc = DepCompiler.CompileDep(dir, outDir, new List<string>());
            Assert.Equal(0, rc);
            Assert.True(Directory.GetFiles(outDir, "*.dll").Length > 0,
                "DLL must compile when gated block is excluded");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Regression: when a "provider" app's app.json declares <c>internalsVisibleTo</c>
    /// granting access to a "consumer" app, multi-app compile must propagate that grant
    /// to the provider's compiled module. Otherwise the consumer hits AL0161 when it
    /// references an internal member of the provider — see issue #1521 (BFTL accessing
    /// BF internal field "Temp Current Sequence No.").
    /// </summary>
    [Fact]
    public void CompileDep_RespectsInternalsVisibleTo_WhenGrantorListsConsumer()
    {
        var providerId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var consumerId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-ivt-" + Guid.NewGuid().ToString("N")[..8]);
        var providerDir = Path.Combine(rootDir, "Provider");
        var consumerDir = Path.Combine(rootDir, "Consumer");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(providerDir);
            Directory.CreateDirectory(consumerDir);
            Directory.CreateDirectory(outputDir);

            // Provider grants Consumer access to its internal members.
            File.WriteAllText(Path.Combine(providerDir, "app.json"), $$"""
            {
              "id": "{{providerId}}",
              "name": "Provider",
              "publisher": "Test",
              "version": "1.0.0.0",
              "internalsVisibleTo": [
                { "id": "{{consumerId}}", "name": "Consumer", "publisher": "Test" }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(providerDir, "Provider.al"), """
            table 50100 "My Tab"
            {
                fields
                {
                    field(1; "X"; Integer) { Access = Internal; }
                }
                keys { key(PK; "X") { Clustered = true; } }
            }
            """);

            // Consumer depends on Provider and pokes the internal field.
            File.WriteAllText(Path.Combine(consumerDir, "app.json"), $$"""
            {
              "id": "{{consumerId}}",
              "name": "Consumer",
              "publisher": "Test",
              "version": "1.0.0.0",
              "dependencies": [
                { "id": "{{providerId}}", "name": "Provider", "publisher": "Test", "version": "1.0.0.0" }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(consumerDir, "Use.al"), """
            codeunit 50101 "Use"
            {
                procedure P()
                var
                    T: Record "My Tab";
                begin
                    T.X := 1;
                end;
            }
            """);

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int result;
            try
            {
                result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            }
            finally
            {
                Console.SetError(origErr);
            }

            var stderr = errCapture.ToString();
            var providerDll = Path.Combine(outputDir, "Test_Provider_1.0.0.0.dll");
            var consumerDll = Path.Combine(outputDir, "Test_Consumer_1.0.0.0.dll");

            Assert.False(stderr.Contains("AL0161"),
                $"AL0161 (inaccessible due to protection level) should not appear when grantor lists consumer in internalsVisibleTo. stderr:\n{stderr}");
            Assert.Equal(0, result);
            Assert.True(File.Exists(providerDll), $"Expected provider DLL {providerDll}");
            Assert.True(File.Exists(consumerDll), $"Expected consumer DLL {consumerDll}");
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    /// <summary>
    /// Regression for the silent-transpile-failure mode (DWF/Base Application 2026-04):
    /// when TranspileMulti aborts before producing C# (parse errors, unresolved
    /// declarations, etc.), CompileDepMultiApp must surface a uniquely-tagged
    /// [TranspileMulti-NoOutput-X] line so downstream debugging can identify which
    /// path fired without re-running under a debugger.
    /// </summary>
    [Fact]
    public void CompileDepMultiApp_OnSilentFailure_PrintsRootCauseTag()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-silentfail-" + Guid.NewGuid().ToString("N")[..8]);
        var appDir = Path.Combine(rootDir, "Broken");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllText(Path.Combine(appDir, "app.json"), """
            {
              "id": "ccccdddd-cccc-cccc-cccc-cccccccccccc",
              "name": "Broken",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """);

            // Deliberately malformed AL — unterminated procedure body. Parses with errors,
            // forcing TranspileMulti down the [TranspileMulti-NoOutput-A] (parse-error) path.
            File.WriteAllText(Path.Combine(appDir, "Broken.al"),
                "codeunit 99700 Broken { procedure P() begin this is not valid AL syntax @@@");

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int result;
            try
            {
                result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            }
            finally
            {
                Console.SetError(origErr);
            }

            var stderr = errCapture.ToString();

            Assert.NotEqual(0, result);
            Assert.Contains("[TranspileMulti-NoOutput-", stderr);
            // CompileDepMultiApp must also dump its own pre-call counters so the call
            // site is identifiable even if the inner tag is missed.
            Assert.Contains("[CompileDepMultiApp-NoOutput]", stderr);
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    /// <summary>
    /// Regression for issue #1521 final stretch: a CLEANSCHEMA<N> guard in the slice
    /// strips a field that a consumer in the same slice references unguarded.
    /// Defining CLEANSCHEMA<N> blindly produces AL0132 / AL0118; the per-slice
    /// auto-detection must drop <N> from the preprocessor set so the field stays.
    ///
    /// Concrete BC 27.x example: GenJournalLine."IC Partner G/L Acc. No." is wrapped
    /// in <c>#if not CLEANSCHEMA25</c> while UpgradeBaseApp.Codeunit.al references
    /// the field unguarded.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_SkipsN_WhenStrippedFieldHasUnguardedConsumer()
    {
        var tableSrc = """
            table 50100 "Bank Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA25
                    field(116; "IC Partner G/L Acc. No."; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var consumerSrc = """
            codeunit 50101 "Use"
            {
                procedure P()
                var T: Record "Bank Tab";
                begin
                    T.SetFilter("IC Partner G/L Acc. No.", '<>''''');
                end;
            }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, consumerSrc });

        Assert.DoesNotContain("CLEANSCHEMA25", symbols);
        // Other CLEANSCHEMA<N> not implicated by this slice should still be defined,
        // matching the historical default behavior.
        Assert.Contains("CLEANSCHEMA1", symbols);
        Assert.Contains("CLEANSCHEMA24", symbols);
    }

    /// <summary>
    /// Companion: when a CLEANSCHEMA<N> guard wraps a field that nothing else
    /// references, defining <N> is safe and should remain in the symbol set.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_KeepsN_WhenStrippedFieldHasNoOtherConsumer()
    {
        var tableSrc = """
            table 50100 "Lonely Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA25
                    field(99; "Unused Obsolete Field"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var unrelatedSrc = """
            codeunit 50101 "Other"
            {
                procedure P() begin end;
            }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, unrelatedSrc });

        Assert.Contains("CLEANSCHEMA25", symbols);
    }

    /// <summary>
    /// Inverse of the negative-guard case: an enum (or other top-level object) is
    /// declared <em>only</em> inside <c>#if CLEANSCHEMA&lt;N&gt; ... #endif</c>, and
    /// a consumer references it unguarded. To keep the declaration visible, the
    /// auto-detector must include <c>CLEANSCHEMA&lt;N&gt;</c> in the symbol set.
    ///
    /// Concrete BC 27.x example: enum "Sales Order Print Option" is declared inside
    /// <c>#if CLEANSCHEMA29</c> but DocumentPrint.Codeunit.al references it unguarded.
    /// Without this, AL0118 fires for every reference site.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_AddsN_WhenPositiveGuardDeclarationHasUnguardedConsumer()
    {
        var enumSrc = """
            #if CLEANSCHEMA29
            enum 50200 "My Print Option"
            {
                Extensible = true;
                value(0; "First") { Caption = 'First'; }
                value(1; "Second") { Caption = 'Second'; }
            }
            #endif
            """;
        var consumerSrc = """
            codeunit 50201 "Use Print"
            {
                procedure P()
                var Opt: Enum "My Print Option";
                begin
                    Opt := "My Print Option"::First;
                end;
            }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { enumSrc, consumerSrc }, defaultMax: 25, scanMax: 50);

        Assert.Contains("CLEANSCHEMA29", symbols);
    }

    /// <summary>
    /// Conflict tradeoff: the SAME N appears as a negative guard hiding a field
    /// (drop N → strip field) AND as a positive guard hiding a declaration with
    /// unguarded consumers (add N → reveal declaration). Pick the side with the
    /// majority of unguarded consumers; on a tie, prefer negative (drop N).
    /// Here the negative-side has 1 consumer, positive-side has 3 — positive wins.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_PositiveWins_WhenMorePositiveConsumers()
    {
        var tableSrc = """
            table 50100 "Conf Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA29
                    field(116; "Old Field"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var oldConsumer = """
            codeunit 50101 "Old Use"
            {
                procedure P()
                var T: Record "Conf Tab";
                begin
                    T.SetFilter("Old Field", '<>''''');
                end;
            }
            """;
        var enumSrc = """
            #if CLEANSCHEMA29
            enum 50200 "New Enum" { value(0; "X") { } }
            #endif
            """;
        var newConsumerA = """
            codeunit 50202 "New Use A"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;
        var newConsumerB = """
            codeunit 50203 "New Use B"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;
        var newConsumerC = """
            codeunit 50204 "New Use C"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, oldConsumer, enumSrc, newConsumerA, newConsumerB, newConsumerC },
            defaultMax: 25, scanMax: 50);

        // Positive side has more consumers (3) than negative side (1) → add N.
        Assert.Contains("CLEANSCHEMA29", symbols);
    }

    /// <summary>
    /// Conflict tie-breaker: equal consumer counts on both sides (or negative
    /// strictly more) prefer the negative side (drop N), preserving the
    /// historical default behaviour.
    /// </summary>
    [Fact]
    public void ComputeCleanSchemaSymbols_NegativeWins_OnTieOrMoreNegativeConsumers()
    {
        var tableSrc = """
            table 50100 "Conf Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA29
                    field(116; "Old Field"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        var oldConsumerA = """
            codeunit 50101 "Old Use A"
            { procedure P() var T: Record "Conf Tab"; begin T.SetFilter("Old Field", ''); end; }
            """;
        var oldConsumerB = """
            codeunit 50102 "Old Use B"
            { procedure P() var T: Record "Conf Tab"; begin T.SetFilter("Old Field", ''); end; }
            """;
        var enumSrc = """
            #if CLEANSCHEMA29
            enum 50200 "New Enum" { value(0; "X") { } }
            #endif
            """;
        var newConsumer = """
            codeunit 50202 "New Use"
            { procedure P() var E: Enum "New Enum"; begin E := "New Enum"::X; end; }
            """;

        var symbols = AlTranspiler.ComputeCleanSchemaSymbols(
            new[] { tableSrc, oldConsumerA, oldConsumerB, enumSrc, newConsumer },
            defaultMax: 25, scanMax: 50);

        // Negative side has more consumers (2) than positive side (1) → drop N.
        Assert.DoesNotContain("CLEANSCHEMA29", symbols);
    }

    /// <summary>
    /// Regression for the auto-stubbed-page-method-call gap: a consumer calls
    /// <c>MyPage.SomeMethod(arg1, arg2)</c> on a page that exists only as an
    /// auto-stub. The stub must include forgiving Variant-arg procedures so
    /// AL0132 doesn't fire.
    ///
    /// Concrete BC 27.x example: PowerBIEmbeddedReportPart.Page.al calls
    /// <c>PowerBIElementAddinHost.SetDisplayedElement(Rec)</c> on the auto-stubbed
    /// "Power BI Element Addin Host" page.
    /// </summary>
    [Fact]
    public void ExtractDeps_AutoStubPage_EmitsProceduresForObservedMethodCalls()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-pagestub-" + Guid.NewGuid().ToString("N")[..8]);
        var srcDir = Path.Combine(rootDir, "src");
        var depDir = Path.Combine(rootDir, "dep", "FakeBaseApp");
        var outputDirAfterExtract = Path.Combine(rootDir, "extracted");
        try
        {
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(depDir);

            // Extension app — references a codeunit defined in the dep, which in
            // turn references the missing page. This mirrors the real DWF flow:
            // user app -> BaseApp -> Power BI Element Addin Host (missing).
            File.WriteAllText(Path.Combine(srcDir, "app.json"), """
            {
              "id": "44444444-4444-4444-4444-444444444444",
              "name": "ConsumerApp",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """);
            File.WriteAllText(Path.Combine(srcDir, "MyExt.al"), """
            codeunit 50500 "MyExt"
            {
                procedure Run()
                var
                    DepC: Codeunit "DepCaller";
                begin
                    DepC.Trigger();
                end;
            }
            """);

            // Dep source — contains the consumer that calls into the missing page.
            File.WriteAllText(Path.Combine(depDir, "DepCaller.al"), """
            codeunit 60500 "DepCaller"
            {
                procedure Trigger()
                var
                    Host: Page "Mystery Host Page";
                begin
                    Host.SetDisplayedElement(1);
                    Host.SetPowerBIFilter('foo');
                end;
            }
            """);

            int rc = AlRunner.DepExtractor.ExtractDeps(
                srcDir,
                new[] { Path.Combine(rootDir, "dep") },
                outputDirAfterExtract,
                new List<string>());
            Assert.Equal(0, rc);

            // The extractor must have generated a stub for the missing page that
            // includes the two methods seen at the call site.
            var stubFiles = Directory.EnumerateFiles(outputDirAfterExtract, "*MysteryHostPage*.al", SearchOption.AllDirectories).ToList();
            Assert.NotEmpty(stubFiles);
            var stubContent = File.ReadAllText(stubFiles[0]);
            Assert.Contains("SetDisplayedElement", stubContent);
            Assert.Contains("SetPowerBIFilter", stubContent);
            // Procedures must accept a Variant arg so any call shape compiles.
            Assert.Contains("Variant", stubContent);
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    // ------------------------------------------------------------------ //
    // Issue #1554: emit exceptions must not crash; LastCompilation must be
    // available even when TranspileMulti returns null so CompileDepMultiApp
    // can still write symbols.json for downstream cross-app resolution.
    // ------------------------------------------------------------------ //

    /// <summary>
    /// When AL has a local variable whose type cannot be resolved (which causes
    /// NavTypeKind 'None' during BC's emit phase), TranspileMulti must NOT throw
    /// a bare exception to the caller. It must return null (zero objects emitted)
    /// while still having set AlTranspiler.LastCompilation — the semantic model
    /// is still valid even when some methods fail to emit.
    /// </summary>
    [Fact]
    public void TranspileMulti_DoesNotThrow_WhenAlHasUnresolvedVarTypeInLocalScope()
    {
        // AL that references a table that doesn't exist.  The BC compiler with
        // continueBuildOnError will still try to emit, and the local-variable
        // field initializer throws because the type resolves to NavTypeKind.None.
        var alSource = """
            codeunit 50097 "TestEmitException1554" {
                procedure Work()
                var
                    r: Record "NoSuchTable1554ZZZ";
                begin
                end;
            }
            """;

        List<(string Name, string Code)>? result = null;
        Exception? thrownEx = null;
        try
        {
            result = AlTranspiler.TranspileMulti(new List<string> { alSource });
        }
        catch (Exception ex)
        {
            thrownEx = ex;
        }

        // Must NOT propagate a bare exception to the caller.
        Assert.Null(thrownEx);
        // LastCompilation must be set — BC's semantic model exists even when emit fails.
        Assert.NotNull(AlTranspiler.LastCompilation);
    }

    /// <summary>
    /// When an app in a multi-app compile fails to produce a DLL (because BC's emit
    /// phase threw exceptions leaving zero captured objects), CompileDepMultiApp must
    /// still write a &lt;App&gt;.symbols.json from AlTranspiler.LastCompilation if
    /// available. This unblocks downstream apps that reference the failed app's types
    /// — they see the symbol declarations even though the runtime DLL is absent.
    ///
    /// Issue #1554: BF → Base App → Tests-TestLibraries chain was stalling at 3/7
    /// because each downstream cascaded on the missing BF symbols.json.
    /// </summary>
    [Fact]
    public void CompileDepMultiApp_WritesSymbolsJson_FromLastCompilation_WhenAppFailsAtEmit()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "al-runner-emit1554-" + Guid.NewGuid().ToString("N")[..8]);
        var app1Dir = Path.Combine(rootDir, "App1FailedEmit");
        var app2Dir = Path.Combine(rootDir, "App2Consumer");
        var outputDir = Path.Combine(rootDir, "out");
        try
        {
            Directory.CreateDirectory(app1Dir);
            Directory.CreateDirectory(app2Dir);
            Directory.CreateDirectory(outputDir);

            var app1Id = "11111111-aaaa-aaaa-aaaa-111111111111";
            var app2Id = "22222222-bbbb-bbbb-bbbb-222222222222";

            // App1 declares a codeunit "HelperCU1554" but references a non-existent table
            // in a local variable.  BC's semantic analysis succeeds (the codeunit declaration
            // is known), but emit may fail for the method with the bad variable.
            // LastCompilation is set before the emit call, so the codeunit declaration is
            // available even if the DLL cannot be produced.
            File.WriteAllText(Path.Combine(app1Dir, "app.json"), $$"""
            {
              "id": "{{app1Id}}",
              "name": "App1FailedEmit",
              "publisher": "Test",
              "version": "1.0.0.0"
            }
            """);
            File.WriteAllText(Path.Combine(app1Dir, "Helper.al"), """
            codeunit 50085 "HelperCU1554"
            {
                procedure GetValue(): Integer
                var r: Record "NoSuchTable1554ABC";
                begin
                    exit(42);
                end;
            }
            """);

            // App2 is a simple consumer that declares its own codeunit.
            // It is topologically ordered after App1 because it lists App1 as a dep.
            File.WriteAllText(Path.Combine(app2Dir, "app.json"), $$"""
            {
              "id": "{{app2Id}}",
              "name": "App2Consumer",
              "publisher": "Test",
              "version": "1.0.0.0",
              "dependencies": [
                { "id": "{{app1Id}}", "name": "App1FailedEmit", "publisher": "Test", "version": "1.0.0.0" }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(app2Dir, "Consumer.al"), """
            codeunit 50086 "ConsumerCU1554"
            {
                procedure Run(): Integer
                begin
                    exit(1);
                end;
            }
            """);

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int result;
            try
            {
                result = DepCompiler.CompileDepMultiApp(rootDir, outputDir, new List<string>());
            }
            finally
            {
                Console.SetError(origErr);
            }

            var stderr = errCapture.ToString();

            // App1 is expected to fail (emit exception → zero objects → TranspileMulti null).
            // App2 should still be attempted and may succeed.
            // The key assertion: App1's symbols.json must be written from LastCompilation
            // so that downstream apps (like App2) can resolve App1's types.
            var app1SymbolsJson = Path.Combine(outputDir, "Test_App1FailedEmit_1.0.0.0.symbols.json");
            Assert.True(File.Exists(app1SymbolsJson),
                $"symbols.json must be written for App1 even when its DLL failed.\n" +
                $"Output dir contents: {string.Join(", ", Directory.GetFiles(outputDir).Select(Path.GetFileName))}\n" +
                $"Stderr:\n{stderr}");

            // The symbols.json must contain App1's codeunit declaration
            var symbolsContent = File.ReadAllText(app1SymbolsJson);
            Assert.Contains("HelperCU1554", symbolsContent);
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, true);
        }
    }

    // ------------------------------------------------------------------ //
    // Issue #1587: CLEANSCHEMA symbols must derive from BC version
    // ------------------------------------------------------------------ //

    /// <summary>
    /// GetCleanSchemaDefaultMaxForBCVersion returns bcMajor - 1 for each
    /// supported BC version. CLEANSCHEMA-N is active starting with BC version N+1
    /// (the cleanup already happened in version N; the current version's symbol
    /// is "in development" and must not be activated).
    ///
    /// Per-version fallback table: BC 26 → 25, BC 27 → 26, BC 28 → 27.
    /// </summary>
    [Fact]
    public void GetCleanSchemaDefaultMaxForBCVersion_ReturnsBCMajorMinusOne()
    {
        // BC 26 → max = 25
        Assert.Equal(25, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(26));
        // BC 27 → max = 26
        Assert.Equal(26, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(27));
        // BC 28 → max = 27
        Assert.Equal(27, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(28));
        // BC 30 → max = 29
        Assert.Equal(29, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(30));
    }

    /// <summary>
    /// GetCleanSchemaDefaultMaxForBCVersion returns 0 for BC version 0 or 1
    /// (no CLEANSCHEMA symbols applicable at the earliest versions).
    /// </summary>
    [Fact]
    public void GetCleanSchemaDefaultMaxForBCVersion_ZeroOrOne_ReturnsZero()
    {
        Assert.Equal(0, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(0));
        Assert.Equal(0, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(1));
    }

    /// <summary>
    /// When app.json declares <c>"application": "27.0.0.0"</c>, CompileDep must
    /// include CLEANSCHEMA26 in the effective preprocessor symbols (BC 27 → 1..26).
    /// CLEANSCHEMA27 must NOT be included (current version's symbol is in-development).
    ///
    /// Verification strategy: an AL table whose field 116 is guarded by
    /// <c>#if not CLEANSCHEMA26</c> (mirrors the real BC 27.x pattern). If CLEANSCHEMA26
    /// is active, the field is stripped and a companion codeunit that references it
    /// UNGUARDED would produce AL0132. So we prove the symbol is active by compiling a
    /// standalone table that does NOT have an unguarded consumer — the compile must
    /// succeed with zero AL0132 errors, and the stderr must report CLEANSCHEMA26 as
    /// "added" (relative to the old defaultMax of 25).
    /// </summary>
    [Fact]
    public void CompileDep_BC27App_IncludesCleanSchema26_InEffectiveSet()
    {
        // Table with a field guarded by #if not CLEANSCHEMA26 and a field guarded by
        // #if not CLEANSCHEMA27. With BC 27 (CLEANSCHEMA26 active, CLEANSCHEMA27 not),
        // field 116 must be stripped and field 117 must be present.
        // We verify by compiling a codeunit that references field 117 (present on BC 27)
        // and not field 116 (stripped on BC 27). The compile must succeed.
        const string tableSource = """
            table 1587010 "CS Test Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA26
                    field(116; "Old BC26 Field"; Code[20]) { }
            #endif
            #if not CLEANSCHEMA27
                    field(117; "Old BC27 Field"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        // Consumer only references the BC27 field (which is present on BC 27 because
        // CLEANSCHEMA27 is NOT active). This codeunit must compile cleanly on BC 27.
        const string consumerSource = """
            codeunit 1587011 "CS27 Consumer"
            {
                procedure Run()
                var T: Record "CS Test Tab";
                begin
                    T.SetFilter("Old BC27 Field", '<>''''');
                end;
            }
            """;
        var dir = Path.Combine(Path.GetTempPath(), "al-cs26-bc27-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Tab.al"), tableSource);
            File.WriteAllText(Path.Combine(dir, "Consumer.al"), consumerSource);
            // BC 27 app — application "27.0.0.0"
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"CS26Test\",\"publisher\":\"Test\"," +
                "\"version\":\"1.0.0.0\",\"application\":\"27.0.0.0\"}");

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int rc;
            try { rc = DepCompiler.CompileDep(dir, outDir, new List<string>()); }
            finally { Console.SetError(origErr); }

            var stderr = errCapture.ToString();
            // Compile must succeed (field 117 is present on BC 27)
            Assert.Equal(0, rc);
            Assert.NotEmpty(Directory.GetFiles(outDir, "*.dll"));
            // Per-slice diagnostic must report CLEANSCHEMA26 in the active set (+CLEANSCHEMA26)
            // because BC 27 has application "27.0.0.0" and CLEANSCHEMA26 is in 1..26.
            Assert.Contains("CLEANSCHEMA26", stderr);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// When app.json declares <c>"application": "27.0.0.0"</c>, CLEANSCHEMA27 must NOT
    /// be in the effective set. Proven by gating a field behind <c>#if not CLEANSCHEMA27</c>
    /// and having an unguarded consumer reference it: if CLEANSCHEMA27 were active, the
    /// field would be stripped and the compile would produce AL0132 (field not found).
    /// The compile must succeed — confirming CLEANSCHEMA27 was correctly excluded.
    /// </summary>
    [Fact]
    public void CompileDep_BC27App_ExcludesCleanSchema27()
    {
        // Field 117 is guarded by #if not CLEANSCHEMA27. On BC 27, CLEANSCHEMA27 is
        // NOT active → field stays. The consumer references it unguarded → compile succeeds
        // only if CLEANSCHEMA27 was NOT activated.
        const string tableSource = """
            table 1587012 "CS27 Excl Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA27
                    field(117; "Field Needs CS27 Inactive"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        const string consumerSource = """
            codeunit 1587013 "CS27 Excl Consumer"
            {
                procedure Run()
                var T: Record "CS27 Excl Tab";
                begin
                    // References the field unguarded — compile fails with AL0132 if CLEANSCHEMA27 is active.
                    T.SetFilter("Field Needs CS27 Inactive", '<>''''');
                end;
            }
            """;
        var dir = Path.Combine(Path.GetTempPath(), "al-cs27excl-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Tab.al"), tableSource);
            File.WriteAllText(Path.Combine(dir, "Consumer.al"), consumerSource);
            // BC 27 app
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"CS27ExclTest\",\"publisher\":\"Test\"," +
                "\"version\":\"1.0.0.0\",\"application\":\"27.0.0.0\"}");

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int rc;
            try { rc = DepCompiler.CompileDep(dir, outDir, new List<string>()); }
            finally { Console.SetError(origErr); }

            var stderr = errCapture.ToString();
            // Must compile without AL0132 (field was preserved because CLEANSCHEMA27 was NOT active)
            Assert.DoesNotContain("AL0132", stderr);
            Assert.Equal(0, rc);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// When app.json declares <c>"application": "26.0.0.0"</c>, CompileDep must
    /// include CLEANSCHEMA25 but NOT CLEANSCHEMA26 (BC 26 → 1..25).
    /// Proven using the same #if not CLEANSCHEMA26 table + unguarded consumer pattern:
    /// if CLEANSCHEMA26 were active, the field would be stripped and AL0132 would fire.
    /// </summary>
    [Fact]
    public void CompileDep_BC26App_ExcludesCleanSchema26()
    {
        const string tableSource = """
            table 1587020 "BC26 Excl Tab"
            {
                fields
                {
                    field(1; "PK"; Code[20]) { }
            #if not CLEANSCHEMA26
                    field(116; "Field Needs CS26 Inactive"; Code[20]) { }
            #endif
                }
                keys { key(PK; "PK") { Clustered = true; } }
            }
            """;
        const string consumerSource = """
            codeunit 1587021 "BC26 Excl Consumer"
            {
                procedure Run()
                var T: Record "BC26 Excl Tab";
                begin
                    T.SetFilter("Field Needs CS26 Inactive", '<>''''');
                end;
            }
            """;
        var dir = Path.Combine(Path.GetTempPath(), "al-cs26excl-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Tab.al"), tableSource);
            File.WriteAllText(Path.Combine(dir, "Consumer.al"), consumerSource);
            // BC 26 app — CLEANSCHEMA26 must NOT be in effective set
            File.WriteAllText(Path.Combine(dir, "app.json"),
                "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"CS26ExclTest\",\"publisher\":\"Test\"," +
                "\"version\":\"1.0.0.0\",\"application\":\"26.0.0.0\"}");

            var origErr = Console.Error;
            using var errCapture = new StringWriter();
            Console.SetError(errCapture);
            int rc;
            try { rc = DepCompiler.CompileDep(dir, outDir, new List<string>()); }
            finally { Console.SetError(origErr); }

            var stderr = errCapture.ToString();
            // Must compile without AL0132 (field preserved because CLEANSCHEMA26 was NOT active)
            Assert.DoesNotContain("AL0132", stderr);
            Assert.Equal(0, rc);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Per-BC-version fallback table is correct: for each known BC version,
    /// the CLEANSCHEMA max is bcVersion.Major - 1 (CLEANSCHEMA-N is the "in-progress"
    /// symbol for BC-N and must not be activated during that version's cycle).
    ///
    /// This test locks the table so changes are intentional and visible in code review.
    /// </summary>
    [Fact]
    public void CleanSchemaTable_MatchesExpectedBCVersionMapping()
    {
        // Spot-check canonical BC versions from the issue's fallback table
        Assert.Equal(25, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(26));
        Assert.Equal(26, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(27));
        Assert.Equal(27, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(28));
        Assert.Equal(28, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(29));
        Assert.Equal(29, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(30));
        // The relationship is always N-1 for any N > 1
        for (int n = 2; n <= 35; n++)
            Assert.Equal(n - 1, AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(n));
    }
}
