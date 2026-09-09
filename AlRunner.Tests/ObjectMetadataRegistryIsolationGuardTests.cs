// ObjectMetadataRegistryIsolationGuardTests — meta-guard for #3613.
//
// AlObjectMetadataRegistry is a process-wide static. Its ConcurrentDictionary makes each
// individual operation thread-safe, which is exactly what makes the hazard quiet: nothing
// ever throws. What breaks is a test that WRITES the registry and READS it back across a
// window, because xunit runs test collections in parallel (xunit.runner.json:
// parallelizeTestCollections=true, maxParallelThreads=4) and a class with no [Collection]
// gets its own parallelizable one.
//
// Both directions were measured on this branch, at 8bf9f508, by interleaving the two real
// classes' operations directly:
//   A. a concurrent Clear() between MetadataLoaderKindCoverageTests' Register and its lookup
//      turns a passing lookup into RunnerOutOfScopeException;
//   B. a concurrent Clear() makes QueryMetadataDocumentPredicateTests' Assert.True on
//      HasBcQueryMetadataDocument fail.
// Either presents as an unreproducible one-leg CI failure.
//
// #3613 filed this as latent, on the reading that MetadataLoaderKindCoverageTests was the
// only unserialized in-process user and so had nobody to race with. That was not so: the
// then-QueryMetadataFromBcDocumentTests carried an in-process [Fact] on the same registry,
// also unserialized, since #3608. Two parallel writers of one static is a live race, and the
// pair could only ever have passed by scheduling luck. Hence a guard rather than two
// attributes: the attributes fix today's pair, this fixes the class of mistake.
//
// ── How the detection works, and what it does NOT catch ──────────────────────────────────
//
// Unlike the parse statics (see ParserStaticsIsolationGuardTests), this registry is PUBLIC,
// so a violation is an ordinary compiled call and not reflection-by-name. The detector still
// scans SOURCE rather than IL, for one reason that decides it: a class that touches the
// registry only from a SUBPROCESS it spawns is not a violation at all — the registry it
// populates lives in the child process — and IL cannot tell those apart, because both spell
// the same call. Source can: the subprocess-driving classes name the registry in prose only.
//
// Caught:
//   * a real call to AlObjectMetadataRegistry.{Clear,Register,LoadSidecar,LoadFromJsonArray}(
//     — the mutating surface, which is what creates a window for another class.
// NOT caught:
//   * a read-only user (TryGet/Count/Snapshot). Deliberate: a reader alone cannot corrupt
//     another class's state, and it is a violation only if something else writes — which the
//     write side of this guard already forces into the serial collection.
//   * a helper in another file mutating on the class's behalf; the scan is per source file.
//   * a mutation through a computed/reflected member name.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public class ObjectMetadataRegistryIsolationGuardTests
{
    // ── the detector, as pure functions over (class name, [Collection] name, source text) ──

    // A real CALL to a mutating member. The trailing `(` is what keeps prose out: several
    // classes discuss AlObjectMetadataRegistry.Clear in a comment without ever calling it,
    // and RadObjectMetadataSnapshotReloadTests names it in a doc header as well as calling it.
    private static readonly Regex MutationRegex = new(
        @"\bAlObjectMetadataRegistry\s*\.\s*(?:Clear|Register|LoadSidecar|LoadFromJsonArray)\s*\(",
        RegexOptions.Compiled);

    internal static bool MutatesTheRegistry(string sourceText) => MutationRegex.IsMatch(sourceText);

    /// <summary>
    /// The collections a registry-mutating class may live in. What makes a collection safe is
    /// <c>DisableParallelization = true</c> on its <c>[CollectionDefinition]</c>, not its name:
    /// xUnit runs every non-parallelizable collection serially and one at a time, so a class in
    /// any of them has the static registry to itself.
    ///
    /// <para><see cref="BcEngineCollection"/> earns its place for the same reason it does in
    /// <see cref="ParserStaticsIsolationGuardTests"/>: a class may carry only ONE
    /// <c>[Collection]</c>, and a test driving the in-process BC engine needs
    /// <see cref="BcEngineFixture"/>, which only that collection supplies.
    /// RadObjectMetadataSnapshotReloadTests is the case — it clears the registry on every
    /// reload cycle AND needs the engine.</para>
    ///
    /// <para>Pinned by <see cref="EverySerialCollection_ReallyDisablesParallelization"/>, so
    /// this list cannot quietly start naming a collection that runs in parallel.</para>
    /// </summary>
    internal static readonly string[] SerialCollectionNames =
    {
        ObjectMetadataRegistrySerialCollection.Name,
        BcEngineCollection.Name,
    };

    internal static string? FindViolation(string className, string? collectionName, string sourceText)
    {
        if (!MutatesTheRegistry(sourceText)) return null;
        if (collectionName is not null && Array.IndexOf(SerialCollectionNames, collectionName) >= 0)
            return null;

        return $"{className} mutates the process-wide AlObjectMetadataRegistry in-process " +
               "(Clear/Register/LoadSidecar/LoadFromJsonArray) but is " +
               (collectionName is null
                   ? "in no xunit collection"
                   : $"in collection \"{collectionName}\"") +
               ". Add [Collection(ObjectMetadataRegistrySerialCollection.Name)] to it — or " +
               "[Collection(BcEngineCollection.Name)] if it also needs the in-process BC " +
               "engine fixture. The registry is a static that xunit's parallel collections " +
               "share, so a concurrent Clear() can land between your Register and your read " +
               "(#3613): the lookup then throws RunnerOutOfScopeException, or another class's " +
               "assertion flips, as a one-leg CI failure nobody can reproduce. If this class " +
               "only touches the registry from a SUBPROCESS it spawns, it is not a violation " +
               "— say so without spelling a call, since the child process has its own registry.";
    }

    /// <summary>
    /// The allowance above rests entirely on <c>DisableParallelization = true</c>. Read the flag
    /// off each collection's own <c>[CollectionDefinition]</c> rather than trusting the comment,
    /// so flipping it fails here instead of silently reopening the race for every class that
    /// joined on this guard's say-so.
    /// </summary>
    [Fact]
    public void EverySerialCollection_ReallyDisablesParallelization()
    {
        // Read the attribute as DATA: xunit's CollectionDefinitionAttribute takes the name as a
        // constructor argument and exposes no Name property, so GetCustomAttribute<T>() can tell
        // us DisableParallelization but not which collection it belongs to.
        var definitions = new Dictionary<string, (string TypeName, bool DisableParallelization)>(StringComparer.Ordinal);
        foreach (var type in typeof(ObjectMetadataRegistryIsolationGuardTests).Assembly.GetTypes())
        {
            foreach (var data in CustomAttributeData.GetCustomAttributes(type))
            {
                if (data.AttributeType != typeof(CollectionDefinitionAttribute)) continue;
                if (data.ConstructorArguments.Count != 1) continue;
                if (data.ConstructorArguments[0].Value is not string name) continue;
                var disabled = data.NamedArguments
                    .Any(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                              && a.TypedValue.Value is true);
                definitions[name] = (type.Name, disabled);
            }
        }

        foreach (var name in SerialCollectionNames)
        {
            Assert.True(definitions.TryGetValue(name, out var definition),
                $"SerialCollectionNames lists \"{name}\", but no [CollectionDefinition] in this " +
                "assembly declares it. The guard cannot vouch for a collection it cannot find.");
            Assert.True(definition.DisableParallelization,
                $"collection \"{name}\" ({definition.TypeName}) is accepted by this guard, but its " +
                "[CollectionDefinition] no longer sets DisableParallelization = true. Either " +
                "restore that, or drop it from SerialCollectionNames — a parallel collection " +
                "gives a class no exclusive access to the static registry, which is the whole " +
                "property this guard requires (#3613).");
        }
    }

    // ── RED: the detector catches a real violation ─────────────────────────────────────────

    // Positive control, spelled as MetadataLoaderKindCoverageTests actually was at 738ec1cf.
    // Without this, every assertion below would still pass if FindViolation were hard-wired to
    // return null — i.e. the whole guard would be noise.
    private const string ViolatingClassSource = """
        public sealed class HypotheticalNewRegistryTests : IDisposable
        {
            public HypotheticalNewRegistryTests() => AlObjectMetadataRegistry.Clear();
            public void Dispose() => AlObjectMetadataRegistry.Clear();

            [Fact]
            public void ReadsBackWhatItRegistered()
            {
                AlObjectMetadataRegistry.Register("Codeunit", 88010, "Thing", "<Codeunit/>");
                Assert.True(AlObjectMetadataRegistry.TryGet("Codeunit", 88010, out _));
            }
        }
        """;

    [Fact]
    public void Detector_ClassMutatingTheRegistryInNoCollection_IsReported()
    {
        var violation = FindViolation("HypotheticalNewRegistryTests", null, ViolatingClassSource);

        Assert.NotNull(violation);
        Assert.Contains("HypotheticalNewRegistryTests", violation);
        Assert.Contains("in no xunit collection", violation);
        Assert.Contains("[Collection(ObjectMetadataRegistrySerialCollection.Name)]", violation);
    }

    [Fact]
    public void Detector_ClassMutatingTheRegistryInAParallelCollection_IsReported()
    {
        Assert.DoesNotContain("some-other-collection", SerialCollectionNames);

        var violation = FindViolation(
            "HypotheticalNewRegistryTests", "some-other-collection", ViolatingClassSource);

        Assert.NotNull(violation);
        Assert.Contains("some-other-collection", violation);
    }

    [Theory]
    [InlineData(ObjectMetadataRegistrySerialCollectionNameLiteral)]
    [InlineData(BcEngineCollectionNameLiteral)]
    public void Detector_ClassMutatingTheRegistryInASerialCollection_IsNotReported(string collection)
        => Assert.Null(FindViolation("HypotheticalNewRegistryTests", collection, ViolatingClassSource));

    // [InlineData] needs compile-time constants; these two pin that the literals the theory
    // uses are the very names the collections declare, so a rename cannot leave the theory
    // silently asserting about collections that no longer exist.
    private const string ObjectMetadataRegistrySerialCollectionNameLiteral = "object-metadata-registry";
    private const string BcEngineCollectionNameLiteral = "bc-engine-serial";

    [Fact]
    public void TheInlineDataLiterals_AreTheRealCollectionNames()
    {
        Assert.Equal(ObjectMetadataRegistrySerialCollection.Name, ObjectMetadataRegistrySerialCollectionNameLiteral);
        Assert.Equal(BcEngineCollection.Name, BcEngineCollectionNameLiteral);
    }

    [Theory]
    [InlineData("Clear")]
    [InlineData("Register")]
    [InlineData("LoadSidecar")]
    [InlineData("LoadFromJsonArray")]
    public void Detector_RecognisesEveryMutatingMember(string member)
    {
        Assert.True(MutatesTheRegistry($"AlObjectMetadataRegistry.{member}(x);"),
            $"the detector must recognise a call to AlObjectMetadataRegistry.{member}");

        // …and the bare name in prose must still not trip it.
        Assert.False(MutatesTheRegistry($"// AlObjectMetadataRegistry.{member} wipes the registry"));
    }

    // The mutating surface is read off the production type, so a member added to
    // AlObjectMetadataRegistry that writes _byKey and is not in the regex fails HERE rather
    // than silently widening the hole the guard is supposed to close.
    [Fact]
    public void TheDetectorCoversEveryPublicMutatingMemberOfTheRegistry()
    {
        var mutators = typeof(AlObjectMetadataRegistry)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            // Read-only members cannot corrupt another class's state; see the header.
            .Where(n => n is not ("TryGet" or "TryGetByName" or "TryGetByKey" or "KeyFor"
                                 or "Snapshot" or "SaveSidecar" or "get_Count" or "get_Keys"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(mutators);
        foreach (var name in mutators)
        {
            Assert.True(MutatesTheRegistry($"AlObjectMetadataRegistry.{name}(x);"),
                $"AlObjectMetadataRegistry.{name} writes the static registry but the guard's " +
                "detector does not recognise a call to it — add it to MutationRegex, or, if it " +
                "is read-only, to the exclusion list in this test with the reason.");
        }
    }

    // Negative direction: an over-eager detector that flagged every test class would be useless
    // and would "pass" the assembly-wide assertion for the wrong reason. Modelled on
    // ObjectMetadataCaptureTests, which drives the runner as a SUBPROCESS: the registry it
    // populates lives in the child process, so it is genuinely not a violation.
    [Fact]
    public void Detector_SubprocessDrivingClassThatOnlyNamesTheRegistryInProse_IsNotReported()
    {
        const string innocentSource = """
            /// The compiler's own AlObjectMetadataRegistry keeps every kind; this spawns the
            /// runner and reads the trace, so the registry it fills is the CHILD's.
            public class ObjectMetadataCaptureLikeTests
            {
                [SkippableFact]
                public void EveryKindIsCaptured()
                {
                    var output = RunRunner(bundle);
                    Assert.Contains("[object-metadata] registered Codeunit 70660", output);
                }
            }
            """;

        Assert.Null(FindViolation("ObjectMetadataCaptureLikeTests", null, innocentSource));
        Assert.False(MutatesTheRegistry(innocentSource));
    }

    [Fact]
    public void Detector_OrdinaryTestClass_IsNotReported()
    {
        const string ordinarySource = """
            public class ErrorClassifierLikeTests
            {
                [Fact]
                public void ClassifiesACompileError()
                {
                    Assert.Equal(ErrorKind.Compile, Classify("AL0118: unknown type"));
                }
            }
            """;

        Assert.Null(FindViolation("ErrorClassifierLikeTests", null, ordinarySource));
    }

    // This file necessarily CONTAINS the call pattern — the fixtures above spell out exactly
    // what the detector must recognise — but never mutates the registry at runtime, so
    // serialising it would be pointless. TheDetectorFiresOnThisOwnFile_WhichIsWhyItIsExcluded
    // pins that the exclusion stays justified rather than becoming a silent hole.
    private static readonly string[] SelfExcludedClasses =
    {
        nameof(ObjectMetadataRegistryIsolationGuardTests),
    };

    // ── GREEN: the real assembly is clean, and the detector really fires on real source ────

    [Fact]
    public void EveryTestClassMutatingTheRegistry_IsInASerialCollection()
    {
        var sources = TestSourceByTypeName();
        var violations = new List<string>();

        foreach (var type in TestClasses())
        {
            if (SelfExcludedClasses.Contains(type.Name)) continue;
            if (!sources.TryGetValue(type.Name, out var source))
            {
                throw new Xunit.Sdk.XunitException(
                    $"no source file under {TestSourceDirectory()} declares test class " +
                    $"{type.Name}. This guard scans source, and a guard that silently skips " +
                    "classes is worse than no guard.");
            }

            var violation = FindViolation(type.Name, CollectionNameOf(type), source.Text);
            if (violation is not null) violations.Add($"{source.Path}: {violation}");
        }

        Assert.True(violations.Count == 0, string.Join("\n\n", violations));
    }

    // Proves the assertion above is not passing because the detector never fires. These three
    // classes DO mutate the registry in-process today; if the detector stopped recognising the
    // pattern, this fails instead of the guard quietly going green forever.
    [Theory]
    [InlineData(nameof(MetadataLoaderKindCoverageTests))]
    [InlineData(nameof(ObjectMetadataRegistryTests))]
    [InlineData(nameof(QueryMetadataDocumentPredicateTests))]
    public void TheKnownInProcessRegistryClasses_AreDetected_AndAreAttributed(string className)
    {
        var sources = TestSourceByTypeName();
        Assert.True(sources.ContainsKey(className), $"source for {className} was not found");

        Assert.True(MutatesTheRegistry(sources[className].Text),
            $"{className} mutates the registry in-process, so the detector must flag it — if it " +
            "does not, the assembly-wide guard is vacuous");

        var type = TestClasses().Single(t => t.Name == className);
        Assert.Equal(ObjectMetadataRegistrySerialCollection.Name, CollectionNameOf(type));
    }

    // RadObjectMetadataSnapshotReloadTests is the BcEngineCollection case: it clears the
    // registry AND needs the engine fixture, so it must be detected and allowed there.
    [Fact]
    public void TheEngineDrivenRegistryClass_IsDetected_AndSitsInTheEngineCollection()
    {
        var sources = TestSourceByTypeName();
        var name = nameof(RadObjectMetadataSnapshotReloadTests);

        Assert.True(MutatesTheRegistry(sources[name].Text));
        Assert.Equal(BcEngineCollection.Name,
            CollectionNameOf(TestClasses().Single(t => t.Name == name)));
    }

    // The other half of "not vacuous": the detector must also say NO a lot. If it flagged every
    // test class, the assembly-wide assertion would only be measuring how diligent we were about
    // sprinkling the attribute everywhere.
    [Fact]
    public void MostTestClasses_AreNotDetectedAsMutatingTheRegistry()
    {
        var sources = TestSourceByTypeName();
        var testClasses = TestClasses().Select(t => t.Name).ToArray();
        Assert.True(testClasses.Length >= 20,
            $"expected the test assembly to hold at least 20 test classes, found {testClasses.Length}");

        var untouched = testClasses
            .Where(n => sources.TryGetValue(n, out var s) && !MutatesTheRegistry(s.Text))
            .ToArray();

        Assert.True(untouched.Length >= 20,
            $"only {untouched.Length} of {testClasses.Length} test classes are detected as NOT " +
            "mutating the registry — the detector is over-eager and the guard proves nothing");
    }

    [Fact]
    public void TheDetectorFiresOnThisOwnFile_WhichIsWhyItIsExcluded()
    {
        var sources = TestSourceByTypeName();
        Assert.True(MutatesTheRegistry(sources[nameof(ObjectMetadataRegistryIsolationGuardTests)].Text));
        Assert.Equal(new[] { nameof(ObjectMetadataRegistryIsolationGuardTests) }, SelfExcludedClasses);
    }

    // ── source + reflection plumbing (fails loudly rather than finding nothing to check) ───

    private sealed record TestSource(string Path, string Text);

    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static string TestSourceDirectory()
    {
        // First choice: the compile-time path of this very file. In practice never taken —
        // Directory.Build.props sets PathMap on every dev/CI build, which rewrites the value
        // baked into [CallerFilePath], so the File.Exists probe below fails and the walk-up
        // runs. Kept because the probe makes the choice safe either way, and the release-pack
        // build (no PathMap) does produce a real absolute path here.
        var compileTimeDir = Path.GetDirectoryName(ThisFilePath());
        if (compileTimeDir is not null &&
            File.Exists(Path.Combine(compileTimeDir, "AlRunner.Tests.csproj")))
        {
            return compileTimeDir;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AlRunner.Tests.csproj"))) return dir.FullName;
            var nested = Path.Combine(dir.FullName, "AlRunner.Tests", "AlRunner.Tests.csproj");
            if (File.Exists(nested)) return Path.GetDirectoryName(nested)!;
        }

        throw new Xunit.Sdk.XunitException(
            "the AlRunner.Tests source directory could not be located (compile-time path was " +
            $"'{ThisFilePath()}', assembly base was '{AppContext.BaseDirectory}'). This guard " +
            "scans source, so with no source it would check nothing and read as passing — " +
            "failing instead.");
    }

    // Matches a type declaration at the start of a line, optionally preceded by attributes and
    // modifiers. Anchoring at line start keeps prose in a `//` comment from registering.
    private static readonly Regex TypeDeclRegex = new(
        @"^[ \t]*(?:\[[^\]]*\][ \t]*)*(?:public|internal|private|protected|file)?[ \t]*" +
        @"(?:(?:sealed|abstract|static|partial|unsafe|readonly|ref)[ \t]+)*" +
        @"(?:class|record|struct)[ \t]+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static Dictionary<string, TestSource> TestSourceByTypeName()
    {
        var root = TestSourceDirectory();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToArray();

        if (files.Length == 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"no .cs files found under '{root}' — this guard would check nothing and still " +
                "go green. Failing loudly instead.");
        }

        var map = new Dictionary<string, TestSource>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            foreach (Match m in TypeDeclRegex.Matches(text))
            {
                map[m.Groups["name"].Value] = new TestSource(path, text);
            }
        }
        return map;
    }

    private static IEnumerable<Type> TestClasses() =>
        typeof(ObjectMetadataRegistryIsolationGuardTests).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    // xunit 2.x's CollectionAttribute exposes its name only through the constructor argument.
    private static string? CollectionNameOf(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var attr in CustomAttributeData.GetCustomAttributes(t))
            {
                if (attr.AttributeType.FullName != "Xunit.CollectionAttribute") continue;
                if (attr.ConstructorArguments.Count > 0 &&
                    attr.ConstructorArguments[0].Value is string name)
                {
                    return name;
                }
            }
        }
        return null;
    }
}
