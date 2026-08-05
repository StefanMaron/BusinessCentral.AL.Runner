// BatchAppIdentityTests — bundled mode must give each app.json its OWN module
// identity, not one synthetic identity for the whole bundle.
//
// The bug: bundled mode merges every suite's .al files into ONE compilation named
// "V2_<bundle-dir>" with a single appId/publisher/version. One module means one app
// identity, so for every suite in the bundle:
//   - NavApp.GetCurrentModuleInfo returns the synthetic name, not the app's own
//   - NavApp.GetResource looks in app '' and finds nothing
//   - install-trigger seeding has no per-app manifest to seed from
// That is 13 of the 16 failures the suite-enumeration fix exposed in
// tests/runner-extras, and it would hit any user with a multi-app workspace.
//
// Isolating each suite into its own process is NOT the fix — measured at 23s
// bundled vs 180s per-suite over 68 suites, and per-suite additionally drops every
// test that needs a sibling app compiled alongside it (the *-dep / *-main pairs).
// The fix is one process, one runtime init, one test run, but one emitted module
// per app.json.
//
// This fixture uses two INDEPENDENT apps (no dependency between them) so it isolates
// the identity question alone. The dependency-ordering half is covered by the
// existing tests/runner-extras/navapp-moduleinfo-{dep,main} pair.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why this is serialized with the other
// runner-subprocess integration tests.
[Collection("server-serial")]
public sealed class BatchAppIdentityTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public BatchAppIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-batch-ident", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stdCache = Path.Combine(home, ".local", "share", "al-runner", "artifacts");
        return Directory.Exists(stdCache) && Directory.EnumerateDirectories(stdCache).Any();
    }

    /// <summary>
    /// Writes an app that asserts its OWN name and version via
    /// NavApp.GetCurrentModuleInfo. The assertion names the expected identity
    /// literally, so a synthetic bundle-wide identity fails with a message naming
    /// what it actually saw.
    /// </summary>
    private static void WriteIdentityApp(
        string dir, string appId, string appName, string version, int baseId, string tag)
    {
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "{{appName}}",
          "publisher": "AL Runner",
          "version": "{{version}}",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, $"BatchIdent{tag}.Codeunit.al"), $$"""
        codeunit {{baseId}} "Batch Ident {{tag}}"
        {
            Subtype = Test;

            [Test]
            procedure OwnModuleName{{tag}}()
            var
                Info: ModuleInfo;
            begin
                NavApp.GetCurrentModuleInfo(Info);
                if Info.Name() <> '{{appName}}' then
                    Error('App {{tag}} must see its own name {{appName}}, got %1.', Info.Name());
            end;

            [Test]
            procedure OwnModuleVersion{{tag}}()
            var
                Info: ModuleInfo;
            begin
                NavApp.GetCurrentModuleInfo(Info);
                if Format(Info.AppVersion()) <> '{{version}}' then
                    Error('App {{tag}} must see its own version {{version}}, got %1.',
                        Format(Info.AppVersion()));
            end;
        }
        """);
    }

    private (string output, int exit) RunBundled(params string[] extra)
    {
        // No --per-suite: this asserts the DEFAULT (bundled) path, which is what
        // both CI legs and every user invocation use.
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + $" \"{_root}\"");
        foreach (var a in extra) args.Append($" {a}");
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
    /// Two independent apps in one bundled run must each see their own name and
    /// version. Before the fix both saw the synthetic "V2_&lt;bundle-dir&gt;" identity
    /// and all four assertions failed.
    /// </summary>
    [Fact]
    public void BundledRun_EachAppSeesItsOwnModuleIdentity()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not present"); return; }

        WriteIdentityApp(Path.Combine(_root, "app-one"),
            "11111111-aaaa-4aaa-8aaa-111111111111", "Batch Ident One", "2.5.0.0", 62300, "One");
        WriteIdentityApp(Path.Combine(_root, "app-two"),
            "22222222-bbbb-4bbb-8bbb-222222222222", "Batch Ident Two", "3.7.1.0", 62310, "Two");

        var (output, exit) = RunBundled();

        // All four tests must run — a suite whose emit is dropped shows up as a
        // missing line, not as a failure, so assert presence explicitly.
        Assert.Contains("OwnModuleNameOne", output);
        Assert.Contains("OwnModuleVersionOne", output);
        Assert.Contains("OwnModuleNameTwo", output);
        Assert.Contains("OwnModuleVersionTwo", output);

        Assert.DoesNotContain("FAIL ", output);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// Negative direction: an app asserting an identity that is NOT its own must
    /// fail, and the failure message must name the identity actually seen. Without
    /// this, the positive test above would still pass if GetCurrentModuleInfo were
    /// hardwired to echo whatever the assertion expected.
    /// </summary>
    [Fact]
    public void BundledRun_WrongExpectedIdentity_FailsNamingWhatItSaw()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not present"); return; }

        // app.json says "Batch Ident Real"; the AL asserts it is called "Wrong Name".
        var dir = Path.Combine(_root, "app-mismatch");
        WriteIdentityApp(dir, "33333333-cccc-4ccc-8ccc-333333333333",
            "Batch Ident Real", "1.0.0.0", 62320, "Mismatch");
        var al = Path.Combine(dir, "BatchIdentMismatch.Codeunit.al");
        File.WriteAllText(al, File.ReadAllText(al).Replace("'Batch Ident Real'", "'Wrong Name'"));

        // --strict: v2 only propagates a non-zero exit code under --strict, so the
        // exit-code half of this assertion needs it. (Whether plain test failures
        // should exit non-zero without --strict is a separate parity question.)
        var (output, exit) = RunBundled("--strict");

        Assert.Contains("FAIL", output);
        Assert.Contains("Batch Ident Real", output);   // names what it actually saw
        Assert.NotEqual(0, exit);
    }
}
