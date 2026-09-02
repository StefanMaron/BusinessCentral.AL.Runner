using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1898: the layered source-dependency compile (<c>Program.RunLayeredPrePass</c> →
/// <c>BcCompiler.EmitDepSymbols</c>) used to build the dep's <c>Compilation</c> from
/// identity + <c>internalsVisibleTo</c> only, never from the dep's own app.json. So a
/// dependency that legitimately sets <c>contextSensitiveHelpUrl</c> and has a page
/// setting <c>ContextSensitiveHelpPage</c> failed AL0543 — "manifest property
/// 'contextSensitiveHelpUrl' must be set" — against a manifest that DOES set it. The
/// resulting <c>InvalidOperationException</c> was thrown from a call site outside every
/// try/catch in Main, so it reached the CLR's default handler and aborted the whole
/// process (exit 134, no al-runner-formatted output) before a single test in EITHER
/// bundle ran — including tests unrelated to help links.
///
/// Two tests, both directions:
///   - Positive: the dep's app.json genuinely sets contextSensitiveHelpUrl → the
///     layered build must honour it, AL0543 must not fire, and both bundles' tests run.
///   - Negative: the dep's app.json genuinely OMITS contextSensitiveHelpUrl (an actually
///     invalid manifest, given the page's ContextSensitiveHelpPage) → AL0543 must still
///     fire (the fix must not just make the diagnostic disappear unconditionally — that
///     would hide a real manifest error, same trap as #1899/AL0327), but now as a
///     formatted "<layered-deps>: COMPILE-FAIL" line with the documented exit code 3
///     (docs/server-mode.md's compile-error ladder), never an unhandled-exception
///     stack trace and exit 134.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
///
/// #2377: deliberately NOT moved to the shared --server fixture, unlike its neighbours.
/// The negative fact's decisive assertion is that "Unhandled exception" is ABSENT — that
/// an invalid dep manifest yields a formatted "<layered-deps>: COMPILE-FAIL" and exit 3
/// rather than the CLR's default handler aborting the PROCESS with exit 134/SIGABRT,
/// which is exactly what #1898 fixed and exactly what a pre-fix regression would undo.
/// That claim is about the CLI's own Main-level exception handling and can only be
/// observed by watching a process exit; RunAllBundlesForServer wraps the same pre-pass in
/// its own try/catch, so a server request can never reach the code path being held down.
/// Converting the POSITIVE fact alone would leave the class spawning anyway for the
/// negative one, so it buys a server process rather than removing one. A test that keeps
/// its meaning and stays slow beats a fast one that proves something else
/// (.claude/rules/tdd.md). See ManifestFeaturesSubprocessTests' fourth fact for the same
/// call, made one fact at a time.
/// </summary>
public class LayeredDepManifestTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
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

    private static void WriteDep(string dir, string id, string name, int idFrom,
        int pageId, int codeunitId, string? contextSensitiveHelpUrl)
    {
        Directory.CreateDirectory(dir);
        var helpUrlLine = contextSensitiveHelpUrl == null
            ? ""
            : $"\n  \"contextSensitiveHelpUrl\": \"{contextSensitiveHelpUrl}\",";
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",{{helpUrlLine}}
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);
        // A page that requires contextSensitiveHelpUrl to be set (AL0543 otherwise), plus
        // a plain codeunit the dependent bundle actually exercises.
        File.WriteAllText(Path.Combine(dir, "HelpAware.Page.al"), $$"""
        page {{pageId}} "LDM Help Aware Page"
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
        codeunit {{codeunitId}} "LDM Answer"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);
    }

    private static void WriteMain(string dir, string id, string name, int idFrom,
        int testCodeunitId, string depId, string depName, string answerCodeunitRef)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "{{depName}}", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{testCodeunitId}} "LDM Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepCodeunit_Answer_Returns42()
            var
                Answer: Codeunit "{{answerCodeunitRef}}";
                Actual: Integer;
            begin
                Actual := Answer.Answer();
                if Actual <> 42 then
                    Error('Expected 42 but got %1', Actual);
            end;
        }
        """);
    }

    [SkippableFact]
    public void DepManifestSetsContextSensitiveHelpUrl_LayeredBuildHonoursIt_BothBundlesRun()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-layered-ctxhelp-pos", Guid.NewGuid().ToString("N"));
        var depDir = Path.Combine(root, "dep");
        var mainDir = Path.Combine(root, "main");
        var depId = "c1a20000-0000-4000-8000-0000000000a1";
        var mainId = "c1a20000-0000-4000-8000-0000000000a2";

        WriteDep(depDir, depId, "LDM Pos Dep", 60900, 60900, 60901,
            contextSensitiveHelpUrl: "https://example.com/docs/");
        WriteMain(mainDir, mainId, "LDM Pos Main", 60910, 60910, depId, "LDM Pos Dep", "LDM Answer");

        var (output, exit) = RunRunner(depDir, mainDir);

        // Precondition: this really took the layered two-bundle source-dependency path,
        // not a degenerate single-bundle one.
        Assert.Contains("[layered]", output);
        Assert.DoesNotContain("AL0543", output);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"),
            $"a dependency whose manifest genuinely sets contextSensitiveHelpUrl must compile and run (exit {exit}):\n{output}");
    }

    [SkippableFact]
    public void DepManifestOmitsContextSensitiveHelpUrl_StillFailsAL0543_AsFormattedCompileFailNotCrash()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-layered-ctxhelp-neg", Guid.NewGuid().ToString("N"));
        var depDir = Path.Combine(root, "dep");
        var mainDir = Path.Combine(root, "main");
        var depId = "c1a20000-0000-4000-8000-0000000000b1";
        var mainId = "c1a20000-0000-4000-8000-0000000000b2";

        WriteDep(depDir, depId, "LDM Neg Dep", 60920, 60920, 60921,
            contextSensitiveHelpUrl: null); // genuinely unset — a real manifest error
        WriteMain(mainDir, mainId, "LDM Neg Main", 60930, 60930, depId, "LDM Neg Dep", "LDM Answer");

        var (output, exit) = RunRunner(depDir, mainDir);

        // The fix must not make AL0543 unconditionally disappear — a manifest that
        // genuinely omits the property is genuinely invalid, and BC would reject it too.
        Assert.Contains("AL0543", output);
        // But the failure must be a formatted, documented runner outcome, never the raw
        // CLR unhandled-exception path (which — pre-fix — produced this exact stack:
        // "Unhandled exception. System.InvalidOperationException: [layered] Failed to
        // emit symbols…" and exit code 134/SIGABRT).
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.Contains("COMPILE-FAIL", output);
        Assert.Equal(3, exit);
    }
}
