// ExecFailure — the one-line description of an app group that threw during its test run.
//
// The message used to start with the literal "<bundled>", which is the marker every
// BUNDLE-level failure uses (EMIT-FAIL, COMPILE-FAIL, EMIT-ZERO). This failure is not
// bundle-level: it happens inside a loop over the bundle's app groups, and it means THIS app
// group contributed zero results while its siblings ran normally. With the marker in place of
// the name, an app's entire test set can disappear from a run and the only line about it does
// not say whose. The sibling line in the same block already names the app group
// ("{rel}: TEST-TIMEOUT-ABORT: …"), so this was also inconsistent within one catch.
//
// A ReflectionTypeLoadException is unwrapped for the same reason it always was: its top line
// ("Unable to load one or more of the requested types") names nothing, and the LoaderExceptions
// underneath are where the real cause is — almost always a dependency whose runtime DLL was
// never built.

using System.Reflection;

namespace AlRunner.Infrastructure;

internal static class ExecFailure
{
    /// <summary>How many distinct loader reasons to quote. More than a handful is a wall of
    /// repetitions of the same missing dependency, and the message is one line in a summary.</summary>
    private const int MaxLoaderReasons = 5;

    /// <summary>
    /// The suite-error line for an app group whose test run threw.
    /// </summary>
    /// <param name="appGroup">The app group's assembly name — what the reader needs in order to
    /// know whose tests are missing from the run.</param>
    public static string Describe(string appGroup, Exception ex)
    {
        var headline = ex.Message.Split('\n')[0];

        var rtle = ex as ReflectionTypeLoadException
            ?? ex.InnerException as ReflectionTypeLoadException;
        if (rtle == null) return $"{appGroup}: EXEC-FAIL: {headline}";

        var reasons = rtle.LoaderExceptions
            .Where(e => e != null)
            .Select(e => e!.Message)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxLoaderReasons)
            .ToList();

        return reasons.Count == 0
            ? $"{appGroup}: EXEC-FAIL: {headline}"
            : $"{appGroup}: EXEC-FAIL: {headline} — {string.Join(" | ", reasons)}";
    }
}
