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
    // `[layered]`, `[watch]` and `[provision]` are explicitly exempted — they are
    // user-facing output (layered source-build progress; watch-mode status; artifact
    // provisioning/download progress), not internal diagnostics.
    private static readonly Regex ComponentTag = new(@"^\[(?!(?:layered|watch|provision)\])[A-Za-z][A-Za-z0-9._+]*\]",
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
