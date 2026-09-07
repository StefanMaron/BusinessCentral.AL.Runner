// ConsoleSwapIsolationGuardTests — meta-guard for #2913.
//
// Console.Out and Console.Error are PROCESS-WIDE. A test that swaps one for a sink, runs
// something, reads the sink back and restores in a `finally` owns that writer across a window,
// and xunit runs test COLLECTIONS in parallel (parallelizeTestCollections=true — see
// xunit.runner.json). Two such classes running at once clobber each other: one class's
// `finally` restores the real Console while the other is still writing to its sink, so the
// line lands in the wrong place. It reads exactly like the output having been swallowed.
//
// ConsoleFilterSerialCollection fences the classes that call Log.Install(). The race needs
// neither Log.Install() nor Log.Verbose — swapping the console is enough on its own — so that
// collection's own header names the classes it does NOT cover and points here. This file is the
// enforcement for all of them: any test class whose source swaps the console must sit in a
// collection that xunit will not run in parallel.
//
// See docs/test-isolation.md#console-swapping for the full inventory, the two rejected designs
// and what remains open (#2913's option 2, an injectable sink, is still the real fix).
//
// ── What counts as "fenced", and why it is derived rather than listed ─────────────────────────
//
// Not a list of blessed collection NAMES: any collection whose [CollectionDefinition] carries
// DisableParallelization = true. xunit runs every such collection serially, one at a time, so a
// class in ANY of them holds the console alone for its duration — which is the entire property
// this guard requires.
//
// That is what dissolves the objection #2913 raised against fencing: a class can carry only ONE
// [Collection], so ProvisionGapLogTests — which needs RecordPatchesSerialCollection because
// ProvisionGapLog is process-global state — could not also join the console one. It does not
// need to. RecordPatchesSerialCollection is itself non-parallelizable, so that class is already
// safe, and the guard says so without any collection being merged or any test losing the fixture
// it depends on.
//
// Deriving it also means the flag is read off each attribute as DATA rather than trusted from a
// comment: flip DisableParallelization to false on any collection and every class that was
// relying on it starts failing here, instead of the race quietly reopening.
// ParserStaticsIsolationGuardTests pins the same property for a hardcoded list; this guard has
// no list to pin.
//
// ── How the detection works, and what it does NOT catch ──────────────────────────────────────
//
// A source scan, for the same reason the parser-statics guard uses one: what makes a class
// dangerous is a CALL to Console.SetOut / Console.SetError, and there is no way to ask
// reflection "does this type's body call this method" without reading IL. Reflection is used
// for the half it is exact about — which types are test classes, and what [Collection] each
// carries.
//
// Caught:
//   * Console.SetOut / Console.SetError / Console.SetIn as a call, in the same source FILE as
//     the test class declaration — including from a private helper method, which is the common
//     shape (InstallTriggerAsyncObservationTests.InvokeCapturingStdErr) and the one a
//     per-class brace-tracking scan gets wrong when a nested type sits in between.
// NOT caught:
//   * a swap performed by a shared helper in a DIFFERENT file on the test class's behalf — the
//     scan is per file, so the call has to be in the file that declares the class,
//   * a swap reached through reflection or an indirection that never spells the call,
//   * a test that spawns the runner as a SUBPROCESS and reads its stdout — correctly not
//     caught, because each subprocess has its own Console and cannot race this process's.
//
// Those are the honest limits. The guard stops the list growing; #2913's option 2 closes it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public class ConsoleSwapIsolationGuardTests
{
    // ── the detector, as pure functions over (class name, [Collection] name, source text) ─────

    // A Console.SetOut / SetError / SetIn CALL. The `\s*` around the dot matches the formatting
    // a line break can introduce; the trailing `\(` is what keeps prose ("Console.SetOut is
    // process-wide") from registering — this file's own header says exactly that and must not
    // count itself for it.
    private static readonly Regex SwapRegex = new(
        @"\bConsole\s*\.\s*Set(?:Out|Error|In)\s*\(",
        RegexOptions.Compiled);

    internal static bool SwapsTheConsole(string sourceText) => SwapRegex.IsMatch(sourceText);

    /// <summary>
    /// Every collection in this assembly whose <c>[CollectionDefinition]</c> sets
    /// <c>DisableParallelization = true</c>, read off the attributes as data.
    ///
    /// <para>This is deliberately DERIVED rather than hardcoded. The property a console-swapping
    /// class needs is "xunit will not run me next to another collection", which is exactly what
    /// that flag means, and a name-based allowlist would both need maintaining and be unable to
    /// express it. It also means a class that must join some other serial collection for an
    /// unrelated reason — <see cref="RecordPatchesSerialCollection"/> for process-global state,
    /// <see cref="BcEngineCollection"/> for its fixture — satisfies this guard without having to
    /// be in two collections at once, which it cannot be.</para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> NonParallelCollections()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in typeof(ConsoleSwapIsolationGuardTests).Assembly.GetTypes())
        {
            foreach (var data in CustomAttributeData.GetCustomAttributes(type))
            {
                if (data.AttributeType != typeof(CollectionDefinitionAttribute)) continue;
                if (data.ConstructorArguments.Count != 1) continue;
                if (data.ConstructorArguments[0].Value is not string name) continue;

                var disabled = data.NamedArguments.Any(
                    a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                         && a.TypedValue.Value is true);
                if (disabled) found[name] = type.Name;
            }
        }
        return found;
    }

    /// <summary>
    /// Returns an actionable violation message, or null when the class is fine.
    /// </summary>
    internal static string? FindViolation(
        string className,
        string? collectionName,
        string sourceText,
        IReadOnlyDictionary<string, string> nonParallelCollections)
    {
        if (!SwapsTheConsole(sourceText)) return null;
        if (collectionName is not null && nonParallelCollections.ContainsKey(collectionName))
            return null;

        return $"{className} swaps the process-wide Console (Console.SetOut / Console.SetError) " +
               "but is " +
               (collectionName is null
                   ? "in no xunit collection"
                   : $"in collection \"{collectionName}\", which does not set " +
                     "DisableParallelization = true") +
               ". Add [Collection(ConsoleFilterSerialCollection.Name)] to it — or any other " +
               "collection whose [CollectionDefinition] disables parallelization, if it needs " +
               "one for an unrelated reason. xunit runs collections in parallel, so a concurrent " +
               "class's `finally` can restore the real Console while this one is still writing " +
               "to its sink; the line then lands in the wrong place and reads as output that was " +
               "swallowed (see #2874 for the observed flake and #2913 for the real fix, an " +
               "injectable sink that removes the need to touch Console at all). If this class " +
               "only redirects to silence noise, dropping the swap entirely is better than " +
               "fencing it.";
    }

    // ── RED: the detector catches a real violation ────────────────────────────────────────────

    // Positive control. Without this, every assertion in this file would still pass if
    // FindViolation were hard-wired to return null — i.e. the whole guard would be noise.
    // Assembled at runtime from Call() rather than written out, for the same reason the [Theory]
    // spellings below are: a literal `Console` + `.SetError(` in this file would make the
    // detector fire on the guard's own source, and the guard would then have to exempt itself
    // from the assembly-wide sweep. ParserStaticsIsolationGuardTests does carry that exemption
    // and pins it with a test; avoiding the need for one is strictly better, so
    // ThisGuardsOwnSource_DoesNotSwapTheConsole pins the avoidance instead.
    private static readonly string ViolatingClassSource = $$"""
        public sealed class HypotheticalNewCaptureTests
        {
            [Fact]
            public void CapturesStdErr()
            {
                var saved = Console.Error;
                var buffer = new StringWriter();
                {{Call("SetError", "buffer")}}
                try { DoSomething(); }
                finally { {{Call("SetError", "saved")}} }
                Assert.Contains("boom", buffer.ToString());
            }
        }
        """;

    /// <summary>
    /// Builds the text of a <c>Console.SetX(arg);</c> call without this file ever containing one.
    /// The concatenation is what keeps the detector — which requires the name and the open paren
    /// adjacently — from matching this source.
    /// </summary>
    private static string Call(string method, string argument, string spacing = "") =>
        "Console" + spacing + "." + spacing + method + spacing + "(" + spacing + argument + ");";

    [Fact]
    public void Detector_ClassSwappingTheConsoleInNoCollection_IsReported()
    {
        var violation = FindViolation(
            "HypotheticalNewCaptureTests", null, ViolatingClassSource, NonParallelCollections());

        Assert.NotNull(violation);
        Assert.Contains("HypotheticalNewCaptureTests", violation);
        Assert.Contains("in no xunit collection", violation);
        Assert.Contains("[Collection(ConsoleFilterSerialCollection.Name)]", violation);
    }

    [Fact]
    public void Detector_ClassSwappingTheConsoleInAParallelCollection_IsReported()
    {
        var nonParallel = NonParallelCollections();
        Assert.DoesNotContain("some-parallel-collection", nonParallel.Keys);

        var violation = FindViolation(
            "HypotheticalNewCaptureTests", "some-parallel-collection", ViolatingClassSource,
            nonParallel);

        Assert.NotNull(violation);
        Assert.Contains("some-parallel-collection", violation);
        Assert.Contains("DisableParallelization = true", violation);
    }

    [Fact]
    public void Detector_ClassSwappingTheConsoleInASerialCollection_IsNotReported()
    {
        Assert.Null(FindViolation(
            "HypotheticalNewCaptureTests", ConsoleFilterSerialCollection.Name,
            ViolatingClassSource, NonParallelCollections()));
    }

    /// <summary>
    /// The load-bearing half of the derived allowance, and the direct answer to #2913's
    /// "a class cannot be in two collections" objection: a console swapper that must live in
    /// <see cref="RecordPatchesSerialCollection"/> for unrelated reasons is already safe, and
    /// the guard must accept it there rather than demanding a merge.
    /// </summary>
    [Theory]
    [InlineData("record-patches-serial")]
    [InlineData("cache-roots-serial")]
    [InlineData("bccompiler-shared-references")]
    public void Detector_AcceptsAnyCollectionThatDisablesParallelization(string collectionName)
    {
        var nonParallel = NonParallelCollections();
        Assert.True(nonParallel.ContainsKey(collectionName),
            $"expected \"{collectionName}\" to be declared with DisableParallelization = true");

        Assert.Null(FindViolation(
            "HypotheticalNewCaptureTests", collectionName, ViolatingClassSource, nonParallel));
    }

    // Negative direction: an over-eager detector that flagged every test class would be useless
    // and would "pass" the assembly-wide assertion for the wrong reason.
    [Fact]
    public void Detector_ClassThatOnlyMentionsTheConsoleInProse_IsNotReported()
    {
        const string innocentSource = """
            // Console.SetOut is process-wide, which is why this test reads the subprocess's
            // stdout instead of redirecting its own Console.SetError anywhere.
            public sealed class SubprocessStdoutLikeTests
            {
                [Fact]
                public void ReportsTheExitCode()
                {
                    var result = RunTheRunnerAsASubprocess();
                    Assert.Equal(0, result.ExitCode);
                    Assert.Contains("2 passed", result.StdOut);
                }
            }
            """;

        Assert.Null(FindViolation(
            "SubprocessStdoutLikeTests", null, innocentSource, NonParallelCollections()));
        Assert.False(SwapsTheConsole(innocentSource));
    }

    [Fact]
    public void Detector_OrdinaryTestClass_IsNotReported()
    {
        const string ordinarySource = """
            public sealed class ErrorClassifierLikeTests
            {
                [Fact]
                public void ClassifiesACompileError()
                {
                    Assert.Equal(ErrorKind.Compile, Classify("AL0118: unknown type"));
                }
            }
            """;

        Assert.Null(FindViolation(
            "ErrorClassifierLikeTests", null, ordinarySource, NonParallelCollections()));
    }

    [Theory]
    [InlineData("SetOut", "sink", "")]
    [InlineData("SetError", "buffer", "")]
    [InlineData("SetIn", "reader", "")]
    [InlineData("SetError", "TextWriter.Null", "")]
    [InlineData("SetOut", "sink", " ")]      // whitespace around the dot and paren
    public void Detector_RecognisesEverySwapSpelling(string method, string argument, string spacing)
    {
        var snippet = Call(method, argument, spacing);
        Assert.True(SwapsTheConsole(snippet), $"the detector must recognise `{snippet}`");
    }

    [Fact]
    public void Detector_RecognisesASwapInsideAFinallyBlock()
    {
        // The restore half is as much a swap as the redirect half — a class that only ever
        // appeared in a `finally` would still own the writer across the window.
        var snippet = "finally { " + Call("SetError", "previous") + " }";
        Assert.True(SwapsTheConsole(snippet), $"the detector must recognise `{snippet}`");
    }

    [Theory]
    [InlineData("// Console.SetOut is process-wide")]
    [InlineData("/// <summary>Console.SetError is not touched here.</summary>")]
    [InlineData("var text = Console.Out.ToString();")]
    [InlineData("var name = nameof(SomeHelper);")]
    public void Detector_DoesNotFireOnProseOrReads(string snippet)
    {
        Assert.False(SwapsTheConsole(snippet), $"the detector must NOT fire on `{snippet}`");
    }

    // ── GREEN: the real assembly is clean, and the detector really fires on real source ───────

    [Fact]
    public void EveryTestClassSwappingTheConsole_IsInANonParallelCollection()
    {
        var sources = TestSourceByTypeName();
        var nonParallel = NonParallelCollections();
        var violations = new List<string>();

        foreach (var type in TestClasses())
        {
            if (type.Name == nameof(ConsoleSwapIsolationGuardTests)) continue;
            if (!sources.TryGetValue(type.Name, out var source))
            {
                throw new Xunit.Sdk.XunitException(
                    $"no source file under {TestSourceDirectory()} declares test class " +
                    $"{type.Name}. The console-swap guard cannot vouch for a class it cannot " +
                    "read, and a guard that silently skips classes is worse than no guard.");
            }

            var violation = FindViolation(
                type.Name, CollectionNameOf(type), source.Text, nonParallel);
            if (violation is not null) violations.Add($"{source.Path}: {violation}");
        }

        Assert.True(violations.Count == 0, string.Join("\n\n", violations));
    }

    // Proves the assertion above is not passing because the detector never fires. These classes
    // DO swap the console today; if the detector stopped recognising the pattern, this fails
    // instead of the guard quietly going green forever.
    [Theory]
    [InlineData(nameof(LogUserFacingTagsTests))]
    [InlineData(nameof(WatchSourceTests))]
    [InlineData(nameof(PhaseLogTests))]
    [InlineData(nameof(HotPathHookCostTests))]
    [InlineData(nameof(AlCallStackCaptureNoFallbackTests))]
    [InlineData(nameof(CacheKeyUnhashableDependencyTests))]
    [InlineData(nameof(InstallTriggerAsyncObservationTests))]
    [InlineData(nameof(ProvisionGapLogTests))]
    public void TheKnownConsoleSwappingClasses_AreDetected_AndAreFenced(string className)
    {
        var sources = TestSourceByTypeName();
        Assert.True(sources.ContainsKey(className), $"source for {className} was not found");

        Assert.True(SwapsTheConsole(sources[className].Text),
            $"{className} swaps the process-wide Console, so the detector must flag it — if it " +
            "does not, the assembly-wide guard is vacuous");

        var type = TestClasses().Single(t => t.Name == className);
        var collection = CollectionNameOf(type);
        Assert.NotNull(collection);
        Assert.True(NonParallelCollections().ContainsKey(collection),
            $"{className} is in collection \"{collection}\", which does not disable parallelization");
    }

    // The other half of "not vacuous": the detector must also say NO a lot. If it flagged every
    // test class, the assembly-wide assertion would only be measuring how diligent we were about
    // sprinkling the attribute everywhere.
    [Fact]
    public void MostTestClasses_DoNotSwapTheConsole()
    {
        var sources = TestSourceByTypeName();
        var testClasses = TestClasses().Select(t => t.Name).ToArray();
        Assert.True(testClasses.Length >= 20,
            $"expected the test assembly to hold at least 20 test classes, found {testClasses.Length}");

        var untouched = testClasses
            .Where(n => sources.TryGetValue(n, out var s) && !SwapsTheConsole(s.Text))
            .ToArray();

        Assert.True(untouched.Length >= 20,
            $"only {untouched.Length} of {testClasses.Length} test classes are detected as NOT " +
            "swapping the console — the detector is over-eager and the guard proves nothing");
    }

    /// <summary>
    /// This file names <c>Console.SetOut</c> only in prose and in quoted fixture snippets, never
    /// as a call in its own body — so unlike the parser-statics guard it needs no self-exclusion
    /// to stay clean. Pinned here so that adding a real swap to this file fails loudly rather
    /// than making the guard exempt itself by accident.
    /// </summary>
    [Fact]
    public void ThisGuardsOwnSource_DoesNotSwapTheConsole()
    {
        var sources = TestSourceByTypeName();
        Assert.True(sources.ContainsKey(nameof(ConsoleSwapIsolationGuardTests)));
        Assert.False(SwapsTheConsole(sources[nameof(ConsoleSwapIsolationGuardTests)].Text),
            "this guard's own source must not swap the console — it asserts over the whole " +
            "assembly and would otherwise have to exempt itself");
    }

    // ── source + reflection plumbing (fails loudly rather than finding nothing to check) ──────

    private sealed record TestSource(string Path, string Text);

    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static string TestSourceDirectory()
    {
        // Same two-step probe as ParserStaticsIsolationGuardTests: the compile-time path first,
        // then a walk up from the loaded assembly. The repo-root Directory.Build.props sets
        // PathMap, which rewrites [CallerFilePath] to "/_/…", so in practice the fallback is the
        // live path — the File.Exists probe is what makes the choice safe either way.
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
            $"'{ThisFilePath()}', assembly base was '{AppContext.BaseDirectory}'). The console-" +
            "swap guard scans source, so with no source it would check nothing and read as a " +
            "passing guard — failing instead.");
    }

    // Matches a type declaration at the start of a line, optionally preceded by attributes and
    // modifiers. Anchoring at line start keeps prose in a `//` comment from registering as one.
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
                $"no .cs files found under '{root}' — the console-swap guard would check " +
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
        typeof(ConsoleSwapIsolationGuardTests).Assembly
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
