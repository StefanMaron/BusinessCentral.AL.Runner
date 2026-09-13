// RunSeed — reproducible AL Random() (#2502). See docs/run-seed.md.
//
// Every run has a run seed: --seed N, else AL_RUNNER_SEED, else a fresh one. Resolving it
// writes AL_RUNNER_SEED into this process's environment, so every child the runner spawns
// (--jobs workers, watchdog resumes, re-execs) inherits the SAME run seed instead of choosing
// its own — a printed seed that only one of several processes used reproduces nothing.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

public static class RunSeed
{
    public const string EnvVar = "AL_RUNNER_SEED";

    private static readonly object Gate = new();
    private static int? _runSeed;
    private static PropertyInfo? _sessionRandom;

    // The test currently executing, for the no-argument Randomize() rewrite. Tests run one at a
    // time per process, so a plain static is enough.
    private static string? _currentTest;
    private static int _currentTestSeed;
    private static string? _warnedFor;

    /// <summary>Parses a seed as written on the command line or in the environment.</summary>
    public static bool TryParse(string? raw, out int seed)
        => int.TryParse(raw?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed);

    /// <summary>Fixes the run seed for this process and every child it spawns.</summary>
    public static void Set(int seed)
    {
        lock (Gate)
        {
            _runSeed = seed;
            Environment.SetEnvironmentVariable(EnvVar, seed.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The run seed: the one <see cref="Set"/> fixed, else <c>AL_RUNNER_SEED</c>, else a newly
    /// generated one (which is then written to the environment, see the header).
    /// </summary>
    public static int Value
    {
        get
        {
            lock (Gate)
            {
                if (_runSeed is { } s) return s;
                var raw = Environment.GetEnvironmentVariable(EnvVar);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (!TryParse(raw, out var fromEnv))
                        throw new InvalidOperationException(
                            $"{EnvVar}='{raw}' is not a whole number; set it to an integer seed or unset it.");
                    Set(fromEnv);
                    return fromEnv;
                }
                var generated = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                Set(generated);
                return generated;
            }
        }
    }

    /// <summary>
    /// The seed one test starts from. FNV-1a (32-bit) over the UTF-8 bytes of
    /// <c>"{runSeed}|{codeunitId}|{methodName}"</c>. Never <c>string.GetHashCode()</c>: that is
    /// randomized per process, so the same seed would give a different sequence every run.
    /// Changing this function changes every seed users have recorded — RunSeedTests pins it.
    /// </summary>
    public static int Derive(int runSeed, int codeunitId, string methodName)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{runSeed}|{codeunitId}|{methodName}");
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619;
        }
        return unchecked((int)hash);
    }

    /// <summary>
    /// Called before each [Test] method: installs <c>new Random(Derive(...))</c> as the skeleton
    /// session's RNG, so the test's Random() sequence depends only on the run seed and its own
    /// identity, not on how many Random() calls earlier tests made.
    /// </summary>
    public static void BeginTest(string codeunitTypeName, string methodName)
    {
        var codeunitId = TestExecutor.ParseAlObjectId(codeunitTypeName) ?? 0;
        var derived = Derive(Value, codeunitId, methodName);
        _currentTest = $"{codeunitTypeName}.{methodName}";
        _currentTestSeed = derived;

        var session = BcRuntime.SkeletonSession
            ?? throw new InvalidOperationException(
                "RunSeed.BeginTest: no skeleton session to seed; a [Test] cannot run before BC is booted.");
        var prop = _sessionRandom ??= session.GetType().GetProperty("Random",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RunSeed.BeginTest: NavSession.Random not found; the Ncl shape changed and Random() "
                + "can no longer be seeded per test.");
        prop.SetValue(session, new Random(derived));
    }

    /// <summary>
    /// Replaces the <c>new Random()</c> inside BC's no-argument <c>ALSystemNumeric.ALRandomize()</c>
    /// (NclCecilRewrite.Runtime.cs). Deliberately NOT observably equivalent to BC: BC reseeds
    /// from entropy, which no run can reproduce, so the runner reseeds from the current test's
    /// derived seed and says so on stderr once per test. <c>Randomize(seed)</c> is untouched.
    /// Owner's design decision on #2502.
    /// </summary>
    public static Random CreateRandomizeRandom()
    {
        var test = _currentTest;
        var seed = test == null ? Derive(Value, 0, string.Empty) : _currentTestSeed;
        if (test != null && !string.Equals(_warnedFor, test, StringComparison.Ordinal))
        {
            _warnedFor = test;
            Console.Error.WriteLine(
                $"warning: {test} called Randomize() without a seed; reseeded from the test's derived seed "
                + $"(run seed: {Value}) so the run stays reproducible. See docs/run-seed.md.");
        }
        return new Random(seed);
    }
}
