using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace CallGraphSpike;

public static class Program
{
    public static int Main(string[] args)
    {
        // BC's parser is resolved out of a service-tier directory at run time rather than
        // copied next to the tool (the repo's Directory.Build.targets strips BC references).
        var tier = Environment.GetEnvironmentVariable("BC_SERVICE_TIER")
                   ?? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".local/share/al-runner/artifacts/28.1.49838.54424");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(tier, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args) => Cli.Run(args);
}
