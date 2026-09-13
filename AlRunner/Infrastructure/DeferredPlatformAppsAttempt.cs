namespace AlRunner.Infrastructure;

/// <summary>
/// Issue #2232: re-runs this invocation as a child process that skips the platform-apps gate,
/// capturing its output instead of streaming it, so the caller can either replay a green attempt
/// or discard a non-green one and fall back to today's provisioning decision.
/// docs/limitations.md#platform-apps-deferral says why only a green attempt is trusted.
/// </summary>
internal static class DeferredPlatformAppsAttempt
{
    internal sealed record Result(int ExitCode, IReadOnlyList<(bool IsError, string Line)> Lines)
    {
        public void Replay()
        {
            foreach (var (isError, line) in Lines)
                (isError ? Console.Error : Console.Out).WriteLine(line);
        }
    }

    public static Result Run(IReadOnlyList<string> originalArgs)
    {
        var exe = Environment.ProcessPath ?? "dotnet";
        var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var viaDotnet = exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                     || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.Environment[ProvisioningCheck.DeferredPlatformAppsEnvVar] = "1";
        if (viaDotnet && asm != null) psi.ArgumentList.Add(asm);
        foreach (var a in originalArgs) psi.ArgumentList.Add(a);

        var lines = new List<(bool, string)>();
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add((false, e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add((true, e.Data)); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        lock (lines) return new Result(p.ExitCode, lines.ToList());
    }
}
