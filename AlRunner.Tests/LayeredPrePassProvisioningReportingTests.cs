using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2956 — the LAYERED pre-pass's own dependency resolve happens inside the emit
/// <c>try</c>, whose <c>catch (Exception ex)</c> rethrew every cause as a plain
/// <c>InvalidOperationException</c>. A <see cref="AlRunner.Infrastructure.MissingDependencyException"/>
/// therefore reached Program.cs only as an <c>InnerException</c>, so the
/// <c>catch (… is IDependencyProvisioningDiagnostic)</c> clause #2095 added never matched
/// and the reader got the short one-liner under a "COMPILE-FAIL" label instead:
/// <code>
///   &lt;layered-deps&gt;: COMPILE-FAIL — [layered] Failed to emit symbols for impl
///     'LSC Chain Middle' from …: Dependency not found: AL Runner/LSC Chain Base v1.0.0.0
/// </code>
/// exit 3, mislabelled as "your AL code did not compile" when it is a provisioning gap.
///
/// The sibling <c>BuildSiblingSourceDeps</c> path already reported this correctly
/// (SiblingSourceDepProvisioningReportingTests), because its resolve throws OUTSIDE the
/// emit try. These tests are the layered mirror, and they pin the DETAILED text rather
/// than the substring the pre-existing LayeredSourceChainTests assert — "Dependency not
/// found" and the app name appear in BOTH the wrapped and unwrapped forms, so neither
/// can tell the two apart.
///
/// Four tests:
///   - CLI positive: the #2095 provisioning-gap report, exit 2, no COMPILE-FAIL.
///   - CLI composition: the impl whose closure failed is still named — the one thing
///     the wrapper carried that ToDetailedMessage does not.
///   - Server positive: the same request over --server reports the detailed text too.
///     Before this fix, CLI and server mode disagreed about this surface.
///   - Negative: a genuine non-provisioning failure on the SAME call site still reports
///     COMPILE-FAIL + exit 3, proving the special case did not swallow the general one.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (visibly) when absent.
/// </summary>
public class LayeredPrePassProvisioningReportingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string NewScratch(string tag) =>
        TestScratch.Dir(Path.Combine("al-runner-layered-provisioning", tag));

    private static string ServerReq(params string[] bundles) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = bundles,
        packagePaths = Array.Empty<string>(),
    });

    private static (string Output, int Exit) RunRunner(string scratchRoot, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append($" \"{b}\"");
        args.Append($" --cache \"{Path.Combine(scratchRoot, "al-out")}\"");
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    // ── Fixtures: middle -> base, with base genuinely absent everywhere ─────────
    // Deliberately NOT the LayeredSourceChainTests GUIDs/ids: those tests assert the old
    // wrapped wording is gone, and sharing a workspace-deps cache key between the two
    // classes would let one answer for the other.

    private static void WriteMiddleApp(string dir, Guid middleId, Guid baseId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{middleId}}",
          "name": "LPP Chain Middle",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{baseId}}", "name": "LPP Chain Base", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60860, "to": 60869 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "ChainMiddleApi.Codeunit.al"), """
        codeunit 60860 "LPP Chain Middle Api"
        {
            procedure ReadValue(RowCode: Code[20]): Integer
            var
                ChainRow: Record "LPP Chain Row";
            begin
                ChainRow.Get(RowCode);
                exit(ChainRow."Row Value");
            end;
        }
        """);
    }

    private static void WriteTestApp(string dir, Guid testsId, Guid middleId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "LPP Chain Test",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{middleId}}", "name": "LPP Chain Middle", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60870, "to": 60879 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "ChainTests.al"), """
        codeunit 60870 "LPP Chain Tests"
        {
            Subtype = Test;

            [Test]
            procedure NeverReached()
            var
                MiddleApi: Codeunit "LPP Chain Middle Api";
            begin
                if MiddleApi.ReadValue('X') <> 0 then
                    Error('should never compile far enough to run this');
            end;
        }
        """);
    }

    /// <summary>
    /// An impl that resolves fine but fails the emit for a REAL compile reason. Two
    /// codeunits share object id 60881, so BC raises AL0264 and the symbol emit throws
    /// from inside the very <c>try</c> whose <c>catch</c> #2956 changes — verified by
    /// hand against the built runner, because most AL errors (AL0118, an out-of-range id)
    /// surface further down the pipeline instead and would have made this control
    /// vacuous. The negative control: this must keep the wrapped COMPILE-FAIL wording and
    /// exit 3.
    /// </summary>
    private static void WriteBadMiddleApp(string dir, Guid middleId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{middleId}}",
          "name": "LPP Bad Middle",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60880, "to": 60889 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Api.Codeunit.al"), """
        codeunit 60881 "LPP Bad Middle Api"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);
        // Same id as above — AL0264, raised during the impl's symbol emit.
        File.WriteAllText(Path.Combine(dir, "Dup.Codeunit.al"), """
        codeunit 60881 "LPP Bad Middle Dup"
        {
            procedure Other(): Integer
            begin
                exit(7);
            end;
        }
        """);
    }

    private static void WriteTestAppFor(string dir, Guid testsId, Guid middleId, string middleName)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "LPP Bad Test",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{middleId}}", "name": "{{middleName}}", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60890, "to": 60899 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
        codeunit 60890 "LPP Bad Tests"
        {
            Subtype = Test;

            [Test]
            procedure NeverReached()
            var
                Api: Codeunit "LPP Bad Middle Api";
            begin
                if Api.Answer() <> 42 then
                    Error('unreachable');
            end;
        }
        """);
    }

    // ── CLI: the #2095 report must reach the reader ─────────────────────────────

    /// <summary>
    /// The claim #2956 is about. With the base app genuinely absent, the layered pre-pass
    /// fails — and what surfaces must be the detailed provisioning-gap report, NOT the
    /// wrapped one-liner under a COMPILE-FAIL label.
    ///
    /// Every assertion here fails against the pre-fix binary: the wrapped form contains
    /// "COMPILE-FAIL", lacks every line of ToDetailedMessage, and exits 3 not 2.
    /// </summary>
    [SkippableFact]
    public void LayeredPrePass_BaseAppAbsent_ReportsProvisioningGap_NotCompileFail()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("absent-cli");
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), testsId = Guid.NewGuid();
        var chainRoot = Path.Combine(scratch, "chain");
        var middleDir = Path.Combine(chainRoot, "middle-app");
        var testsDir = Path.Combine(chainRoot, "tests-app");
        WriteMiddleApp(middleDir, middleId, baseId);
        WriteTestApp(testsDir, testsId, middleId);

        var (output, exit) = RunRunner(scratch, middleDir, testsDir);

        // NOT mislabelled as a compile failure, and not an unhandled crash.
        Assert.DoesNotContain("COMPILE-FAIL", output);
        Assert.DoesNotContain("Unhandled exception", output);
        // The detailed #2095 report, line by line — none of these appear in the wrapper.
        Assert.Contains("A required dependency package is missing from your package cache.", output);
        Assert.Contains("This is a PROVISIONING gap — your code is NOT the problem.", output);
        Assert.Contains("Missing: AL Runner/LPP Chain Base v1.0.0.0", output);
        Assert.Contains($"App ID:  {baseId}", output);
        Assert.Contains("Resolve it:", output);
        // Third-party branch of ToDetailedMessage: the runner cannot download this, so the
        // advice must be --package-cache, never "al-runner provision".
        Assert.Contains("Add the missing package to your --package-cache <dir>", output);
        Assert.DoesNotContain("al-runner provision", output);
        // Exit 2 ("execution error"), the code every other provisioning gap returns —
        // not 3 ("compilation error").
        Assert.Equal(2, exit);
    }

    /// <summary>
    /// Composition, not plain unwrapping: <c>ToDetailedMessage</c> knows the missing
    /// dependency but not which impl asked for it, and in a multi-impl invocation that is
    /// information the reader needs. The wrapper carried it; the fix must keep it.
    /// </summary>
    [SkippableFact]
    public void LayeredPrePass_BaseAppAbsent_ProvisioningGapStillNamesTheImplBeingBuilt()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("absent-impl-named");
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), testsId = Guid.NewGuid();
        var chainRoot = Path.Combine(scratch, "chain");
        var middleDir = Path.Combine(chainRoot, "middle-app");
        var testsDir = Path.Combine(chainRoot, "tests-app");
        WriteMiddleApp(middleDir, middleId, baseId);
        WriteTestApp(testsDir, testsId, middleId);

        var (output, exit) = RunRunner(scratch, middleDir, testsDir);

        Assert.Equal(2, exit);
        // Which impl, and where its source is — both concrete, neither in ToDetailedMessage.
        Assert.Contains("Reached while building source dependency 'LPP Chain Middle' (layered pre-pass).", output);
        Assert.Contains($"Its source: {middleDir}", output);
        // And the composed report still carries the inner diagnostic in full.
        Assert.Contains("This is a PROVISIONING gap — your code is NOT the problem.", output);
    }

    // ── Server mode: the same request must report the same way ──────────────────

    /// <summary>
    /// #2956 flagged that CLI and server mode disagreed about where this failure surfaces:
    /// server mode had no <c>IDependencyProvisioningDiagnostic</c> special case at all and
    /// flattened everything into "LAYERED-PREPASS-FAIL: {ex.Message}". A caller driving the
    /// runner over the server protocol got strictly less than a CLI caller, for the same
    /// gap on the same fixture.
    /// </summary>
    [SkippableFact]
    public async Task ServerRunTests_BaseAppAbsent_ReportsProvisioningGap_NotPrepassFail()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("absent-server");
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), testsId = Guid.NewGuid();
        var chainRoot = Path.Combine(scratch, "chain");
        var middleDir = Path.Combine(chainRoot, "middle-app");
        var testsDir = Path.Combine(chainRoot, "tests-app");
        WriteMiddleApp(middleDir, middleId, baseId);
        WriteTestApp(testsDir, testsId, middleId);

        await using var server = await CliServer.StartAsync(new[] { "--cache", Path.Combine(scratch, "al-out") });
        var lines = await server.SendRequestStreamingAsync(ServerReq(middleDir, testsDir), TimeSpan.FromSeconds(300));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        Assert.NotEqual(0, summary.GetProperty("exitCode").GetInt32());
        Assert.True(summary.TryGetProperty("compilationErrors", out var compileErrors),
            $"expected compilationErrors when base app is absent: {string.Join(" | ", lines)}");
        var allErrorText = string.Join(" | ", compileErrors.EnumerateArray()
            .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())));

        // The detailed report, delivered through the protocol — not the flattened one-liner.
        Assert.Contains("This is a PROVISIONING gap — your code is NOT the problem.", allErrorText);
        Assert.Contains("Missing: AL Runner/LPP Chain Base v1.0.0.0", allErrorText);
        Assert.Contains("Add the missing package to your --package-cache <dir>", allErrorText);
        Assert.Contains("Reached while building source dependency 'LPP Chain Middle'", allErrorText);
        Assert.DoesNotContain("LAYERED-PREPASS-FAIL", allErrorText);
    }

    // ── Negative: a genuine compile failure must NOT be reclassified ─────────────

    /// <summary>
    /// The guard against over-reaching. A real AL compile error inside the impl's emit —
    /// AL0543, nothing to do with dependency resolution — must still report exactly as it
    /// does today: the wrapped "[layered] Failed to emit symbols" text under
    /// <c>&lt;layered-deps&gt;: COMPILE-FAIL</c>, exit 3. If this went to exit 2 as well,
    /// the fix would have turned every layered failure into a "provisioning gap" and told
    /// users to fix their package cache when their AL genuinely does not compile.
    /// </summary>
    [SkippableFact]
    public void LayeredPrePass_GenuineCompileFailure_StillReportsCompileFail_Exit3()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("compile-fail");
        Guid middleId = Guid.NewGuid(), testsId = Guid.NewGuid();
        var chainRoot = Path.Combine(scratch, "chain");
        var middleDir = Path.Combine(chainRoot, "middle-app");
        var testsDir = Path.Combine(chainRoot, "tests-app");
        WriteBadMiddleApp(middleDir, middleId);
        WriteTestAppFor(testsDir, testsId, middleId, "LPP Bad Middle");

        var (output, exit) = RunRunner(scratch, middleDir, testsDir);

        Assert.Contains("<layered-deps>: COMPILE-FAIL", output);
        Assert.Contains("[layered] Failed to emit symbols for impl 'LPP Bad Middle'", output);
        Assert.Contains("AL0264", output);
        Assert.DoesNotContain("Unhandled exception", output);
        // Emphatically NOT reclassified as a provisioning gap.
        Assert.DoesNotContain("PROVISIONING gap", output);
        Assert.DoesNotContain("Add the missing package to your --package-cache", output);
        Assert.Equal(3, exit);
    }
}
