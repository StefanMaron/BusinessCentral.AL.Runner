namespace AlRunner;

internal static class PerfTrace
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("AL_RUNNER_PERF") == "1";

    internal static void Log(string message)
    {
        if (Enabled)
            Console.Error.WriteLine($"PERF {message}");
    }
}
