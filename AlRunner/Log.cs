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
    // `[layered]`, `[watch]`, `[provision]`, `[bc]` and `[dep]` are explicitly exempted —
    // they are user-facing output (layered source-build progress; watch-mode status;
    // artifact provisioning/download progress; which BC version was selected; dependency
    // resolution warnings), not internal diagnostics.
    //
    // `[bc]` was NOT exempted until 2026-07-29, so the two lines naming the selected BC
    // version vanished at default verbosity. Measured: the same suite scores 1041P/35F/0E
    // on `--bc-version 28.1` and 996P/77F/3E on the default selection — a 42-test swing
    // decided silently. Which version ran is a RESULT, not a diagnostic.
    private static readonly Regex ComponentTag =
        new(@"^\[(?!(?:layered|watch|provision|bc|dep)\])[A-Za-z][A-Za-z0-9._+]*\]",
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
