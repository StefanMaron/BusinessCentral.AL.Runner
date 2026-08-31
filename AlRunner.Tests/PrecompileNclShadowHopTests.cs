// PrecompileNclShadowHopTests — issue #2065.
//
// The question #2065 asks is "why did Microsoft.Dynamics.Nav.Ncl.dll appear in
// AlRunner/bin/Release/net8.0/ at all?", and the answer measured while closing it is:
// the `--precompile` subcommand wrote it there.
//
// Program.cs dispatches `--precompile` (RunPrecompile) near the very top of Main, long
// before the shadow-re-exec decision point that the normal bundle-run flow passes
// through. RunPrecompile then calls
//
//     NclCecilRewrite.RewriteInPlace(srcDir, Path.Combine(AppContext.BaseDirectory,
//                                                         "Microsoft.Dynamics.Nav.Ncl.dll"))
//
// unconditionally. On an install that does NOT ship Ncl.dll beside al-runner.dll — which
// is every install since #2023/#2026, including AlRunner's own build output, where
// Directory.Build.targets strips it — RewriteInPlace CREATES the file rather than
// replacing one. That single write is the whole defect:
//
//   * It permanently changes what NclShadowRuntime.NeedsShadow() answers for that
//     directory, so every LATER invocation from the same directory silently stops taking
//     the shadow hop. The runner's observable behaviour then differs between a clean
//     checkout and a used one — measured: with the stray file present the
//     `[reexec] Ncl.dll not shipped in this install ...` line disappears entirely.
//   * It contaminates a directory `--precompile` does not own. Two implementation agents
//     independently lost time to exactly this on 2026-08-29 (five
//     StartupOutputReexecDedupTests failing their own precondition because a stray
//     Ncl.dll had been left in the shared AlRunner/bin/Release/net8.0), and both worked
//     around it by deleting the file by hand.
//   * It does not even help THIS process. CoreCLR computes the trusted-platform-assemblies
//     list in the native host, before any managed code runs, from the literal on-disk
//     contents of AppContext.BaseDirectory AT THAT MOMENT — see NclShadowRuntime's class
//     doc. Writing the file a few statements into RunPrecompile is too late for the
//     process doing the writing; it only ever benefits the NEXT one.
//
// The main bundle-run flow already has the right answer for all of this: hop into a
// runner-owned shadow directory that legitimately holds Ncl.dll before its TPA is
// computed, and rewrite in place THERE. This suite pins that `--precompile` takes the
// same hop, in both directions.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompileNclShadowHopTests
{
    private const string NclFileName = "Microsoft.Dynamics.Nav.Ncl.dll";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string ShadowReexecLine = "[reexec] Ncl.dll not shipped in this install";

    /// <summary>
    /// A private, uniquely-named mirror of AlRunner's real build output — the same
    /// mechanism (and for the same reason) as StartupOutputReexecDedupTests.MirrorOriginalBinDir:
    /// these tests need to observe, and in one case deliberately change, whether Ncl.dll sits
    /// beside al-runner.dll, and the shared build output directory is spawned against
    /// concurrently by roughly two dozen other classes in this assembly.
    /// </summary>
    private static string MirrorOriginalBinDir()
    {
        var originalBinDir = Path.Combine(
            ProjectPath, "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework);
        var originalNcl = Path.Combine(originalBinDir, NclFileName);
        Assert.False(File.Exists(originalNcl),
            $"precondition violated: {originalNcl} already exists in the runner's shared build " +
            "output directory. It is stripped from AlRunner's copy-local set by " +
            "Directory.Build.targets, so a fresh build never produces it — if it is there, " +
            "something wrote it (the defect issue #2065 is about), and mirroring would copy " +
            "that contamination forward and make these tests assert nothing.");

        var privateDir = Directory.CreateTempSubdirectory("al-runner-precompile-mirror-").FullName;
        NclShadowRuntime.MirrorInstallDirectory(originalBinDir, privateDir);
        return privateDir;
    }

    /// <summary>
    /// Minimal but real .app package: a plain ZIP (AppLoader falls back to offset 0 when the
    /// 8-byte NAVX magic does not match) holding NavxManifest.xml plus one src/*.al table.
    /// Version/Application/Platform are all pinned to the exact 4-part BC build this binary
    /// was compiled against, so RunPrecompile's own SelectVersion(manifest.Version, null)
    /// resolves a real artifact directory instead of falling through to a non-deterministic
    /// "latest in cache" default — same reasoning as PrecompileAlDiagnosticFailureTests.
    /// </summary>
    private static string WriteAppPackage(string dir, int tableId)
    {
        var bcVersion = BcArtifacts.EngineBuiltVersion()?.ToString()
            ?? throw new InvalidOperationException(
                "EngineBuiltVersion() is null — this test binary's BcEngineVersion " +
                "AssemblyMetadata is missing; rebuild AlRunner before running this test.");
        var name = $"Precompile2065_{tableId}";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{Guid.NewGuid()}" Name="{name}" Publisher="Repro2065" Version="{bcVersion}"
                   Application="{bcVersion}" Platform="{bcVersion}"/>
              <Dependencies/>
            </Package>
            """;

        Directory.CreateDirectory(dir);
        var appPath = Path.Combine(dir, name + ".app");
        using (var fs = new FileStream(appPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var es = zip.CreateEntry("NavxManifest.xml").Open())
                es.Write(Encoding.UTF8.GetBytes(xml));
            using (var es = zip.CreateEntry("src/Repro.Table.al").Open())
                es.Write(Encoding.UTF8.GetBytes($$"""
                    table {{tableId}} "Repro 2065 Tab {{tableId}}"
                    {
                        fields
                        {
                            field(1; "No."; Code[20]) { }
                            field(2; Amount; Decimal) { }
                        }
                        keys { key(PK; "No.") { Clustered = true; } }
                    }
                    """));
        }
        return appPath;
    }

    private static (string Output, int Exit) RunPrecompile(string dllPath, string appPath, string outputDll)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --precompile \"{appPath}\" --out \"{outputDll}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Issue #2239 reversed part of #2038's decision: `[reexec]` no longer survives
        // Log's default filter unconditionally — a clean run does not need its own
        // process topology to read its results, so it now requires --verbose. This
        // class is specifically about the re-exec hop (whether it fires, exactly once,
        // idempotently), which is exactly the detail --verbose exists to surface. Set
        // via the AL_RUNNER_VERBOSE env var, not a `--verbose` CLI arg: `--precompile`
        // dispatches through RunPrecompile's own minimal sub-arg parser (`--out` and
        // `--package-cache` only, everything else silently ignored) rather than the
        // main flag parser that recognises `--verbose`, so passing it on the command
        // line here would do nothing.
        psi.Environment["AL_RUNNER_VERBOSE"] = "1";
        psi.Environment.Remove("AL_RUNNER_NCL_SHADOW_DONE");
        psi.Environment.Remove("AL_RUNNER_REEXECED");

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(300_000), "--precompile did not exit within 300s");
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>
    /// #2065, the defect itself: `--precompile` run from an install that does not ship
    /// Ncl.dll must take the shadow hop (like the bundle-run flow already does) and leave the
    /// directory it was launched from exactly as it found it.
    ///
    /// Asserts all three things that matter together, because any one alone is satisfiable by
    /// a wrong implementation: the subcommand still WORKS (a DLL is written), it announced the
    /// hop exactly once, and no Ncl.dll was left behind — so NeedsShadow answers the same
    /// before and after, and a second invocation from the same directory behaves identically
    /// to the first.
    /// </summary>
    [SkippableFact]
    public void Precompile_InstallWithoutNcl_HopsToShadowDirAndLeavesInstallDirClean()
    {
        TestArtifacts.SkipIfMissing();

        var privateDir = MirrorOriginalBinDir();
        try
        {
            var privateDll = Path.Combine(privateDir, "al-runner.dll");
            var privateNcl = Path.Combine(privateDir, NclFileName);
            Assert.True(NclShadowRuntime.NeedsShadow(privateDir),
                $"precondition violated: {privateNcl} exists in the freshly built mirror");

            var workDir = Path.Combine(privateDir, "..", "al-runner-2065-work-" + Guid.NewGuid().ToString("N"));
            var appPath = WriteAppPackage(workDir, 62290);
            var outputDll = Path.Combine(workDir, "Repro2065.dll");

            var (output, exit) = RunPrecompile(privateDll, appPath, outputDll);

            // --precompile still does its job. Without this, "never write Ncl.dll" would be
            // satisfied by a subcommand that simply stopped working.
            Assert.Equal(0, exit);
            Assert.True(File.Exists(outputDll),
                $"--precompile exited 0 but wrote no DLL at {outputDll}.\n{output}");

            // The defect: nothing was written into the directory --precompile was launched from.
            Assert.False(File.Exists(privateNcl),
                $"--precompile left {NclFileName} in the install directory it was launched from " +
                $"({privateDir}) — that permanently flips NclShadowRuntime.NeedsShadow for every " +
                $"later invocation from there. Output:\n{output}");
            Assert.True(NclShadowRuntime.NeedsShadow(privateDir));

            // It got there via the shadow hop, announced once — not by skipping the rewrite.
            Assert.Equal(1, CountOccurrences(output, ShadowReexecLine));

            // Idempotent: a second run from the same directory behaves exactly like the first,
            // which is the property the stray file destroyed.
            var appPath2 = WriteAppPackage(workDir, 62291);
            var outputDll2 = Path.Combine(workDir, "Repro2065b.dll");
            var (output2, exit2) = RunPrecompile(privateDll, appPath2, outputDll2);
            Assert.Equal(0, exit2);
            Assert.True(File.Exists(outputDll2));
            Assert.False(File.Exists(privateNcl));
            Assert.Equal(1, CountOccurrences(output2, ShadowReexecLine));
        }
        finally
        {
            Directory.Delete(privateDir, recursive: true);
        }
    }

    /// <summary>
    /// The negative direction, and the reason the fix cannot simply be "never rewrite from
    /// --precompile": an install that LEGITIMATELY ships Ncl.dll beside al-runner.dll (a
    /// shadow child's own directory is exactly this shape, and so is AlRunner.Tests' own bin
    /// dir) must be recognised as not needing the hop. It must run in-process — no `[reexec]`
    /// line, one process — and it must still hold its Ncl.dll afterwards, rewritten in place
    /// the way every Ncl-shipping install already expects.
    /// </summary>
    [SkippableFact]
    public void Precompile_InstallShippingNcl_RunsInProcessWithNoShadowHop()
    {
        TestArtifacts.SkipIfMissing();

        var privateDir = MirrorOriginalBinDir();
        try
        {
            var privateDll = Path.Combine(privateDir, "al-runner.dll");
            var privateNcl = Path.Combine(privateDir, NclFileName);

            // Make this mirror a legitimately Ncl-shipping install, the way every install
            // was before #2023/#2026 stripped the file from the package. Source: the BC
            // artifact cache's own service-tier copy — the same file NclShadowRuntime reads
            // when it builds a shadow dir.
            File.Copy(Path.Combine(BcArtifacts.ServiceTierDir, NclFileName), privateNcl);
            Assert.False(NclShadowRuntime.NeedsShadow(privateDir),
                "precondition violated: NeedsShadow is still true after placing Ncl.dll beside " +
                "the entry assembly");

            var workDir = Path.Combine(privateDir, "..", "al-runner-2065-work-" + Guid.NewGuid().ToString("N"));
            var appPath = WriteAppPackage(workDir, 62292);
            var outputDll = Path.Combine(workDir, "Repro2065Shipping.dll");

            var (output, exit) = RunPrecompile(privateDll, appPath, outputDll);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(outputDll),
                $"--precompile exited 0 but wrote no DLL at {outputDll}.\n{output}");

            // No hop: this install already satisfies the precondition the hop exists to create.
            Assert.Equal(0, CountOccurrences(output, ShadowReexecLine));

            // And the file it legitimately ships is still there — rewritten in place, not
            // removed, not left pristine.
            Assert.True(File.Exists(privateNcl),
                $"{privateNcl} disappeared — an Ncl-shipping install must keep its own copy");
            Assert.False(NclShadowRuntime.NeedsShadow(privateDir));
        }
        finally
        {
            Directory.Delete(privateDir, recursive: true);
        }
    }
}
