using System;
using System.IO;

namespace AlRunner.Infrastructure;

/// <summary>
/// Reads the process working directory without letting an unreadable one abort the run
/// (#3120). <c>Environment.CurrentDirectory</c> calls getcwd(2), which fails with ENOENT once
/// the directory has been removed (<c>cd d &amp;&amp; rm -rf d &amp;&amp; exec …</c>), and the
/// resulting FileNotFoundException escaped top-level statements as exit 134. Every read of the
/// working directory in the run path goes through <see cref="TryGet()"/>; callers treat
/// <c>null</c> as "no working directory" rather than as an error.
/// </summary>
public static class WorkingDirectory
{
    /// <summary>The working directory, or <c>null</c> when the OS cannot name it.</summary>
    public static string? TryGet() => TryGet(static () => Environment.CurrentDirectory);

    internal static string? TryGet(Func<string> read)
    {
        try { return read(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// <paramref name="absolutePath"/> relative to <paramref name="currentDirectory"/>, or the
    /// absolute path itself when there is no working directory to be relative to.
    /// </summary>
    public static string DisplayPath(string absolutePath, string? currentDirectory)
        => currentDirectory == null ? absolutePath : Path.GetRelativePath(currentDirectory, absolutePath);
}
