// #4892: xUnit discovery calls GetExportedTypes on this assembly, which loads every public type.
// A plain `dotnet test` has only Ncl.dll of BC's assemblies in bin (Directory.Build.targets strips
// the rest), so a public type whose load needs Nav.Types — a static field of a BC value type,
// or a BC base type — crashed discovery before any test ran.
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace AlRunner.Tests;

public sealed class PlainDiscoveryLoadsNoAbsentAssemblyTests
{
    /// <summary>
    /// Resolves what a plain `dotnet test` host can: the framework, and files in the test bin.
    /// Anything else throws, as it does there, even though this process may have it loaded.
    /// </summary>
    private sealed class BinOnlyContext : AssemblyLoadContext
    {
        private readonly string _bin;
        private readonly HashSet<string> _framework;

        public BinOnlyContext(string bin) : base("plain-discovery-probe", isCollectible: true)
        {
            _bin = bin;
            var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            _framework = new HashSet<string>(
                tpa.Where(p => p.StartsWith(runtimeDir, StringComparison.Ordinal)).Select(Path.GetFileNameWithoutExtension)!,
                StringComparer.OrdinalIgnoreCase);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (_framework.Contains(name.Name!)) return null;
            var path = Path.Combine(_bin, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
            throw new FileNotFoundException($"'{name.Name}' is neither in the framework nor in the test bin", name.Name);
        }
    }

    [Fact]
    public void EveryPublicType_LoadsWithOnlyTheFrameworkAndTheTestBin()
    {
        var bin = AppContext.BaseDirectory;
        var testAssembly = Path.Combine(bin, "AlRunner.Tests.dll");
        Assert.True(File.Exists(testAssembly), $"cannot see the test assembly at '{testAssembly}'");
        // The rule this pins only means something while bin lacks Nav.Types; if a build ever
        // copies it, the probe below can no longer fail and must say so rather than pass.
        Assert.False(File.Exists(Path.Combine(bin, "Microsoft.Dynamics.Nav.Types.dll")),
            "Microsoft.Dynamics.Nav.Types.dll is in the test bin, so this probe measures nothing");

        var context = new BinOnlyContext(bin);
        try
        {
            var asm = context.LoadFromAssemblyPath(testAssembly);
            var exported = asm.GetExportedTypes();
            Assert.Contains(exported, t => t.Name == nameof(PlainDiscoveryLoadsNoAbsentAssemblyTests));
        }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or FileLoadException)
        {
            Assert.Fail(
                "A public type in AlRunner.Tests cannot load without an assembly plain `dotnet test` does not " +
                "have, so xUnit discovery fails before any test runs (#4892). Usual cause: a static field typed " +
                "as a BC value type (e.g. ApplicationObjectId) — make it a property. " + ex.Message);
        }
        finally
        {
            context.Unload();
        }
    }
}
