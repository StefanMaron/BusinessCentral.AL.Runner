using System.Diagnostics;
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

    /// <summary>One runner child on a private copy of the fixture, with <paramref name="soValue"/> as the
    /// variable in the CHILD's environment only (<c>null</c> removes it, so an ambient value cannot
    /// leak in).</summary>
    private static (string Output, int Exit) RunChild(string? soValue)
    {
        var bundle = TestScratch.Dir("al-runner-win32-env-child");
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --no-cache",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (soValue is null) psi.Environment.Remove(Var); else psi.Environment[Var] = soValue;
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

    /// <summary>
    /// Three children at once (a runner run costs seconds): the variable absent (control: the fixture
    /// runs and passes, so any exit below is the variable's doing), pointing at a file that does not
    /// exist, and pointing at an existing library that has no Win32 exports. The last two must abort
    /// with the shim's own loud message, naming the variable and the path: a child that ignored the
    /// variable would exit 0 like the control.
    /// </summary>
    [SkippableFact]
    public async Task RealVariable_IsHonouredByARunnerChild_AndFailsLoudly()
    {
        TestArtifacts.SkipIfMissing();

        var dir = TestScratch.FlatDir("win32stubs-env-child-");
        Directory.CreateDirectory(dir);
        var missing = Path.Combine(dir, "absent.so");
        var wrong = Path.Combine(dir, "trivial.so");
        var cFile = Path.Combine(dir, "trivial.c");
        File.WriteAllText(cFile, "int dummy_export(void) { return 42; }\n");
        using (var cc = Process.Start(new ProcessStartInfo("cc", $"-shared -fPIC -o \"{wrong}\" \"{cFile}\"")
            { RedirectStandardError = true, UseShellExecute = false })!)
        {
            cc.WaitForExit(10000);
            TestArtifacts.SkipIf(cc.ExitCode != 0, $"no working C compiler on this machine: `cc -shared` exited {cc.ExitCode}.");
        }

        var control = Task.Run(() => RunChild(null));
        var missingRun = Task.Run(() => RunChild(missing));
        var wrongRun = Task.Run(() => RunChild(wrong));
        var (controlOut, controlExit) = await control;
        var (missingOut, missingExit) = await missingRun;
        var (wrongOut, wrongExit) = await wrongRun;

        Assert.True(controlExit == 0, $"control (variable unset) failed, so the two aborts below prove nothing:\n{controlOut}");
        Assert.Contains("PASS", controlOut);

        Assert.True(missingExit == 134, $"expected SIGABRT (134) for a missing override file, got {missingExit}:\n{missingOut}");
        Assert.Contains($"{Var} is set to '{missing}' but that file does not exist", missingOut);

        Assert.True(wrongExit == 134, $"expected SIGABRT (134) for an override with no Win32 exports, got {wrongExit}:\n{wrongOut}");
        Assert.Contains("LCIDToLocaleName", wrongOut);
        Assert.DoesNotContain("PASS", wrongOut);
    }
}
