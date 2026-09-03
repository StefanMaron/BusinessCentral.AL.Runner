// BcAssemblerParsePipelineTests — the two Roslyn-side passes BcAssembler.CompileCore now shares
// or parallelises (issue #2589).
//
// Neither change may alter what the compiler sees. Parallel parsing is only safe because results
// land in fixed array slots, so the tree ORDER handed to CSharpCompilation.Create is the
// sequential order whatever order the workers finish in — and Roslyn folds tree order into member
// ordering and diagnostic ordering, so that is the property to pin, not merely "every source was
// parsed". Sharing a MetadataReference is only safe because the cache key carries the file's
// stamp: a --bc-version switch or a rebuilt al-runner.dll points the same path at different bytes.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcAssemblerParsePipelineTests
{
    /// <summary>Enough sources to exceed BcAssembler's serial threshold and use every worker.</summary>
    private const int ManySources = 400;

    private static List<EmittedSource> NumberedSources(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new EmittedSource($"Object{i}", $"class C{i} {{ int F() => {i}; }}"))
            .ToList();

    // ---- parallel parse ---------------------------------------------------------

    /// <summary>
    /// The claim the whole change rests on: parsing in parallel yields the same trees, in the
    /// same positions, as parsing in order. Repeated, because a race that reorders results does
    /// not have to lose on the first attempt.
    /// </summary>
    [Fact]
    public void ParseInParallel_PlacesEveryTreeAtItsOwnSourceIndex()
    {
        var sources = NumberedSources(ManySources);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var trees = BcAssembler.ParseInParallel(sources);

            Assert.Equal(sources.Count, trees.Length);
            for (var i = 0; i < sources.Count; i++)
            {
                Assert.NotNull(trees[i]);
                Assert.Equal($"Object{i}.cs", trees[i].FilePath);
                Assert.Equal(sources[i].Code, trees[i].GetText().ToString());
            }
        }
    }

    /// <summary>
    /// Same trees as the sequential form, compared as text so a difference in parse options or in
    /// which source landed where shows up as an inequality rather than a reference mismatch.
    /// </summary>
    [Fact]
    public void ParseInParallel_MatchesSequentialParsingSourceForSource()
    {
        var sources = NumberedSources(ManySources);
        var sequential = sources
            .Select(s => CSharpSyntaxTree.ParseText(
                s.Code, BcAssembler.GeneratedParseOptionsForTests, path: s.Name + ".cs"))
            .ToList();

        var parallel = BcAssembler.ParseInParallel(sources);

        Assert.Equal(sequential.Count, parallel.Length);
        for (var i = 0; i < sequential.Count; i++)
            Assert.True(sequential[i].IsEquivalentTo(parallel[i]),
                $"tree {i} differs between the sequential and parallel parse");
    }

    /// <summary>
    /// The redirect pass is part of parsing, not a separate step a caller could forget: a source
    /// naming a service-tier member the skeleton runtime cannot serve must come back pointing at
    /// the shim. Uses the real redirect table's first entry so this cannot pass against a
    /// hand-written key that no longer exists.
    /// </summary>
    [Fact]
    public void ParseInParallel_AppliesThePolyfillRedirects()
    {
        var sources = NumberedSources(ManySources);
        sources[7] = new EmittedSource(
            "Redirected", "class R { void M() { NavRuntimeHelpers.ThrowIfWrongArgumentCount(1, null, \"x\"); } }");

        var text = BcAssembler.ParseInParallel(sources)[7].GetText().ToString();

        Assert.Contains("global::AlRunnerShim.NavRuntimeHelpersShim.ThrowIfWrongArgumentCount", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" NavRuntimeHelpers.ThrowIfWrongArgumentCount", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The small-input path is a different branch (serial, no threads started) and has to parse
    /// every source too — an off-by-one there would drop objects out of a small app group with no
    /// diagnostic, because a missing tree is simply a type the compilation never saw.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void ParseInParallel_ParsesEverySource_AtAndAroundTheSerialThreshold(int count)
    {
        var sources = NumberedSources(count);

        var trees = BcAssembler.ParseInParallel(sources);

        Assert.Equal(count, trees.Length);
        for (var i = 0; i < count; i++)
            Assert.Equal($"Object{i}.cs", trees[i].FilePath);
    }

    /// <summary>
    /// Doc comments are not parsed into structured trivia, and the language version is pinned
    /// rather than following whatever the Roslyn package's newest major happens to be. Both are
    /// deliberate: nothing downstream reads doc comments, and C# 14 turned <c>field</c> into a
    /// contextual keyword inside accessor bodies, which is exactly the kind of identifier an
    /// AL-to-C# emitter produces.
    /// </summary>
    [Fact]
    public void GeneratedParseOptions_PinTheLanguageVersionAndSkipDocumentationParsing()
    {
        var options = BcAssembler.GeneratedParseOptionsForTests;

        Assert.Equal(DocumentationMode.None, options.DocumentationMode);
        Assert.Equal(LanguageVersion.CSharp13, options.LanguageVersion);
        Assert.NotEqual(LanguageVersion.Default, options.LanguageVersion);
    }

    // ---- shared metadata references ---------------------------------------------

    private static string WriteAssembly(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.Copy(typeof(object).Assembly.Location, path, overwrite: true);
        return path;
    }

    /// <summary>The module name recorded INSIDE the PE the reference actually indexed — not the
    /// path it was loaded from, which is the same for both references here by construction.</summary>
    private static string ModuleNameOf(MetadataReference reference) =>
        ((AssemblyMetadata)((PortableExecutableReference)reference).GetMetadata()).GetModules()[0].Name;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-mdref", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SharedMetadataReferences_ReturnsTheSameInstanceForAnUnchangedFile()
    {
        var dir = NewTempDir();
        var path = WriteAssembly(dir, "unchanged.dll");

        var first = Assert.Single(BcAssembler.SharedMetadataReferences(new[] { path }));
        var second = Assert.Single(BcAssembler.SharedMetadataReferences(new[] { path }));

        Assert.Same(first, second);
    }

    /// <summary>
    /// The negative direction, and the reason the key is not the path alone: a
    /// <c>--bc-version</c> switch or a rebuilt <c>al-runner.dll</c> puts different bytes at the
    /// same path, and serving the cached metadata would compile AL against a version that is no
    /// longer on disk.
    /// </summary>
    [Fact]
    public void SharedMetadataReferences_ReturnsADifferentInstanceWhenTheFileAtThatPathChanges()
    {
        var dir = NewTempDir();
        var path = WriteAssembly(dir, "swapped.dll");
        var first = Assert.Single(BcAssembler.SharedMetadataReferences(new[] { path }));

        // A different assembly of a different length, at the same path.
        File.Copy(typeof(Xunit.FactAttribute).Assembly.Location, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        var second = Assert.Single(BcAssembler.SharedMetadataReferences(new[] { path }));

        Assert.NotSame(first, second);
        // NotSame alone would pass on a cache that simply never hits, so read the identity back
        // out: the second reference must describe the bytes that are on disk NOW.
        Assert.Equal(Path.GetFileName(typeof(object).Assembly.Location), ModuleNameOf(first));
        Assert.Equal(Path.GetFileName(typeof(Xunit.FactAttribute).Assembly.Location), ModuleNameOf(second));
    }

    /// <summary>
    /// Order and count are the compiler's reference-resolution order, so the cache may not
    /// reorder or deduplicate the list it is handed.
    /// </summary>
    [Fact]
    public void SharedMetadataReferences_PreservesTheOrderAndCountOfThePathsGiven()
    {
        var dir = NewTempDir();
        var a = WriteAssembly(dir, "a.dll");
        var b = WriteAssembly(dir, "b.dll");

        var refs = BcAssembler.SharedMetadataReferences(new[] { a, b, a });

        Assert.Equal(3, refs.Count);
        Assert.Same(refs[0], refs[2]);
        Assert.NotSame(refs[0], refs[1]);
        Assert.Equal(a, ((PortableExecutableReference)refs[0]).FilePath);
        Assert.Equal(b, ((PortableExecutableReference)refs[1]).FilePath);
    }
}
