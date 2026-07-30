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
/// </summary>
public static class AlRunnerPaths
{
    /// <summary>The current user's home/profile directory, on every OS.</summary>
    public static string UserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
