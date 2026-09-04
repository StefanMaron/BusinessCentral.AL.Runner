// ParallelFanOutFlagDriftTests — keep ParallelFanOut.ValueTakingFlags in step with Program.cs.
//
// The failure this prevents is silent. If Program.cs gains a flag that consumes the next
// argument and ValueTakingFlags does not learn about it, the fan-out reads that VALUE as a
// positional. When the value happens to name a bundle in the run it is dropped from the worker's
// command line; when it does not, it is passed through as an extra bundle path. Either way the
// workers run something other than what the user asked for, and the aggregate still prints a
// confident total.
//
// Parsing the source rather than asserting a hand-copied list: a hand-copied list is exactly
// what goes stale.

using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutFlagDriftTests
{
    private static string ProgramSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(dir, "AlRunner", "Program.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate AlRunner/Program.cs from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryValueTakingFlagInProgramCs_IsDeclaredToTheFanOut()
    {
        var src = ProgramSource();

        // The parser's own shape for "this flag takes the next argument".
        var declared = Regex.Matches(src, @"args\[i\] == ""(--[a-z0-9-]+)""[^;\r\n]*i \+ 1 < args\.Length")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(declared); // the regex itself must still match something

        var missing = declared.Where(f => !ParallelFanOut.ValueTakingFlags.Contains(f)).ToList();

        Assert.True(missing.Count == 0,
            "Program.cs parses these flags as taking a value, but ParallelFanOut.ValueTakingFlags "
            + "does not list them, so --jobs would treat each value as a bundle path: "
            + string.Join(", ", missing));
    }

    /// <summary>The other direction: a stale entry for a flag that no longer takes a value would
    /// make the fan-out swallow the NEXT real argument. Allows --jobs, which the fan-out owns
    /// and Program.cs need not parse the same way.</summary>
    [Fact]
    public void TheFanOutDoesNotClaimFlagsProgramCsNoLongerTakesAValueFor()
    {
        var src = ProgramSource();

        var stale = ParallelFanOut.ValueTakingFlags
            .Where(f => f != "--jobs")
            .Where(f => !Regex.IsMatch(src, @"args\[i\] == """ + Regex.Escape(f) + @"""[^;\r\n]*i \+ 1 < args\.Length"))
            .ToList();

        Assert.True(stale.Count == 0,
            "ParallelFanOut.ValueTakingFlags lists flags Program.cs no longer parses as taking a "
            + "value, so --jobs would swallow the argument after each: " + string.Join(", ", stale));
    }
}
