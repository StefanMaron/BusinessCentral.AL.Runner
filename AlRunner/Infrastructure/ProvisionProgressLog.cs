namespace AlRunner.Infrastructure;

/// <summary>
/// #4564: on a continuing run, a download set reports one start line (printed by the caller)
/// and the downloader's own <c>Downloaded ...</c> summary, not one line per file. Under
/// --verbose, or for the <c>provision</c> subcommand whose report IS its output, callers pass
/// the sink through unchanged. Failures stay loud: an <c>Error</c> line and everything after
/// it, and every warning or retry, always reach the sink.
/// </summary>
internal static class ProvisionProgressLog
{
    public static Action<string> Condense(Action<string> sink, bool verbose)
    {
        if (verbose) return sink;
        var failing = false;
        return message =>
        {
            if (IsError(message)) failing = true;
            if (failing || IsWarning(message) || IsSetSummary(message))
                sink(message);
        };
    }

    // The downloader's failure lines, which continue on indented lines naming the URL and
    // the fix (ArtifactDownloader.TryHeadContentLength); those follow an Error line, so
    // everything after one is kept.
    internal static bool IsError(string message)
        => message.TrimStart().StartsWith("Error", StringComparison.Ordinal);

    internal static bool IsWarning(string message)
    {
        var t = message.TrimStart();
        return t.StartsWith("WARNING", StringComparison.Ordinal)
            || t.StartsWith("Warning", StringComparison.Ordinal)
            || t.StartsWith("Retrying", StringComparison.Ordinal);
    }

    // One per set: "Downloaded 501 DLLs (343 MB) to ...", "Downloaded 108 test .app file(s)
    // (20 MB) to ...", "Downloaded 6 app(s) (116 MB total) to ...".
    internal static bool IsSetSummary(string message)
        => message.StartsWith("Downloaded ", StringComparison.Ordinal);
}
