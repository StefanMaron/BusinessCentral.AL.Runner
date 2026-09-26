namespace AlRunner.Tests;

/// <summary>
/// How a test edits a source file while a `--watch` runner is watching it (#4702).
/// <c>File.WriteAllText</c> truncates first and writes second, so the watcher sees the
/// truncation as a change; a test host that stalls past <c>WatchSource.QuietMs</c> between the
/// two lets the runner run a whole cycle against an EMPTY file. Write a sibling temp file and
/// rename it over the target instead: one rename event, and the content is complete before it.
/// The temp name does not end in ".al", so the watcher ignores it.
/// </summary>
internal static class WatchEdit
{
    /// <param name="beforeCommit">Runs after the new content is on disk and before it replaces
    /// <paramref name="path"/> — the window a stalled writer would sit in. Tests only.</param>
    public static void Replace(string path, string content, Action? beforeCommit = null)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, content);
        beforeCommit?.Invoke();
        File.Move(tmp, path, overwrite: true);
    }
}
