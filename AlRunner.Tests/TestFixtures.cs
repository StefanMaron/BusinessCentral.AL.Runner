using System.IO;

namespace AlRunner.Tests;

/// <summary>
/// Shared test-fixture helpers. Centralizes the <see cref="RepoRoot"/> constant that
/// every test file derived independently before this consolidation, so future relocations
/// of the test binary or repo layout only have to be updated in one place.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RepoRoot"/> resolves the repository root from the test binary's
/// <see cref="System.AppContext.BaseDirectory"/>. The path math
/// (<c>../../../..</c>) reflects the standard
/// <c>AlRunner.Tests/bin/&lt;Configuration&gt;/&lt;TFM&gt;/</c> output layout — bumping
/// the SDK or the project structure may require updating this constant.
/// </para>
/// <para>
/// <see cref="CliServer.RepoRoot"/> still has its own internal copy; that field is
/// referenced from the production-style helper (which starts the server before any
/// individual test runs) and is kept distinct so a hypothetical future split between
/// "test-only fixtures" and "test harness" stays clean. In practice the two values are
/// identical — both are built from the same <c>AppContext.BaseDirectory</c> + four
/// <c>..</c> hops.
/// </para>
/// </remarks>
internal static class TestFixtures
{
    /// <summary>
    /// Repository root directory (the parent of <c>AlRunner</c>/<c>AlRunner.Tests</c>).
    /// Resolved once per process from the test binary's location.
    /// </summary>
    public static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Combine <see cref="RepoRoot"/> with the given relative <paramref name="segments"/>.
    /// Equivalent to <c>Path.Combine(RepoRoot, segments...)</c> with one less ceremony at
    /// the call site. Named <c>Resolve</c> rather than <c>Path</c> to avoid the obvious
    /// type-name collision with <see cref="System.IO.Path"/> at call sites that pull both
    /// into scope.
    /// </summary>
    public static string Resolve(params string[] segments)
    {
        var combined = new string[segments.Length + 1];
        combined[0] = RepoRoot;
        for (var i = 0; i < segments.Length; i++)
            combined[i + 1] = segments[i];
        return System.IO.Path.Combine(combined);
    }
}
