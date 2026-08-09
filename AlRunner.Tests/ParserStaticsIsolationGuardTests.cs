// ParserStaticsIsolationGuardTests — meta-guard for #1712.
//
// The AL source parsers on RecordPatches publish their results into PROCESS-WIDE static
// dictionaries (`_parsedTables`, `_parsedPages`, `_parsedReports`, …). A test that drives a
// parser and then reads the result back out of one of those dictionaries is reading shared
// mutable state across a window, and xunit runs test COLLECTIONS in parallel — so anything
// concurrent that repopulates or clears them (RecordPatches.ResetForReload, an in-process
// bundle load, a neighbouring parser test) can land between the write and the read.
//
// That is exactly what #1696 hit: AlSourceParserSyntaxTreeTests.Caption_WithTrailingComment-
// Property failed as "table 61896 was not parsed at all" on four of five CI legs while passing
// on the fifth, passing in isolation, and passing in two full local suite runs. It presented as
// a cross-BC-version parser difference and was not one — the parse was verified correct against
// the failing leg's own Microsoft.Dynamics.Nav.CodeAnalysis. The only difference was scheduling:
// CI runners have a different core count, so xunit picks a different degree of parallelism.
//
// RecordPatchesSerialCollection fixes it, but only for classes that REMEMBER to join it. This
// file is the enforcement: it finds test classes that reach the parse statics and fails if any
// of them is missing [Collection(RecordPatchesSerialCollection.Name)].
//
// #1712 also lists the real fix — make the parse entry points RETURN their result instead of
// publishing into a dictionary, with the registration path doing the storing. That removes the
// class of bug outright; this guard only makes forgetting the workaround loud. It is a stopgap.
//
// ── How the detection works, and what it does NOT catch ──────────────────────────────────────
//
// The offending pattern is reflection BY NAME: `GetField("_parsedTables", NonPublic|Static)` /
// `GetMethod("TryParseTableFile", NonPublic|Static)`. Those names only exist in the test
// assembly as string literals — there is no IL-level reference to the private field or method to
// walk, because the members are private and the call goes through System.Reflection. So the
// detector scans test SOURCE for the quoted marker literals, and uses reflection over the test
// assembly only for the part reflection is exact about: which types are test classes and what
// [Collection] they carry.
//
// Caught:
//   * "_parsed…" / "TryParse…File" as a quoted string literal (the reflection-by-name pattern),
//   * a direct `RecordPatches.ResetForReload` call (clears every parse dictionary).
// NOT caught:
//   * a class that reaches the statics through the PUBLIC accessors only (e.g.
//     RecordPatches.IsPageParsed) with no marker literal anywhere in its source,
//   * a helper in a different file doing the reflection on the test class's behalf — the scan is
//     per source file, so the marker has to be in the same file as the class declaration,
//   * a marker that appears only in a computed/concatenated string.
// Those are the honest limits. The guard narrows the footgun; #1712 closes it.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public class ParserStaticsIsolationGuardTests
{
    // ── the detector, as pure functions over (class name, [Collection] name, source text) ─────

    // A quoted "_parsedSomething" / "TryParseSomethingFile" literal, or a ResetForReload call.
    // Quoting matters: BcAppSymbolCacheTableExtTests mentions `_parsedExtensionFields` in a
    // PROSE COMMENT without ever touching it, and must not be flagged for that.
    private static readonly Regex MarkerRegex = new(
        "\"(?:_parsed[A-Za-z]+|TryParse[A-Za-z]*File)\"" +
        @"|\bRecordPatches\s*\.\s*ResetForReload\b",
        RegexOptions.Compiled);

    internal static bool TouchesParseStatics(string sourceText) => MarkerRegex.IsMatch(sourceText);

    /// <summary>
    /// Returns an actionable violation message, or null when the class is fine.
    /// </summary>
    internal static string? FindViolation(string className, string? collectionName, string sourceText)
    {
        if (!TouchesParseStatics(sourceText)) return null;
        if (collectionName == RecordPatchesSerialCollection.Name) return null;

        return $"{className} reaches the RecordPatches AL parse statics (a \"_parsed…\" / " +
               $"\"TryParse…File\" reflection literal, or ResetForReload) but is " +
               (collectionName is null
                   ? "in no xunit collection"
                   : $"in collection \"{collectionName}\"") +
               $". Add [Collection(RecordPatchesSerialCollection.Name)] to it — the parsers " +
               "publish into process-wide static dictionaries on RecordPatches and xunit runs " +
               "collections in parallel, so a concurrent class can clear or repopulate them " +
               "between your write and your read (see #1696 for the four-of-five-CI-legs " +
               "failure this produced, and #1712 for the real fix).";
    }

    // This file necessarily CONTAINS the marker literals — the synthetic fixtures below spell
    // out the exact pattern the detector must recognise — but it never reflects on the statics
    // at runtime, so serialising it would be pointless. The exclusion is not a free pass:
    // TheDetectorFiresOnThisOwnFile_WhichIsWhyItIsExcluded pins that this file really does trip
    // the detector, so the exclusion stays justified rather than becoming a silent hole.
    private static readonly string[] SelfExcludedClasses =
    {
        nameof(ParserStaticsIsolationGuardTests),
    };

    // ── RED: the detector catches a real violation ────────────────────────────────────────────

    // Positive control. Without this, every assertion in this file would still pass if
    // FindViolation were hard-wired to return null — i.e. the whole guard would be noise.
    private const string ViolatingClassSource = """
        [Collection("some-other-collection")]
        public class HypotheticalNewParserTests
        {
            [Fact]
            public void ParsesATable()
            {
                var parse = typeof(AlRunner.Patches.RecordPatches).GetMethod("TryParseTableFile",
                    BindingFlags.NonPublic | BindingFlags.Static)!;
                parse.Invoke(null, new object[] { "table 61896 T { }" });
                var tables = typeof(AlRunner.Patches.RecordPatches)
                    .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetValue(null)!;
            }
        }
        """;

    [Fact]
    public void Detector_ClassTouchingTheStaticsInAnotherCollection_IsReported()
    {
        var violation = FindViolation(
            "HypotheticalNewParserTests", "some-other-collection", ViolatingClassSource);

        Assert.NotNull(violation);
        Assert.Contains("HypotheticalNewParserTests", violation);
        Assert.Contains("[Collection(RecordPatchesSerialCollection.Name)]", violation);
        Assert.Contains("some-other-collection", violation);
    }

    [Fact]
    public void Detector_ClassTouchingTheStaticsInNoCollection_IsReported()
    {
        var violation = FindViolation("HypotheticalNewParserTests", null, ViolatingClassSource);

        Assert.NotNull(violation);
        Assert.Contains("HypotheticalNewParserTests", violation);
        Assert.Contains("in no xunit collection", violation);
        Assert.Contains("[Collection(RecordPatchesSerialCollection.Name)]", violation);
    }

    [Fact]
    public void Detector_ClassTouchingTheStaticsInTheSerialCollection_IsNotReported()
    {
        Assert.Null(FindViolation(
            "HypotheticalNewParserTests", RecordPatchesSerialCollection.Name, ViolatingClassSource));
    }

    // Negative direction: an over-eager detector that flagged every test class would be useless
    // and would "pass" the assembly-wide assertion for the wrong reason. The fixture is modelled
    // on BcAppSymbolCacheTableExtTests, which names a parse static in prose and nothing else.
    [Fact]
    public void Detector_ClassThatOnlyMentionsAStaticInProse_IsNotReported()
    {
        const string innocentSource = """
            // A table extension whose fields never reached _parsedExtensionFields, causing the
            // symbol cache to drop them. TryParseTableExtensionFile is where the fix landed.
            public class BcAppSymbolCacheTableExtLikeTests
            {
                [Fact]
                public void ExtensionFieldsSurviveTheSymbolCache()
                {
                    Assert.Equal(2, Cache.ExtensionFieldsOf(61896).Count);
                }
            }
            """;

        Assert.Null(FindViolation("BcAppSymbolCacheTableExtLikeTests", null, innocentSource));
        Assert.False(TouchesParseStatics(innocentSource));
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

    [Theory]
    [InlineData("_parsedTables")]
    [InlineData("_parsedPages")]
    [InlineData("_parsedReports")]
    [InlineData("_parsedReportExtensions")]
    [InlineData("_parsedQueries")]
    [InlineData("_parsedXmlPorts")]
    [InlineData("_parsedObjectDecls")]
    [InlineData("_parsedObjectCaptions")]
    [InlineData("_parsedExtensionFields")]
    [InlineData("TryParseTableFile")]
    [InlineData("TryParseTableExtensionFile")]
    [InlineData("TryParsePageFile")]
    [InlineData("TryParseReportFile")]
    [InlineData("TryParseQueryFile")]
    [InlineData("TryParseXmlPortFile")]
    [InlineData("TryParseObjectDeclFile")]
    [InlineData("TryParseObjectCaptionFile")]
    public void Detector_RecognisesEveryParseStaticAndEntryPointByName(string memberName)
    {
        Assert.True(TouchesParseStatics($$"""
            RecordPatchesType.GetMember("{{memberName}}", BindingFlags.NonPublic | BindingFlags.Static);
            """),
            $"the detector must recognise reflection by the name \"{memberName}\"");

        // …and the bare name in prose must still not trip it.
        Assert.False(TouchesParseStatics($"// a note about {memberName} and nothing more"));
    }

    [Fact]
    public void Detector_RecognisesResetForReload()
    {
        Assert.True(TouchesParseStatics("AlRunner.Patches.RecordPatches.ResetForReload();"));
        Assert.False(TouchesParseStatics("// ResetForReload wipes the parse dictionaries"));
    }

    // ── GREEN: the real assembly is clean, and the detector really fires on real source ───────

    [Fact]
    public void EveryTestClassTouchingTheParseStatics_IsInTheSerialCollection()
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
                    $"{type.Name}. The parser-statics guard cannot vouch for a class it cannot " +
                    "read, and a guard that silently skips classes is worse than no guard.");
            }

            var violation = FindViolation(type.Name, CollectionNameOf(type), source.Text);
            if (violation is not null) violations.Add($"{source.Path}: {violation}");
        }

        Assert.True(violations.Count == 0, string.Join("\n\n", violations));
    }

    // Proves the assertion above is not passing because the detector never fires. These three
    // classes DO drive the parsers by reflection today; if the detector stopped recognising the
    // pattern, this fails instead of the guard quietly going green forever.
    [Theory]
    [InlineData(nameof(AlSourceParserSyntaxTreeTests))]
    [InlineData(nameof(AlSourceParserCommentTests))]
    [InlineData(nameof(SiblingParserCommentTests))]
    public void TheKnownParserTestClasses_AreDetectedAsTouchingTheStatics_AndAreAttributed(
        string className)
    {
        var sources = TestSourceByTypeName();
        Assert.True(sources.ContainsKey(className), $"source for {className} was not found");

        Assert.True(TouchesParseStatics(sources[className].Text),
            $"{className} drives the parsers by reflection, so the detector must flag it as " +
            "touching the parse statics — if it does not, the assembly-wide guard is vacuous");

        var type = TestClasses().Single(t => t.Name == className);
        Assert.Equal(RecordPatchesSerialCollection.Name, CollectionNameOf(type));
    }

    // The other half of "not vacuous": the detector must also say NO a lot. If it flagged every
    // test class, the assembly-wide assertion would only be measuring how diligent we were about
    // sprinkling the attribute everywhere.
    [Fact]
    public void MostTestClasses_AreNotDetectedAsTouchingTheStatics()
    {
        var sources = TestSourceByTypeName();
        var testClasses = TestClasses().Select(t => t.Name).ToArray();
        Assert.True(testClasses.Length >= 20,
            $"expected the test assembly to hold at least 20 test classes, found {testClasses.Length}");

        var untouched = testClasses
            .Where(n => sources.TryGetValue(n, out var s) && !TouchesParseStatics(s.Text))
            .ToArray();

        Assert.True(untouched.Length >= 20,
            $"only {untouched.Length} of {testClasses.Length} test classes are detected as NOT " +
            "touching the parse statics — the detector is over-eager and the guard proves nothing");
    }

    [Fact]
    public void TheDetectorFiresOnThisOwnFile_WhichIsWhyItIsExcluded()
    {
        // Documents the one exclusion honestly: this file contains the marker literals as test
        // fixtures, so the detector flags it, and it is exempt for that reason only.
        var sources = TestSourceByTypeName();
        Assert.True(TouchesParseStatics(sources[nameof(ParserStaticsIsolationGuardTests)].Text));
        Assert.Equal(new[] { nameof(ParserStaticsIsolationGuardTests) }, SelfExcludedClasses);
    }

    // ── source + reflection plumbing (fails loudly rather than finding nothing to check) ──────

    private sealed record TestSource(string Path, string Text);

    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static string TestSourceDirectory()
    {
        // Preferred: the compile-time path of this very file. Exact when the assembly is built
        // and tested on the same checkout, which is how CI and every dev box run it.
        var compileTimeDir = Path.GetDirectoryName(ThisFilePath());
        if (compileTimeDir is not null &&
            File.Exists(Path.Combine(compileTimeDir, "AlRunner.Tests.csproj")))
        {
            return compileTimeDir;
        }

        // Fallback: walk up from the loaded assembly (bin/<cfg>/<tfm>/ → the project dir).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AlRunner.Tests.csproj"))) return dir.FullName;
            var nested = Path.Combine(dir.FullName, "AlRunner.Tests", "AlRunner.Tests.csproj");
            if (File.Exists(nested)) return Path.GetDirectoryName(nested)!;
        }

        throw new Xunit.Sdk.XunitException(
            "the AlRunner.Tests source directory could not be located (compile-time path was " +
            $"'{ThisFilePath()}', assembly base was '{AppContext.BaseDirectory}'). The parser-" +
            "statics guard scans source, so with no source it would check nothing and read as a " +
            "passing guard — failing instead.");
    }

    // Matches a type declaration at the start of a line, optionally preceded by attributes and
    // modifiers. Anchoring at line start keeps prose like "…the bug class itself survives…" in a
    // `//` comment from registering as a declaration.
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
                $"no .cs files found under '{root}' — the parser-statics guard would check " +
                "nothing and still go green. Failing loudly instead.");
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
        typeof(ParserStaticsIsolationGuardTests).Assembly
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
