using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2542: the in-bundle sibling-symbols compile (<c>Program.EmitSiblingSymbols</c> →
/// <c>BcCompiler.EmitDepSymbols</c>) located the sibling's own app.json by scanning ONLY
/// its source folders:
/// <code>dirs.Select(d => Path.Combine(d, "app.json")).FirstOrDefault(File.Exists)</code>
/// <c>EmitDepSymbols</c> is already handed the app's own root as <c>appRootDir</c>
/// (<c>group.SuiteDir</c>), and <c>AppGroup</c>'s own documentation says that root is "NOT
/// the same as Paths (which may be src/test subdirectories)". <c>CollectSuitePaths</c>
/// reduces an app that keeps its AL under <c>src/</c> to exactly <c>[&lt;app&gt;/src]</c>,
/// so for that layout the scan above found no manifest at all and every manifest-derived
/// compiler input fell back to its unset default — while the SAME app's own
/// <c>Emit()</c> read them correctly from <c>appRootDir</c>.
///
/// Every AL fixture in this repo is flat (app.json beside the .al files), which is the one
/// layout where the folder scan happens to find the manifest. That is why neither #1898's
/// nor #1940/#1941/#1943's tests could see this.
///
/// Three facts, all over a ONE-bundle two-app tree in the app-root-plus-src/ layout:
///   - contextSensitiveHelpUrl, whose absence is a visible AL0543 diagnostic that aborts
///     the sibling compile and costs the dependent app its objects.
///   - preprocessorSymbols, whose absence silently drops a procedure from the symbols the
///     dependent binds against (AL0132) while the sibling's own runtime module still has
///     it — the quiet half of the same bug.
///   - the guard: a sibling app root that GENUINELY omits contextSensitiveHelpUrl must
///     still fail AL0543. The fix must not be "stop checking" (same trap as #1899/AL0327
///     and #1898's own negative fact).
///
/// Each positive fact runs the same bundle TWICE over its own --cache directory — once
/// cold, once warm. ComputeAlCacheKey hashes each .al file's path RELATIVE to the bundle,
/// so two runs over two different temp directories holding byte-identical fixtures produce
/// the same key: without a per-fact cache dir the second run of this class was silently a
/// HIT, and the cold-only assertions could not see it.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// Deliberately NOT converted to the shared --server fixture: the third fact's decisive
/// assertion is that the failure is a formatted runner outcome and not the CLR's default
/// handler, which is only observable by watching a process exit — same reasoning as
/// LayeredDepManifestTests.
/// </summary>
public class SiblingSymbolsAppRootManifestTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <param name="cacheDir">
    /// A per-fact AL-output cache directory, so the first call in a fact is genuinely a
    /// MISS and the second is genuinely a HIT. Without it these facts share the default
    /// ~/.cache/al-runner/al-out with every other run on the machine, and since
    /// ComputeAlCacheKey hashes each .al file's path RELATIVE to the bundle, two runs over
    /// two different temp directories holding byte-identical fixtures produce the SAME key
    /// — so the second run of the class silently served a cached DLL. That is invisible on
    /// a cold run and is exactly the failure mode .claude/rules/local-test-scope.md calls
    /// out for cache-sensitive changes. Lives under the fixture root (a temp dir), never
    /// inside a worktree.
    /// </param>
    private static (string output, int exit) RunRunner(string bundle, string cacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --cache \"").Append(cacheDir).Append('"');
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // Without these the EMIT-EXCLUDED payload names only the excluded OBJECT and never
        // the AL diagnostics that identified it — which is exactly what these facts read.
        psi.EnvironmentVariables["AL_RUNNER_DIAG_EMITRETRY"] = "1";
        psi.EnvironmentVariables["BCCOMPILER_DIAG"] = "1";
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The library app, in the app-root-plus-src/ layout that is the whole point of these
    /// facts: app.json at &lt;dir&gt;, every .al file one level down in &lt;dir&gt;/src.
    /// CollectSuitePaths hands EmitDepSymbols only &lt;dir&gt;/src for such an app.
    /// </summary>
    private static void WriteLibAppRootPlusSrc(
        string dir, string id, string name, int idFrom, int pageId, int codeunitId,
        string tag, string? contextSensitiveHelpUrl, string? preprocessorSymbol)
    {
        var src = Path.Combine(dir, "src");
        Directory.CreateDirectory(src);

        var helpUrlLine = contextSensitiveHelpUrl == null
            ? ""
            : $"\n  \"contextSensitiveHelpUrl\": \"{contextSensitiveHelpUrl}\",";
        var preprocLine = preprocessorSymbol == null
            ? ""
            : $"\n  \"preprocessorSymbols\": [ \"{preprocessorSymbol}\" ],";
        // No "application" property — see .claude/rules/no-base-app-in-csharp-tests.md.
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",{{helpUrlLine}}{{preprocLine}}
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);

        // A page that requires contextSensitiveHelpUrl to be set on the compilation
        // (AL0543 otherwise). BC reads that value from CompilationOptions, which
        // EmitDepSymbols builds from the manifest it managed to find.
        File.WriteAllText(Path.Combine(src, "HelpAware.Page.al"), $$"""
        page {{pageId}} "SSAR Help Aware Page {{tag}}"
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

        // The procedure the dependent app calls. When `preprocessorSymbol` is non-null the
        // manifest declares it AND the procedure sits behind an #if on it, so the symbol
        // reaching ParseOptions is the difference between the dependent seeing this member
        // and getting AL0132 for a member the sibling's runtime module demonstrably has.
        var body = preprocessorSymbol == null
            ? """
                  procedure Answer(): Integer
                  begin
                      exit(42);
                  end;
              """
            : $$"""
              #if {{preprocessorSymbol}}
                  procedure Answer(): Integer
                  begin
                      exit(42);
                  end;
              #endif
              """;
        File.WriteAllText(Path.Combine(src, "Answer.Codeunit.al"), $$"""
        codeunit {{codeunitId}} "SSAR Answer {{tag}}"
        {
        {{body}}
        }
        """);
    }

    /// <summary>The dependent app, same app-root-plus-src/ layout, same bundle.</summary>
    private static void WriteMainAppRootPlusSrc(
        string dir, string id, string name, int idFrom, int testCodeunitId,
        string depId, string depName, string answerCodeunitRef)
    {
        var src = Path.Combine(dir, "src");
        Directory.CreateDirectory(src);
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
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(src, "Tests.Codeunit.al"), $$"""
        codeunit {{testCodeunitId}} "SSAR Tests {{Path.GetFileName(dir)}}"
        {
            Subtype = Test;

            [Test]
            procedure SiblingCodeunit_Answer_Returns42()
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

    private static string NewRoot(string tag)
    {
        var root = TestScratch.Dir("al-runner-sibling-approot-" + tag);
        Directory.CreateDirectory(root);
        return root;
    }

    [SkippableFact]
    public void SiblingManifestSetsContextSensitiveHelpUrl_ReadFromAppRoot_BundleRuns()
    {
        TestArtifacts.SkipIfMissing();

        var root = NewRoot("ctxhelp-pos");
        var libId = "5a220000-0000-4000-8000-0000000000c1";
        var mainId = "5a220000-0000-4000-8000-0000000000c2";

        WriteLibAppRootPlusSrc(
            Path.Combine(root, "lib"), libId, "SSAR Pos Lib", 61300, 61300, 61301, "Pos",
            contextSensitiveHelpUrl: "https://example.com/docs/", preprocessorSymbol: null);
        WriteMainAppRootPlusSrc(
            Path.Combine(root, "main"), mainId, "SSAR Pos Main", 61320, 61320,
            libId, "SSAR Pos Lib", "SSAR Answer Pos");

        var cache = root + "-cache";
        var (output, exit) = RunRunner(root, cache);

        // Precondition: two DISTINCT app groups, so this really is the in-bundle
        // sibling-dependency shape and not one merged module where no sibling compile
        // happens at all. BCCOMPILER_DIAG (set on every spawn from this class) prints one
        // `module=` line per compiled module.
        Assert.Contains("module=SSAR Pos Lib", output);
        Assert.Contains("module=SSAR Pos Main", output);
        // AL0185 is what the dependent gets when the sibling's symbols never arrived.
        Assert.DoesNotContain("AL0185", output);
        // The manifest DOES set contextSensitiveHelpUrl — reading it from the app root is
        // the whole fix, so AL0543 must not fire.
        Assert.DoesNotContain("AL0543", output);
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"),
            $"a sibling whose app root sets contextSensitiveHelpUrl must compile and let the "
            + $"dependent app run (exit {exit}):\n{output}");

        // Second run against the SAME cache: the AL-output cache HIT must not resurrect a
        // pre-fix module or skip the sibling compile the dependent still binds against.
        var (warmOutput, warmExit) = RunRunner(root, cache);
        Assert.DoesNotContain("AL0543", warmOutput);
        Assert.DoesNotContain("AL0185", warmOutput);
        Assert.True(warmExit == 0 && warmOutput.Contains("1P/0F/0E"),
            $"warm re-run over the same cache must still pass (exit {warmExit}):\n{warmOutput}");
    }

    [SkippableFact]
    public void SiblingManifestPreprocessorSymbols_ReadFromAppRoot_DependentBindsGuardedProcedure()
    {
        TestArtifacts.SkipIfMissing();

        var root = NewRoot("preproc");
        var libId = "5a220000-0000-4000-8000-0000000000d1";
        var mainId = "5a220000-0000-4000-8000-0000000000d2";

        WriteLibAppRootPlusSrc(
            Path.Combine(root, "lib"), libId, "SSAR Pre Lib", 61340, 61340, 61341, "Pre",
            contextSensitiveHelpUrl: "https://example.com/docs/",
            preprocessorSymbol: "SSAR_FEATURE_ON");
        WriteMainAppRootPlusSrc(
            Path.Combine(root, "main"), mainId, "SSAR Pre Main", 61360, 61360,
            libId, "SSAR Pre Lib", "SSAR Answer Pre");

        var cache = root + "-cache";
        var (output, exit) = RunRunner(root, cache);

        Assert.Contains("module=SSAR Pre Lib", output);
        Assert.Contains("module=SSAR Pre Main", output);
        // When the manifest's preprocessorSymbols never reach ParseOptions the #if-guarded
        // procedure is absent from the sibling's symbols, and the dependent fails on it:
        // AL0185 when that leaves the codeunit itself empty/missing, AL0132 on the member.
        Assert.DoesNotContain("AL0132", output);
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        // The test asserts the concrete 42, so a stub returning 0 fails it.
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"),
            $"a sibling's manifest preprocessorSymbols must reach its symbol compile so the "
            + $"dependent binds the #if-guarded procedure (exit {exit}):\n{output}");

        var (warmOutput, warmExit) = RunRunner(root, cache);
        Assert.DoesNotContain("AL0132", warmOutput);
        Assert.DoesNotContain("AL0185", warmOutput);
        Assert.True(warmExit == 0 && warmOutput.Contains("1P/0F/0E"),
            $"warm re-run over the same cache must still pass (exit {warmExit}):\n{warmOutput}");
    }

    [SkippableFact]
    public void SiblingAppRootGenuinelyOmitsContextSensitiveHelpUrl_StillFailsAL0543()
    {
        TestArtifacts.SkipIfMissing();

        var root = NewRoot("ctxhelp-neg");
        var libId = "5a220000-0000-4000-8000-0000000000e1";
        var mainId = "5a220000-0000-4000-8000-0000000000e2";

        WriteLibAppRootPlusSrc(
            Path.Combine(root, "lib"), libId, "SSAR Neg Lib", 61380, 61380, 61381, "Neg",
            contextSensitiveHelpUrl: null, // genuinely unset — a real manifest error
            preprocessorSymbol: null);
        WriteMainAppRootPlusSrc(
            Path.Combine(root, "main"), mainId, "SSAR Neg Main", 61400, 61400,
            libId, "SSAR Neg Lib", "SSAR Answer Neg");

        var (output, exit) = RunRunner(root, root + "-cache");

        // Reading the app root must not turn into "stop checking": a manifest that really
        // omits the property is really invalid, and BC would reject it too.
        Assert.Contains("AL0543", output);
        // And the failure stays a formatted runner outcome, never the CLR's default handler.
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.NotEqual(0, exit);
    }
}
