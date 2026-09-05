// BcAppSymbolReadException — loud failure when a registered dependency .app's
// SymbolReference.json cannot be read or parsed to completion.
//
// Why this is fatal rather than a skipped .app (#2712): the runner answers "which fields
// does table X have" from these symbols. An .app whose table extensions failed to parse
// part-way through used to be presented as a shorter list of extensions, and the run then
// reported ordinary-looking test failures ("field 5912 cannot be found in the 'Customer'
// table") with an unchanged exit code. A run that cannot see a dependency's symbols cannot
// produce meaningful results, so it stops here instead — the same posture as
// DependencyLoadException for a dependency that fails to compile.
//
// See: .claude/rules/loud-failures.md.

namespace AlRunner.Infrastructure;

/// <summary>
/// Thrown when a dependency .app registered with <c>RecordPatches.AddBcAppPath</c> has a
/// SymbolReference.json the runner could not read to completion. Carries the .app path and
/// which symbol surface failed ("table symbols" / "table extensions"); the inner exception
/// is the original failure (an <see cref="OutOfMemoryException"/> in the reported case).
/// </summary>
public sealed class BcAppSymbolReadException : Exception
{
    public string AppPath { get; }
    public string Surface { get; }

    public BcAppSymbolReadException(string appPath, string surface, Exception inner)
        : base(BuildMessage(appPath, surface, inner), inner)
    {
        AppPath = appPath;
        Surface = surface;
    }

    // No leading `[tag]`: Log's default-verbosity filter drops lines that START with a
    // bracketed component tag, and this message must survive being printed on its own.
    private static string BuildMessage(string appPath, string surface, Exception inner)
        => $"symbol-read-fail {Path.GetFileName(appPath)}: could not read its {surface} from " +
           $"SymbolReference.json — {inner.GetType().Name}: {inner.Message}. Continuing would " +
           $"silently report wrong test results (the runner would treat every object this .app " +
           $"contributes as absent), so the run is aborted. Path: {appPath}";
}
