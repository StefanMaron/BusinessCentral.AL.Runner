// LoadedAssemblyReferencesTests — #5414.
//
// WHAT HAPPENED: DependencyR2rChunkModuleIdentityTests loads R2R chunk assemblies from its own
// scratch cache and its Dispose deleted that cache. Every assembly it loaded stayed in
// AppDomain.GetAssemblies() with a Location naming a deleted file, so the next class to turn
// the loaded assemblies into compile references (CodeunitPublisherMetadataResolutionTests,
// CodeunitManualBindingOptionsTests) died in MetadataReference.CreateFromFile — all 10 rows,
// deterministically, in one test process, passing alone.
//
// These tests drive the reference builder with assemblies whose Location is the ONLY thing that
// varies (a subclass of Assembly answering a path), so they neither load anything into the test
// process nor depend on which class ran before. The cross-class reproduction is the filter in
// the PR body; LoadedAssemblyLifetimeGuardTests pins that no class can recreate it.

using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class LoadedAssemblyReferencesTests
{
    private sealed class FakeAssembly : Assembly
    {
        private readonly string _location;
        private readonly bool _dynamic;

        public FakeAssembly(string location, bool dynamic = false)
        {
            _location = location;
            _dynamic = dynamic;
        }

        public override string Location => _location;
        public override bool IsDynamic => _dynamic;
    }

    private static string[] Paths(IEnumerable<Microsoft.CodeAnalysis.MetadataReference> refs)
        => refs.Cast<Microsoft.CodeAnalysis.PortableExecutableReference>()
            .Select(r => r.FilePath!).ToArray();

    /// <summary>A real, readable assembly file in an owned scratch directory.</summary>
    private static string CopyOfARealDll()
    {
        var dir = TestScratch.Dir("al-runner-loaded-assembly-references");
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "Copy.dll");
        File.Copy(typeof(FactAttribute).Assembly.Location, copy);
        return copy;
    }

    [Fact]
    public void AnAssemblyWhoseFileWasDeleted_IsSkipped_AndTheOthersKeepTheirOrder()
    {
        var first = typeof(object).Assembly;
        var last = typeof(FactAttribute).Assembly;
        var gone = new FakeAssembly(Path.Combine(
            Path.GetTempPath(), "al-runner-never-existed-" + Guid.NewGuid().ToString("N"), "r2r-chunks", "000.dll"));

        var refs = LoadedAssemblyReferences.Build(new Assembly[] { first, gone, last });

        Assert.Equal(new[] { first.Location, last.Location }, Paths(refs));
    }

    [Fact]
    public void TheSameFile_IsReferenced_WhileItExists_AndSkippedOnceItIsDeleted()
    {
        // The control that makes the test above mean something: it is the file's existence that
        // decides, not the fake type or an assembly whose path merely looks scratch-like.
        var path = CopyOfARealDll();
        var asm = new FakeAssembly(path);

        Assert.Equal(new[] { path }, Paths(LoadedAssemblyReferences.Build(new Assembly[] { asm })));

        File.Delete(path);

        Assert.Empty(LoadedAssemblyReferences.Build(new Assembly[] { asm }));
    }

    [Fact]
    public void DynamicAndPathlessAssemblies_AreStillNotReferenced()
    {
        var real = typeof(object).Assembly;
        var refs = LoadedAssemblyReferences.Build(new Assembly[]
        {
            new FakeAssembly(real.Location, dynamic: true),
            new FakeAssembly(string.Empty),
            real,
        });

        Assert.Equal(new[] { real.Location }, Paths(refs));
    }
}
