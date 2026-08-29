// Issue #2152 — the AL-diagnostic compile-failure guard #2150/#2154 added only
// covered the default bundled compile path. `--precompile` (RunPrecompile, the
// dependency-app-to-DLL subcommand) has the identical BC ContinueBuildOnError gap:
// `emitted` (emitOut.Sources) can come back non-empty — a broken query column's
// metadata still emits — at the same time emitOut.Diagnostics also carries the
// AL0353 BC reported for it. Before this fix, --precompile only checked diagnostics
// when EMIT-ZERO (0 sources) fired, so it could silently write out a DLL for AL a
// real service tier would refuse to publish. That matters more here than it looks:
// --precompile's whole point is producing a dependency another bundle run treats as
// precompiled and trusted (precompiled-dll-respect.md) — a silently-accepted compile
// error here poisons every later bundle that depends on the resulting DLL.
//
// --precompile takes a real .app package (a NAVX-prefixed or plain ZIP with
// NavxManifest.xml + src/*.al — see AppLoader.ReadManifest/ExtractAl), not a source
// directory, so this builds one from scratch the same way
// AlRunner.Tests/DependencyResolverTests.cs's WriteApp helper does.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompileAlDiagnosticFailureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunPrecompile(string inputApp, string outputDll)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(" --precompile \"").Append(inputApp).Append('"');
        args.Append(" --out \"").Append(outputDll).Append('"');
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
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    // Minimal .app: a plain ZIP (no NAVX header — AppLoader falls back to offset 0
    // when the 8-byte magic doesn't match) containing NavxManifest.xml plus src/*.al
    // for every (name, source) pair given. No Application/Platform attributes and no
    // <Dependencies/> — the fixture below references only its own table, so it needs
    // no Microsoft first-party roots resolved.
    //
    // Version/Application/Platform are all pinned to the SAME value —
    // AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion() — the exact 4-part BC
    // build this test binary was compiled against and provisioned for (same value
    // TestBuildConfig.BcVersionArg pins the OTHER two follow-up tests to). Two
    // deliberate reasons, not one:
    //   1. --precompile's own SelectVersion(manifest.Version, null) call resolves the
    //      BC artifact directory FROM the app's own manifest version — an arbitrary
    //      "1.0.0.0" falls through to the lazy "latest in cache" default instead,
    //      which is non-deterministic across machines/CI legs.
    //   2. Matching Application/Platform gives AppLoader.ImplicitRoots something real
    //      to resolve (Microsoft/Application + Microsoft/System), so
    //      BcCompiler.SetPackageCacheFallback's "enumerate every .app in the package
    //      cache dirs" fallback — reserved for apps with NEITHER dependencies NOR
    //      Application/Platform attributes, a shape no real modern .app actually
    //      ships — never engages. That fallback surfaced a genuine but unrelated
    //      runner false-positive during this test's own development (AL1022 for an
    //      unrelated PEPPOL package version mismatch, on an app that never
    //      references PEPPOL) — real modern .app packages always carry
    //      Application/Platform (AppLoader.ImplicitRoots' own doc comment), so this
    //      fixture is deliberately shaped like one instead of exercising that corner.
    private static string WriteAppPackage(string dir, string appId, string name, string publisher,
        params (string FileName, string Source)[] alSources)
    {
        var bcVersion = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()?.ToString()
            ?? throw new InvalidOperationException(
                "EngineBuiltVersion() is null — this test binary's BcEngineVersion " +
                "AssemblyMetadata is missing; rebuild AlRunner before running this test.");
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{bcVersion}"
                   Application="{bcVersion}" Platform="{bcVersion}"/>
              <Dependencies/>
            </Package>
            """;

        Directory.CreateDirectory(dir);
        var appPath = Path.Combine(dir, name + ".app");
        using (var fs = new FileStream(appPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("NavxManifest.xml");
            using (var es = manifestEntry.Open())
                es.Write(Encoding.UTF8.GetBytes(xml));

            foreach (var (fileName, source) in alSources)
            {
                var entry = zip.CreateEntry("src/" + fileName);
                using var es = entry.Open();
                es.Write(Encoding.UTF8.GetBytes(source));
            }
        }
        return appPath;
    }

    [SkippableFact]
    public void Precompile_ColumnDeclaresDataSourceAndMethodCount_FailsWithAl0353()
    {
        TestArtifacts.SkipIfMissing();

        var dir = Path.Combine(Path.GetTempPath(), "al-runner-precompile-al0353-bad", Guid.NewGuid().ToString("N"));
        var appPath = WriteAppPackage(dir, "f6666666-6666-6666-6666-666666666666",
            "PrecompileAl0353Bad", "Repro2152",
            ("Order.Table.al", """
                table 62230 "AL0353 Pre Order"
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                        field(2; Amount; Decimal) { }
                    }
                    keys
                    {
                        key(PK; "No.") { Clustered = true; }
                    }
                }
                """),
            ("OrderSum.Query.al", """
                query 62231 "AL0353 Pre Order Sum"
                {
                    QueryType = Normal;

                    elements
                    {
                        dataitem(Order; "AL0353 Pre Order")
                        {
                            column(TheAmount; Amount) { }
                            column(CountAmount; Amount) { Method = Count; }
                        }
                    }
                }
                """));
        var outputDll = Path.Combine(dir, "PrecompileAl0353Bad.dll");

        var (output, exitCode) = RunPrecompile(appPath, outputDll);

        // Would still pass if --precompile always wrote a DLL regardless of BC
        // diagnostics — assert the SPECIFIC diagnostic, and that no DLL was written.
        Assert.NotEqual(0, exitCode);
        Assert.Contains("AL0353", output);
        Assert.Contains("A Column must have a valid data source or have the 'Method' property set to 'Count'", output);
        Assert.False(File.Exists(outputDll), "a DLL should not be written for AL BC's own compiler rejected");
    }

    [SkippableFact]
    public void Precompile_ColumnMethodCountWithNoDataSource_WritesDll()
    {
        TestArtifacts.SkipIfMissing();

        // The corrected form real BC accepts — proves the precompile gate does not
        // also reject valid AL. Required alongside the negative test above: a guard
        // that always failed would pass that test too.
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-precompile-al0353-good", Guid.NewGuid().ToString("N"));
        var appPath = WriteAppPackage(dir, "f7777777-7777-7777-7777-777777777777",
            "PrecompileAl0353Good", "Repro2152",
            ("Order.Table.al", """
                table 62232 "AL0353 Pre Order Good"
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                        field(2; Amount; Decimal) { }
                    }
                    keys
                    {
                        key(PK; "No.") { Clustered = true; }
                    }
                }
                """),
            ("OrderSum.Query.al", """
                query 62233 "AL0353 Pre Order Sum Good"
                {
                    QueryType = Normal;

                    elements
                    {
                        dataitem(Order; "AL0353 Pre Order Good")
                        {
                            column(TheAmount; Amount) { }
                            column(CountAmount) { Method = Count; }
                        }
                    }
                }
                """));
        var outputDll = Path.Combine(dir, "PrecompileAl0353Good.dll");

        var (output, exitCode) = RunPrecompile(appPath, outputDll);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("AL0353", output);
        Assert.True(File.Exists(outputDll));
    }
}
