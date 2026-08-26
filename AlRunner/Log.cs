// Log — diagnostic-output filter. By default, lines tagged with a `[Component]`
// prefix (e.g. `[BcRuntime] ...`, `[Cecil] ...`) are suppressed so end users see
// only test results + summary. Set Verbose=true (via --verbose or env
// AL_RUNNER_VERBOSE=1) to surface all internal logs. Real errors that don't use
// the bracketed-component pattern (e.g. unhandled exception stacks) always pass
// through.
using System.Text.RegularExpressions;

namespace AlRunner;

public static class Log
{
    public static bool Verbose { get; set; } =
        Environment.GetEnvironmentVariable("AL_RUNNER_VERBOSE") == "1";

    // Matches `[Component]` or `[ComponentName]` at the start of a line — alphanumeric
    // tag in square brackets, NOT a numeric progress tag like `[1/3]`.
    // `[layered]`, `[watch]`, `[provision]`, `[bc]`, `[dep]` and `[expectations]` are
    // explicitly exempted — they are user-facing output (layered source-build progress;
    // watch-mode status; artifact provisioning/download progress; which BC version was
    // selected; dependency resolution warnings; whether the tests/expectations manifest
    // was found), not internal diagnostics.
    //
    // `[bc]` was NOT exempted until 2026-07-29, so the two lines naming the selected BC
    // version vanished at default verbosity. Measured: the same suite scores 1041P/35F/0E
    // on `--bc-version 28.1` and 996P/77F/3E on the default selection — a 42-test swing
    // decided silently. Which version ran is a RESULT, not a diagnostic.
    //
    // `[expectations]` was NOT exempted until #1984, so the notices Program.cs prints
    // when the tests/expectations manifest is (or is not) found were themselves silently
    // eaten by this exact filter — the very issue those notices exist to fix. Whether
    // out-of-scope/known-gap/divergence classification applied to a run is a RESULT
    // (it changes pass/fail counts), not a diagnostic.
    //
    // `[reexec]` was added for #2034: NclShadowRuntime's re-exec explanation lines were
    // tagged `[Cecil]` — the SAME class of bug as the `[bc]` swallow above — so a process
    // silently relaunching a child had no explanation on stderr at default verbosity.
    // These lines (why a second process is about to run, plus the genuinely unexpected
    // conditions hit while building the shadow dir) are operationally significant in the
    // same way BC-version selection is; the ~280 other `[Cecil]`-tagged per-method
    // rewrite diagnostics in NclCecilRewrite.cs are NOT retagged and stay suppressed —
    // that volume of internal detail is exactly what this filter exists to hide.
    //
    // `[dap]` was added for #1642: --dap's "listening on 127.0.0.1:<port>" line is the
    // ONLY signal a DAP client (or a human at a terminal) has that the runner is ready
    // to accept a connection — the exact same "readiness, not diagnostic" class as
    // `[bc]`/`[reexec]` above. Caught by DapClient's own test harness timing out waiting
    // for a line that was actually printed, just silently dropped before reaching
    // stdout — the same failure shape the `[bc]` comment above describes.
    private static readonly Regex ComponentTag =
        new(@"^\[(?!(?:layered|watch|provision|bc|dep|expectations|reexec|dap)\])[A-Za-z][A-Za-z0-9._+]*\]",
            RegexOptions.Compiled);

    public static void Install()
    {
        // Wrap both stdout and stderr. Bracket-tagged lines drop unless Verbose.
        Console.SetOut(new FilteredWriter(Console.Out));
        Console.SetError(new FilteredWriter(Console.Error));
    }

    private sealed class FilteredWriter : TextWriter
    {
        private readonly TextWriter _inner;
        public FilteredWriter(TextWriter inner) { _inner = inner; }
        public override System.Text.Encoding Encoding => _inner.Encoding;
        public override void WriteLine(string? value)
        {
            if (!Verbose && value != null && ComponentTag.IsMatch(value)) return;
            _inner.WriteLine(value);
        }
        public override void WriteLine() => _inner.WriteLine();
        public override void Write(string? value)
        {
            if (!Verbose && value != null && ComponentTag.IsMatch(value)) return;
            _inner.Write(value);
        }
        public override void Write(char value) => _inner.Write(value);
        public override void Flush() => _inner.Flush();
    }
}
