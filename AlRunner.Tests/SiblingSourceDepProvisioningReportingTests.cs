using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2095: a missing/too-old THIRD-PARTY dependency reached through
/// <c>Program.BuildSiblingSourceDeps</c>'s own dependency resolution (resolving the
/// sibling source app's OWN <c>dependencies</c>, not the main bundle's) was folded into
/// the generic <c>catch (Exception ex)</c> at that call site and printed as
/// <c>"&lt;sibling-source-deps&gt;: COMPILE-FAIL — {ex.Message}"</c> — the SHORT one-line
/// form of the exception, under a label that reads as "your AL code did not compile"
/// when it is actually a provisioning or version gap the reader cannot fix by touching
/// their AL.
///
/// Three tests, all through the real spawned runner (same shape as
/// LayeredDepManifestTests, which covers the sibling RunLayeredPrePass call site for the
/// same #1898 unhandled-exception fix):
///   - Positive (missing): the sibling source app's own declared dependency is absent
///     from every cache dir → provisioning-gap wording, --package-cache guidance, no
///     COMPILE-FAIL.
///   - Positive (version too old): the dependency IS present but every candidate is
///     below the declared minimum → version-gap wording, no COMPILE-FAIL.
///   - Negative (regression guard for #1898): a genuine failure on the SAME call site
///     that is NOT one of the two dependency-resolution exceptions must still report
///     exactly as it does today — "COMPILE-FAIL" + exit 3 — proving the special case
///     did not swallow the general one.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class SiblingSourceDepProvisioningReportingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string mainBundle, string? packageCacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        if (packageCacheDir != null)
            args.Append(" --package-cache \"").Append(packageCacheDir).Append('"');
        args.Append(" \"").Append(mainBundle).Append('"');
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

    private static void WriteMain(string dir, string id, int idFrom, string sidekickId, int testCodeunitId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "SSD Main",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{sidekickId}}", "name": "SSD Sidekick", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{testCodeunitId}} "SSD Tests"
        {
            Subtype = Test;

            [Test]
            procedure NeverReached()
            begin
                Error('should never compile far enough to run this');
            end;
        }
        """);
    }

    /// <summary>
    /// Sidekick's OWN app.json declares a dependency on a third-party app that is
    /// completely absent from every searched cache dir. Optionally omits
    /// contextSensitiveHelpUrl to also produce a genuine AL0543 further down the
    /// pipeline — irrelevant here, we never get that far.
    /// </summary>
    private static void WriteSidekick(string dir, string id, int idFrom,
        string thirdPartyId, string thirdPartyMinVersion, int codeunitId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "SSD Sidekick",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{thirdPartyId}}", "name": "Acme Add-On", "publisher": "Acme Corp", "version": "{{thirdPartyMinVersion}}" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Answer.Codeunit.al"), $$"""
        codeunit {{codeunitId}} "SSD Sidekick Answer"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);
    }

    /// <summary>
    /// Sidekick whose own manifest genuinely omits contextSensitiveHelpUrl for a page
    /// that requires it — a real AL0543, unrelated to dependency resolution (no
    /// declared dependencies at all). Used as the regression guard: the SAME
    /// try/catch this issue touches must still report a non-dependency failure the way
    /// it always has.
    /// </summary>
    private static void WriteBadManifestSidekick(string dir, string id, int idFrom,
        int pageId, int codeunitId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "SSD Sidekick",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "HelpAware.Page.al"), $$"""
        page {{pageId}} "SSD Help Aware Page"
        {
            PageType = Card;
            ContextSensitiveHelpPage = 'sales-invoice';

            layout
            {
                area(Content)
                {
                    field(Dummy; DummyValue) { ApplicationArea = All; Caption = 'Dummy'; }
                }
            }

            var
                DummyValue: Text[30];
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Answer.Codeunit.al"), $$"""
        codeunit {{codeunitId}} "SSD Sidekick Answer"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);
    }

    /// <summary>Writes a minimal NAVX .app file (header + ZIP with NavxManifest.xml) — a
    /// synthetic third-party dependency package for the resolver to index, without
    /// needing a real BC compile.</summary>
    private static void WriteMinimalApp(string dir, string fileName, string appId, string name,
        string publisher, string version)
    {
        Directory.CreateDirectory(dir);
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var es = entry.Open();
            es.Write(Encoding.UTF8.GetBytes(xml));
        }
        var zipBytes = ms.ToArray();
        // NAVX wrapper AppLoader.NavxZipOffset actually recognizes: magic "NAVX" + LE
        // uint32 zip offset (8) + zip bytes immediately after. A file that does not start
        // with "NAVX" reads as zip-offset-0, i.e. THIS byte stream must itself be a valid
        // zip — which a zero-filled placeholder header is not, so the .app silently fails
        // to parse (AppLoader.ReadManifest returns null) and never gets indexed at all.
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(Path.Combine(dir, fileName), result);
    }

    [SkippableFact]
    public void SiblingDep_ThirdPartyDependencyMissingEverywhere_ReportsProvisioningGap_NotCompileFail()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-ssd-missing", Guid.NewGuid().ToString("N"));
        var mainDir = Path.Combine(root, "main");
        var sidekickDir = Path.Combine(root, "sidekick");
        var emptyCacheDir = Path.Combine(root, "empty-cache");
        Directory.CreateDirectory(emptyCacheDir);
        var mainId = "5cd10000-0000-4000-8000-0000000000c1";
        var sidekickId = "5cd10000-0000-4000-8000-0000000000c2";
        var thirdPartyId = "5cd10000-0000-4000-8000-0000000000c3";

        WriteMain(mainDir, mainId, 60950, sidekickId, 60950);
        WriteSidekick(sidekickDir, sidekickId, 60960, thirdPartyId, "2.0.0.0", 60961);

        var (output, exit) = RunRunner(mainDir, emptyCacheDir);

        // Must NOT be mislabeled as a compile failure.
        Assert.DoesNotContain("COMPILE-FAIL", output);
        Assert.DoesNotContain("Unhandled exception", output);
        // Must reach the detailed, actionable message: names the gap kind and the fix.
        Assert.Contains("PROVISIONING gap", output);
        Assert.Contains("Acme Add-On", output);
        Assert.Contains("--package-cache", output);
        Assert.Equal(2, exit);
    }

    [SkippableFact]
    public void SiblingDep_ThirdPartyDependencyPresentButTooOld_ReportsVersionGap_NotCompileFail()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-ssd-oldver", Guid.NewGuid().ToString("N"));
        var mainDir = Path.Combine(root, "main");
        var sidekickDir = Path.Combine(root, "sidekick");
        var cacheDir = Path.Combine(root, "cache");
        var mainId = "5cd20000-0000-4000-8000-0000000000d1";
        var sidekickId = "5cd20000-0000-4000-8000-0000000000d2";
        var thirdPartyId = "5cd20000-0000-4000-8000-0000000000d3";

        WriteMain(mainDir, mainId, 60970, sidekickId, 60970);
        WriteSidekick(sidekickDir, sidekickId, 60980, thirdPartyId, "2.0.0.0", 60981);
        // Third-party dep IS in the cache, but only at v1.0.0.0 — below the declared v2.0.0.0.
        WriteMinimalApp(cacheDir, "AcmeAddOn_v1.app", thirdPartyId, "Acme Add-On", "Acme Corp", "1.0.0.0");

        var (output, exit) = RunRunner(mainDir, cacheDir);

        Assert.DoesNotContain("COMPILE-FAIL", output);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.Contains("VERSION gap", output);
        Assert.Contains("Acme Add-On", output);
        // Names the fix: get a build at/above the declared minimum.
        Assert.Contains("2.0.0.0", output);
        Assert.Equal(2, exit);
    }

    [SkippableFact]
    public void SiblingDep_GenuineManifestFailure_StillReportsCompileFail_Exit3()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-ssd-badmanifest", Guid.NewGuid().ToString("N"));
        var mainDir = Path.Combine(root, "main");
        var sidekickDir = Path.Combine(root, "sidekick");
        var mainId = "5cd30000-0000-4000-8000-0000000000e1";
        var sidekickId = "5cd30000-0000-4000-8000-0000000000e2";

        WriteMain(mainDir, mainId, 60990, sidekickId, 60990);
        WriteBadManifestSidekick(sidekickDir, sidekickId, 60995, 60995, 60996);

        var (output, exit) = RunRunner(mainDir, packageCacheDir: null);

        // A genuine non-dependency-resolution failure on the SAME call site must still
        // report exactly as it always has — the #2095 special case must not swallow it.
        Assert.Contains("AL0543", output);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.Contains("<sibling-source-deps>: COMPILE-FAIL", output);
        Assert.Equal(3, exit);
    }
}
