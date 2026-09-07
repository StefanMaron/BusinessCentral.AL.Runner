// OutputPaths — the parent directory of every --out/--output-junit/--coverage-out path
// exists before the run starts, or the run does not start (issue #2403).
//
// Every one of those three writers opened its file only AFTER the whole run had finished:
// File.WriteAllText for --out, XmlWriter.Create for the other two. A missing parent
// directory therefore surfaced as an unhandled DirectoryNotFoundException at the very last
// step — measured on the Microsoft Tests-SINGLESERVER bucket, 103 seconds and 834
// classified results thrown away for a mistyped directory, with exit 134 and a stack trace
// in place of a diagnostic.
//
// See docs/cli-output-paths.md for the full rationale, the measured failure, and why
// preflight and write-time recovery are both kept.

using System;
using System.IO;

namespace AlRunner.Infrastructure;

/// <summary>
/// Preflight and write-time handling for the CLI's file-producing flags.
/// </summary>
internal static class OutputPaths
{
    /// <summary>
    /// Ensure <paramref name="path"/>'s parent directory exists, creating it if needed.
    /// Returns null on success, or the diagnostic to print when the path cannot be made
    /// writable. <paramref name="flag"/> is the CLI flag that supplied the value, so the
    /// reader is told which of several output flags to fix.
    /// </summary>
    internal static string? TryPrepare(string flag, string path)
    {
        // A bare filename ("results.json") has no directory part to create, and
        // GetDirectoryName answers "" for it rather than null — treat both as "the
        // current directory, which exists by definition".
        string? parent;
        try
        {
            parent = Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            // GetFullPath is what rejects a structurally impossible value (embedded NUL,
            // an empty string). Returning the message keeps the caller's exit path the
            // same for "cannot be parsed" and "cannot be created".
            return BuildUnusableMessage(flag, path, ex.Message);
        }
        if (string.IsNullOrEmpty(parent)) return null;

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex)
        {
            return BuildUnusableMessage(flag, path, ex.Message);
        }

        // CreateDirectory succeeds when the path already exists AS A DIRECTORY, and throws
        // IOException when a FILE sits at that path — already covered above. What it does
        // not check is whether the directory can be written to, which is the other way a
        // late write fails. Probing that by creating a file would race and leave litter on
        // a path the caller may never write; the write-time guard below is the answer for
        // that case, so preflight deliberately stops here rather than pretending to more
        // certainty than it has.
        return null;
    }

    /// <summary>
    /// The wording for an output path the runner cannot write to, shared by preflight and
    /// the write-time guard so the two cannot drift into describing the same condition
    /// differently.
    /// </summary>
    internal static string BuildUnusableMessage(string flag, string path, string detail)
        // Framework exception messages already end in '.', and appending another produced
        // "...already exists.. Give --out ..." — trim so the sentence reads once.
        => $"{flag} '{path}' is not a usable output path: {detail.TrimEnd()?.TrimEnd('.')}. " +
           $"Give {flag} a path whose parent directory can be created and written to.";

    /// <summary>
    /// Run <paramref name="write"/>, having first ensured the parent directory exists.
    /// Returns null on success, or the diagnostic when the write failed.
    ///
    /// <para>This repeats preflight's directory creation on purpose. Preflight runs at
    /// argument-parse time and the write runs minutes later, so the directory can be gone
    /// by then — and, more mundanely, a caller reaching this method directly (the watchdog
    /// resume's carry file, --server) never went through preflight at all.</para>
    ///
    /// <para>Returning the message rather than throwing is what keeps the run's results:
    /// the caller prints it, records that this artifact was lost, and carries on to the
    /// remaining outputs and to the exit code the TESTS earned. A failure to write a report
    /// is not a test failure, and must not silently become one.</para>
    /// </summary>
    internal static string? TryWrite(string flag, string path, Action write)
    {
        var prepared = TryPrepare(flag, path);
        if (prepared != null) return prepared;
        try
        {
            write();
        }
        catch (Exception ex)
        {
            return BuildUnusableMessage(flag, path, ex.Message);
        }
        return null;
    }
}
