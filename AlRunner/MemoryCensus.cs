// MemoryCensus — env-gated per-test memory diagnostic. Disabled (and zero-cost — a single
// cached-bool check, see Log) unless AL_RUNNER_MEM_CENSUS=1 is set. When enabled, logs GC heap
// size, process RSS, loaded-assembly count, and the size of every candidate per-test retention
// structure after each test method runs. Retained as a tool: it is how the per-call
// _skeletonRootScope leak (MethodScopePatches) was pinned after the _skeletonSharedObjectContainer
// one — a flat gcTotalMB across tests is the definitive proof a per-test leak is gone. Leave it in.
using System.Reflection;

namespace AlRunner;

internal static class MemoryCensus
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("AL_RUNNER_MEM_CENSUS") == "1";

    private static int _counter;

    private static long ReadVmRssKb()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                        return kb;
                }
            }
        }
        catch { /* best-effort */ }
        return -1;
    }

    public static void Log(string codeunit, string method)
    {
        if (!Enabled) return;
        var n = System.Threading.Interlocked.Increment(ref _counter);

        // Force a blocking full GC so gcTotal reflects true LIVE retention, not
        // merely garbage awaiting collection (rules out "would plateau eventually").
        var gcTotal = GC.GetTotalMemory(forceFullCollection: true);
        var rssKb = ReadVmRssKb();
        var asmCount = AppDomain.CurrentDomain.GetAssemblies().Length;

        var (daSources, daTables) = Patches.RecordPatches.CensusDataAccessByTable();
        var mediaEntries = Patches.MediaSetPatches.CensusEntryCount();
        var linkEntries = Patches.RecordLinkPatches.CensusEntryCount();
        var storageEntries = Patches.TenantStoragePatches.CensusEntryCount();
        var sharedChildren = BcRuntime.CensusSharedObjectContainerChildCount();

        Console.Error.WriteLine(
            $"[mem-census] #{n} {codeunit}.{method} " +
            $"gcTotalMB={gcTotal / 1024.0 / 1024.0:F1} rssMB={rssKb / 1024.0:F1} " +
            $"asm={asmCount} daSources={daSources} daTables={daTables} " +
            $"media={mediaEntries} links={linkEntries} storage={storageEntries} " +
            $"sharedChildren={sharedChildren}");
    }
}
