// TestDataStaticsCollectionGuardTests — #4220.
//
// A [Collection] attribute fixes the pair that has it. Nothing makes the next class add one,
// and the symptom of forgetting is a test that fails about one run in nine — which reads as a
// flake and teaches everyone to re-run rather than to look.
//
// It reads the test SOURCES rather than reflecting over the assembly, because the claim is
// about what a future editor writes, and the file is where they write it.
//
// TRAP: it must strip STRING LITERALS, not only comments. AlRunner.Tests embeds AL fixtures
// and diagnostic messages in C# strings, and a scan that keeps them reports classes that
// mutate nothing — measured here: `Probe.Reset()` inside embedded AL named four innocent
// classes, and the same hole makes EnumMetadataRegistryCollectionGuardTests (#4199) list
// itself, exempted only by the literal "[Collection(" inside its own failure message.
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataStaticsCollectionGuardTests
{
    private static readonly string TestsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner.Tests"));

    /// <summary>The mutating members of the four --test-data statics. Assignments and calls
    /// only: `nameof(TestDataProvisioner.ResetForTests)` is how TestDataProvisionerTallyAtomicityTests
    /// names the method for a Cecil scan without ever calling it, and a pattern without the
    /// parentheses would report that class as a mutator it is not.</summary>
    /// <summary>
    /// One branch per static, and <see cref="Mutation"/> is their OR. Deliberately ONE source:
    /// a second copy of these patterns let a stale branch keep matching through the other copy,
    /// so the per-static check below silently kept passing (caught while fixing #4257).
    /// </summary>
    private static readonly (string Static, Regex Pattern)[] MutationBranches =
    {
        ("TestDataNormalization", new Regex(@"TestDataNormalization\.(Enabled\s*=[^=]|ResetForTests\(\)|TryParseArg\()", RegexOptions.Compiled)),
        ("TestDataOptions",       new Regex(@"TestDataOptions\.(Enabled\s*=[^=]|ExplicitBackupPath\s*=[^=]|CompanyOverride\s*=[^=]|ResetForTests\(\)|TryParseArg\()", RegexOptions.Compiled)),
        ("TestDataProvisioner",   new Regex(@"TestDataProvisioner\.(ResetForTests\(\)|Arm\()", RegexOptions.Compiled)),
        ("BackupReaderTool",      new Regex(@"BackupReaderTool\.ResetForTests\(\)", RegexOptions.Compiled)),
    };

    private static readonly Regex Mutation = new(
        string.Join("|", MutationBranches.Select(b => "(?:" + b.Pattern.ToString() + ")")),
        RegexOptions.Compiled);

    private static readonly Regex TopLevelClass = new(
        @"(?m)^(?:public|internal)\s+(?:sealed\s+)?(?:abstract\s+)?(?:static\s+)?(?:partial\s+)?class\s+(\w+)",
        RegexOptions.Compiled);

    private sealed record Mutator(string ClassName, string File);

    /// <summary>Every top-level class whose own body mutates one of the statics, with whether
    /// its attribute block declares a [Collection]. Class-scoped rather than file-scoped
    /// because TestDataSameNamedTablesTests.cs holds a serialised mutator BESIDE a
    /// collection-less class, which a file-scoped check would wave through.</summary>
    private static List<Mutator> Mutators()
    {
        var found = new List<Mutator>();
        foreach (var path in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var code = CSharpSource.ReadCodeOnly(path);
            if (!Mutation.IsMatch(code)) continue;
            var decls = TopLevelClass.Matches(code);
            for (var i = 0; i < decls.Count; i++)
            {
                var start = decls[i].Index;
                var end = i + 1 < decls.Count ? decls[i + 1].Index : code.Length;
                if (!Mutation.IsMatch(code[start..end])) continue;

                // No attribute-block walk here any more: which collection a class joins is read
                // by reflection in CollectionFacts(), so the source scan only has to answer
                // "does this class's own body mutate a static".
                found.Add(new Mutator(decls[i].Groups[1].Value, Path.GetFileName(path)));
            }
        }
        return found;
    }

    /// <summary>
    /// Class name -> the collection NAME it declares, for every class carrying
    /// <c>[Collection(...)]</c>; and the set of collection names whose
    /// <c>[CollectionDefinition]</c> sets <c>DisableParallelization = true</c>.
    ///
    /// Both ends are read by REFLECTION and keyed on the resolved collection name, which is the
    /// shape ConsoleSwapIsolationGuardTests.NonParallelCollections() uses and the reason it has
    /// no spelling problem. A first version of this read the USE SITE with a regex over source
    /// and keyed on the declaring TYPE name; that reported four live classes spelled
    /// <c>[Collection("object-metadata-registry")]</c> as having no attribute at all, and also
    /// false-rejected <c>ns.X.Name</c>, <c>global::</c> and same-line multi-attribute forms
    /// (found in review). Resolving both ends deletes that class of defect rather than adding a
    /// regex branch per spelling: the compiler has already done the resolution, so there is
    /// nothing left to parse.
    /// </summary>
    private static (IReadOnlyDictionary<string, string> Joined, ISet<string> Serial) CollectionFacts()
    {
        var joined = new Dictionary<string, string>(StringComparer.Ordinal);
        var serial = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in typeof(TestDataStaticsCollectionGuardTests).Assembly.GetTypes())
        {
            // Walk the base chain for [Collection]: CollectionAttribute is Inherited=true and
            // xunit honours that, but CustomAttributeData.GetCustomAttributes(type) returns
            // DECLARED attributes only. A class inheriting its collection from a base would
            // otherwise read as joining none -- a false offender. Census today is 0 such classes,
            // so this is latent; the sibling CollectionNameOf walks BaseType for the same reason
            // and this copy stopped one line short of it (found in review).
            for (var t = type; t is not null && joined.ContainsKey(type.Name) == false; t = t.BaseType)
            {
                foreach (var data in CustomAttributeData.GetCustomAttributes(t))
                {
                    if (data.AttributeType != typeof(CollectionAttribute)) continue;
                    if (data.ConstructorArguments.Count != 1) continue;
                    if (data.ConstructorArguments[0].Value is string inherited) joined[type.Name] = inherited;
                }
            }

            foreach (var data in CustomAttributeData.GetCustomAttributes(type))
            {
                if (data.ConstructorArguments.Count != 1) continue;
                if (data.ConstructorArguments[0].Value is not string name) continue;

                if (data.AttributeType == typeof(CollectionDefinitionAttribute)
                         && data.NamedArguments.Any(
                             a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                                  && a.TypedValue.Value is true))
                {
                    serial.Add(name);
                }
            }
        }
        return (joined, serial);
    }

    /// <summary>
    /// Which classes are seen mutating each of the four statics. Per-static rather than a total
    /// because a total cannot discriminate a broken probe from a smaller tree (#4257).
    /// </summary>
    private static IEnumerable<(string Static, IReadOnlyList<string> Seen)> MutatorsByStatic()
    {
        foreach (var (name, pattern) in MutationBranches)
        {
            var seen = new List<string>();
            foreach (var path in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories))
            {
                var code = CSharpSource.ReadCodeOnly(path);
                if (pattern.IsMatch(code)) seen.Add(Path.GetFileNameWithoutExtension(path));
            }
            yield return (name, seen);
        }
    }

    [Fact]
    public void TheGuardCanSeeTheMutators_SoAnEmptyResultIsNotAFalsePass()
    {
        // The third state: a guard that measured nothing must not report its success state.
        // Without this, a broken directory probe or an over-eager stripper makes the
        // membership test below pass by finding nobody at all.
        // Directory.Exists alone is not "the probe can see the sources": TestsDir pointed at
        // AlRunner.Tests/Fixtures exists and holds no test classes, so it sails past and the
        // checks below measure an empty tree (found in review of #4260). Assert the directory
        // holds this assembly's own source file, which is the cheapest thing that cannot be
        // true of the wrong directory.
        Assert.True(Directory.Exists(TestsDir), $"cannot see the test sources at '{TestsDir}'");
        Assert.True(File.Exists(Path.Combine(TestsDir, nameof(TestDataStaticsCollectionGuardTests) + ".cs")),
            $"'{TestsDir}' exists but does not contain this guard's own source file, so it is not "
            + "the AlRunner.Tests source root and everything measured below is about the wrong "
            + "tree (#4257).");
        var mutators = Mutators();
        // PER-STATIC coverage, not a total (#4257, and the review of #4260 that rejected the
        // total). A count cannot tell a broken probe from a legitimately smaller tree: both
        // shrink it, and measured, the two print BYTE-IDENTICAL output --
        //
        //   delete BackupRowProvenanceTests' only mutation   -> found 4 (A, B, C, D)
        //   make ONE Mutation alternation branch stale       -> found 4 (A, B, C, D)
        //
        // so "read the list to tell which" is not actionable, and an instruction to lower the
        // floor is actively wrong in the second case. This asserts instead that EVERY one of the
        // four statics is still seen by at least one class, which does discriminate: a stale
        // regex branch zeroes exactly the static it spells, while deleting a mutator only
        // decrements one static that other classes still cover.
        // The branch SET is pinned first, because the loop below cannot miss what it does not
        // iterate: deleting a branch outright made the static invisible rather than reported
        // empty, and every test stayed green (found in review). Making a branch stale is caught;
        // removing it was not.
        // SORTED both sides: what this pins is a SET, and Assert.Equal over the declaration
        // order reds on a pure reorder -- a semantic no-op. That is the shape that teaches
        // reflexive literal-editing (red, nothing wrong, edit the literal), which is the habit
        // the count floor this replaced had already built (found in review).
        var census = MutatorsByStatic().ToList();
        Assert.Equal(
            new[] { "BackupReaderTool", "TestDataNormalization", "TestDataOptions", "TestDataProvisioner" },
            census.Select(c => c.Static).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        foreach (var (statik, seen) in census)
        {
            // The census, not a claim about the other three: under a broken CSharpSource.CodeOnly
            // ALL four read as 0, and saying "the other three are still seen" would then be false
            // in exactly the case hardest to diagnose (found in review). Printing the counts lets
            // the reader tell a one-static failure (2; 3; 0; 3) from a blind probe (0; 0; 0; 0).
            Assert.True(seen.Count > 0,
                $"no class under '{TestsDir}' is seen mutating {statik}, so this guard is no longer "
                + $"covering that static. Census: "
                + string.Join("; ", census.Select(c => $"{c.Static}={c.Seen.Count}"))
                + ". One zero means that static's branch of MutationBranches no longer matches the "
                + "members that exist today; all zeros mean the probe itself stopped seeing the "
                + "sources. Check which before changing anything (#4257).");
        }

        // And it must still see a class it is NOT about to report, or the population above
        // could be five copies of the same unserialised shape.
        Assert.Contains(mutators, m => CollectionFacts().Joined.ContainsKey(m.ClassName));
    }

    [Fact]
    public void EveryClassThatMutatesTheTestDataStatics_JoinsASerialCollection()
    {
        // Resolve each collection to whether it REALLY disables parallelization, rather than
        // accepting any [Collection( ... ]. A substring test made this guard green on a class
        // joined to a parallel collection -- which still races, and which the message below
        // already promised was not good enough (found in review, #4249's reviewer on this PR).
        // Same shape as ConsoleSwapIsolationGuardTests.NonParallelCollections().
        var (joined, serial) = CollectionFacts();
        var offenders = Mutators()
            .Where(m => !joined.TryGetValue(m.ClassName, out var c) || !serial.Contains(c))
            .ToList();

        Assert.True(offenders.Count == 0,
            "these test classes mutate the process-wide --test-data statics (TestDataOptions, "
            + "TestDataNormalization, TestDataProvisioner, BackupReaderTool) but are not in a "
            + "collection that disables parallelization, so xunit runs them alongside the other "
            + "mutators and each sees the others' writes (#4220): "
            + string.Join(", ", offenders.Select(
                m => $"{m.ClassName} ({m.File}){(joined.TryGetValue(m.ClassName, out var c) ? $" -- its collection \"{c}\" does NOT set DisableParallelization" : " -- no [Collection]")}"))
            + $". Add [Collection({nameof(TestDataStaticsSerialCollection)}.Name)]. Joining a "
            + "different DisableParallelization collection is equally correct -- but it must "
            + "actually set that flag.");
    }

    [Fact]
    public void TheSerialCollection_ActuallyDisablesParallelization()
    {
        // The attribute's NAME buys nothing; DisableParallelization is what serialises. Read it
        // as data so dropping the argument fails here rather than silently un-fixing #4220.
        var definition = typeof(TestDataStaticsSerialCollection);
        var attribute = definition.GetCustomAttributesData().SingleOrDefault(
            a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        Assert.True(attribute != null,
            $"{definition.Name} carries no [CollectionDefinition], so nothing joins it.");
        Assert.True(
            attribute!.NamedArguments.Any(
                a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                     && a.TypedValue.Value is true),
            $"{definition.Name}'s [CollectionDefinition] no longer sets DisableParallelization = "
            + "true, so its members run in parallel again and #4220 is back with the attribute "
            + "still in place.");
    }
}
