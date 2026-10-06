using System.Diagnostics;
using AlRunner.Infrastructure;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The REAL <c>AL_RUNNER_WIN32_STUBS_SO</c> variable, read by a real runner child. It is the documented
/// remedy for a machine without a C compiler (README, troubleshooting, the no-compiler error text), and
/// since #5201 no test may set it in the test host: <see cref="Win32StubsLoudFailureTests"/> goes through
/// <c>Win32Stubs.OverrideSoForTests</c>, which never reads the variable. So THIS class is the only thing
/// that can notice <c>GetOrBuild</c> ceasing to honour it. The value is given to the child's own
/// <see cref="ProcessStartInfo.Environment"/>, never to the host.
/// </summary>
public class Win32StubsEnvVarChildTests
{
    private const string Var = "AL_RUNNER_WIN32_STUBS_SO";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    /// <summary>
    /// The start info of one runner child on <paramref name="bundle"/>, with <paramref name="soValue"/> as
    /// the variable in the CHILD's environment only (<c>null</c> removes it, so an ambient value cannot
    /// leak in). The children here abort on purpose, and the C# CI legs export
    /// <c>DOTNET_DbgEnableMiniDump</c> (.github/workflows/bc-tests.yml), which the child would inherit:
    /// each abort would then write a heap dump of several hundred MB into the artifact that real crashes
    /// are diagnosed from (#5201 itself was). The child's own copy turns it off.
    /// </summary>
    [ExpectedRunnerAbort]
    internal static ProcessStartInfo BuildChildStartInfo(string bundle, string? soValue)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --no-cache",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.SwitchOff();
        if (soValue is null) psi.Environment.Remove(Var); else psi.Environment[Var] = soValue;
        return psi;
    }

    private static (string Output, int Exit) RunChild(string? soValue)
    {
        var bundle = TestScratch.Dir("al-runner-win32-env-child");
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var sb = new StringBuilder();
        using var p = Process.Start(BuildChildStartInfo(bundle, soValue))!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The children are started with the heap-dump variable turned off even when the host has it on,
    /// without running an abort. A non-"0" value, or no value, means the CI leg's dump settings would
    /// reach a deliberately aborting child. Pure: it reads the start info and starts nothing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("/tmp/some-shim.so")]
    public void ChildrenAreStartedWithTheCrashDumpSwitchOff(string? soValue)
    {
        var psi = BuildChildStartInfo("/unused", soValue);
        Assert.Equal("0", psi.Environment[CrashDump.SwitchVariable]);
        // The variable under test is still handed over, or removed, exactly as asked.
        if (soValue is null) Assert.False(psi.Environment.ContainsKey(Var));
        else Assert.Equal(soValue, psi.Environment[Var]);
    }

    /// <summary>
    /// Two children at once (a runner run costs seconds): the variable absent (control: the fixture runs
    /// and passes, so the exit below is the variable's doing) and pointing at a file that does not exist,
    /// which must abort with the shim's own loud message naming the variable and the path. A child that
    /// ignored the variable would exit 0 like the control. SIGABRT (134) is a POSIX exit; on Windows the
    /// shim resolver is never registered and the variable is ignored.
    /// </summary>
    [SkippableFact]
    public async Task RealVariable_NamingAMissingFile_AbortsARunnerChildLoudly()
    {
        Skip.If(OperatingSystem.IsWindows(), "the Win32 shim exists only to fake Win32 on Linux/macOS; on Windows the variable is ignored");
        TestArtifacts.SkipIfMissing();

        var missing = Path.Combine(TestScratch.FlatDir("win32stubs-env-child-"), "absent.so");
        var control = Task.Run(() => RunChild(null));
        var missingRun = Task.Run(() => RunChild(missing));
        var (controlOut, controlExit) = await control;
        var (missingOut, missingExit) = await missingRun;

        Assert.True(controlExit == 0, $"control (variable unset) failed, so the abort below proves nothing:\n{controlOut}");
        Assert.Contains("PASS", controlOut);

        Assert.True(missingExit == 134, $"expected SIGABRT (134) for a missing override file, got {missingExit}:\n{missingOut}");
        Assert.Contains($"{Var} is set to '{missing}' but that file does not exist", missingOut);
    }

    /// <summary>
    /// The variable naming an existing library with no Win32 exports (built with <c>cc</c>, so this is the
    /// only child that needs a compiler): the child loads it and aborts on the missing export. A missing
    /// compiler is a skip on a dev box and a FAILURE on CI, where the C# legs have one by construction
    /// (the prebuilt shim itself is built with it), so a leg cannot go green having run none of this.
    /// </summary>
    [SkippableFact]
    public void RealVariable_NamingALibraryWithoutWin32Exports_AbortsARunnerChild()
    {
        Skip.If(OperatingSystem.IsWindows(), "the Win32 shim exists only to fake Win32 on Linux/macOS; on Windows the variable is ignored");
        TestArtifacts.SkipIfMissing();
        // The abort this asserts is a LATER Win32 import (Types' WindowsLanguageHelper, LCIDToLocaleName)
        // dying after NavEnvironment's constructor has already fallen back to a skeleton. BC 29 reaches no
        // such import, so a library with no exports is reported as the constructor's fallback and the run
        // finishes — see AMissingFile for the half that stays loud on every BC build. Measured on BC 29.0
        // (this child exits 0 with `NavEnvironment ctor THREW`), not asserted for builds after it.
        Skip.If((BcArtifacts.EngineBuiltVersion()?.Major ?? 0) >= 29,
            "BC 29 reaches no Win32 import after the NavEnvironment fallback, so nothing aborts the child; see #5382");

        var dir = TestScratch.FlatDir("win32stubs-env-child-wrong-");
        Directory.CreateDirectory(dir);
        var wrong = Path.Combine(dir, "trivial.so");
        var cFile = Path.Combine(dir, "trivial.c");
        File.WriteAllText(cFile, "int dummy_export(void) { return 42; }\n");
        var buildError = TryBuildSharedLibrary(cFile, wrong);
        if (buildError is not null) SkipOrFailOnMissingCompiler(buildError, TestArtifacts.RunningOnCi);

        var (output, exit) = RunChild(wrong);
        Assert.True(exit == 134, $"expected SIGABRT (134) for an override with no Win32 exports, got {exit}:\n{output}");
        Assert.Contains("LCIDToLocaleName", output);
        Assert.DoesNotContain("PASS", output);
    }

    internal static void SkipOrFailOnMissingCompiler(string buildError, bool runningOnCi)
    {
        if (runningOnCi)
            Assert.Fail($"no working C compiler on a CI leg, where one is guaranteed by construction: {buildError}");
        throw new SkipException($"no working C compiler on this machine: {buildError}");
    }

    [Fact]
    public void AMissingCompiler_FailsOnCi_AndSkipsOnADevBox()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => SkipOrFailOnMissingCompiler("no cc", runningOnCi: true));
        var skip = Assert.Throws<SkipException>(() => SkipOrFailOnMissingCompiler("no cc", runningOnCi: false));
        Assert.Contains("no cc", skip.Message);
    }

    /// <summary>Null on success, else why <c>cc -shared</c> could not build it.</summary>
    private static string? TryBuildSharedLibrary(string cFile, string soFile)
    {
        try
        {
            using var cc = Process.Start(new ProcessStartInfo("cc", $"-shared -fPIC -o \"{soFile}\" \"{cFile}\"")
                { RedirectStandardError = true, UseShellExecute = false })!;
            cc.WaitForExit(10000);
            return cc.ExitCode == 0 ? null : $"`cc -shared` exited {cc.ExitCode}.";
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return $"`cc` could not be started ({e.Message}).";
        }
    }
}
