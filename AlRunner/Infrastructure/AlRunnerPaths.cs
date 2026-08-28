namespace AlRunner.Infrastructure;

/// <summary>
/// Single source of truth for per-user, cross-platform base paths.
///
/// The runner historically resolved its caches from the POSIX <c>HOME</c> environment
/// variable, which is <b>null on Windows</b> — so the symbol/package-cache resolvers
/// silently yielded nothing there and the tool could not find (or provision) BC artifacts.
/// <see cref="UserHome"/> uses <see cref="Environment.SpecialFolder.UserProfile"/>, which
/// is <c>$HOME</c> on Linux/macOS and <c>C:\Users\&lt;name&gt;</c> on Windows — identical
/// behaviour on POSIX, correct on Windows. <see cref="BcArtifacts"/> already resolves its
/// artifacts root this way; this helper is where the remaining sites converge.
///
/// <para><b>Issue #2114:</b> when <c>$HOME</c> names a directory that does not exist,
/// <see cref="Environment.GetFolderPath"/> silently returns <c>""</c> instead of throwing.
/// Every caller that then does <c>Path.Combine(UserHome, ".local", "share", ...)</c> gets
/// back a bare RELATIVE path (<c>Path.Combine("", "a", "b")</c> == <c>"a/b"</c>, no leading
/// separator). That relative path passes every <c>File.Exists</c>/<c>Directory.Exists</c>
/// probe downstream by silently resolving against whatever the process's current working
/// directory happens to be — sometimes finding nothing (a confusing "not found" message
/// pointing at a path with no root), sometimes coincidentally finding something real
/// (e.g. the actual artifact cache, if launched from the real <c>$HOME</c> as CWD despite
/// the env var itself pointing elsewhere) — and it is only caught, if at all, deep inside
/// an API that demands an absolute path (<c>AssemblyLoadContext.LoadFromAssemblyPath</c>),
/// by which point the exception is unhandled and takes the process down with SIGABRT and a
/// core dump instead of a diagnostic. <see cref="UserHome"/> validates the resolved value
/// is non-empty and rooted before ever returning it, so every consumer inherits the loud
/// failure for free instead of quietly handing out a relative path.</para>
/// </summary>
public static class AlRunnerPaths
{
    /// <summary>The current user's home/profile directory, on every OS. Throws
    /// <see cref="InvalidOperationException"/> naming <c>$HOME</c>'s raw value when
    /// resolution does not yield an absolute path — see the class remarks for why a
    /// relative result must never be handed to a caller.</summary>
    public static string UserHome =>
        Validate(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE"));

    /// <summary>
    /// Rejects a non-rooted (empty or relative) resolved home directory with a loud,
    /// actionable diagnostic instead of returning it. Takes the raw <c>$HOME</c>/
    /// <c>%USERPROFILE%</c> value purely for the error message, so this validation logic
    /// is unit-testable without mutating the real process environment (which is shared,
    /// mutable, process-wide state — see <c>no-git-stash-with-worktrees.md</c>'s sibling
    /// concern about shared state races, same class of problem here across parallel tests).
    /// Internal + <c>InternalsVisibleTo("AlRunner.Tests")</c>.
    /// </summary>
    internal static string Validate(string resolvedHome, string? rawHomeEnvVar)
    {
        if (!string.IsNullOrEmpty(resolvedHome) && Path.IsPathRooted(resolvedHome))
            return resolvedHome;

        var rawDisplay = string.IsNullOrEmpty(rawHomeEnvVar) ? "<unset>" : rawHomeEnvVar;
        var why = string.IsNullOrEmpty(rawHomeEnvVar)
            ? "no HOME (or USERPROFILE) environment variable is set"
            : $"the directory named by HOME ('{rawHomeEnvVar}') likely does not exist";
        throw new InvalidOperationException(
            $"al-runner could not resolve an absolute home directory for the current user: " +
            $"{why}, so .NET's profile resolution returned '{resolvedHome}' instead of a real " +
            $"path. HOME is currently '{rawDisplay}'. Every al-runner cache (BC artifacts, " +
            $"symbols, compiled dependencies) is derived from this directory and requires an " +
            $"ABSOLUTE path — a relative one would silently resolve against whatever directory " +
            $"the process happens to be launched from. Resolve it ONE of these ways: " +
            $"(a) create the directory named by HOME; (b) point HOME at an existing absolute " +
            $"directory; (c) for the BC artifact root specifically, bypass this resolution " +
            $"entirely with --artifact-path <dir>.");
    }
}
