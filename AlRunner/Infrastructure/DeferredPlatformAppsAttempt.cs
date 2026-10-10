namespace AlRunner.Infrastructure;

/// <summary>
/// Issue #2232: re-runs this invocation as a child process that skips the platform-apps gate,
/// capturing its output instead of streaming it, so the caller can either replay a green attempt
/// or discard a non-green one and fall back to today's provisioning decision.
/// docs/limitations.md#platform-apps-deferral says why only a green attempt is trusted.
/// </summary>
internal static class DeferredPlatformAppsAttempt
{
    /// <summary>
    /// Written by the attempt child, to both streams, at the point where it has caught up with what
    /// the parent had already printed (Program.cs, the #2232 child branch). Everything the child
    /// printed before it is a second copy of lines the parent printed itself; <see cref="Result.Replay"/>
    /// drops them (#5477). A control character so no real output line can equal it.
    /// </summary>
    public const string BeginMarker = "\u001e[al-runner] deferred-attempt-begin";

    public static void EmitBeginMarker(TextWriter stdout)
    {
        stdout.WriteLine(BeginMarker);
        stdout.Flush();
        Console.Error.WriteLine(BeginMarker);
        Console.Error.Flush();
    }

    internal sealed record Result(int ExitCode, IReadOnlyList<(bool IsError, string Line)> Lines)
    {
        /// <summary>
        /// Prints the attempt's output. <paramref name="stdout"/> is where the PARENT's real stdout is:
        /// under <c>--output-json</c> the parent has pointed <c>Console.Out</c> at stderr, so the
        /// default would put the child's JSON document on stderr and leave stdout empty (#5477).
        /// Per stream, lines before <see cref="BeginMarker"/> are dropped and the marker itself is
        /// never shown. A stream with no marker is replayed whole: duplicated lines are the cost of
        /// a child that did not reach the marker, and losing output would be worse.
        /// </summary>
        public void Replay(TextWriter? stdout = null, TextWriter? stderr = null)
        {
            stdout ??= Console.Out;
            stderr ??= Console.Error;
            var skipOut = FirstMarker(isError: false);
            var skipErr = FirstMarker(isError: true);
            int seenOut = 0, seenErr = 0;
            foreach (var (isError, line) in Lines)
            {
                var index = isError ? seenErr++ : seenOut++;
                if (index <= (isError ? skipErr : skipOut)) continue;
                (isError ? stderr : stdout).WriteLine(line);
            }
        }

        // Index, within its own stream, of that stream's first marker line; -1 when it has none.
        private int FirstMarker(bool isError)
        {
            var index = 0;
            foreach (var (lineIsError, line) in Lines)
            {
                if (lineIsError != isError) continue;
                if (line == BeginMarker) return index;
                index++;
            }
            return -1;
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
