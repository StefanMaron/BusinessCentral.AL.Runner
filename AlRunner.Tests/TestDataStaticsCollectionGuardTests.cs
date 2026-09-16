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
using System.Text;
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
    private static readonly Regex Mutation = new(
        @"TestDataNormalization\.(Enabled\s*=[^=]|ResetForTests\(\)|TryParseArg\()"
        + @"|TestDataOptions\.(Enabled\s*=[^=]|ExplicitBackupPath\s*=[^=]|CompanyOverride\s*=[^=]|ResetForTests\(\)|TryParseArg\()"
        + @"|TestDataProvisioner\.(ResetForTests\(\)|Arm\()"
        + @"|BackupReaderTool\.ResetForTests\(\)",
        RegexOptions.Compiled);

    private static readonly Regex TopLevelClass = new(
        @"(?m)^(?:public|internal)\s+(?:sealed\s+)?(?:abstract\s+)?(?:static\s+)?(?:partial\s+)?class\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>Comments, and every string literal form this assembly uses, replaced by a
    /// space. See the file header for why the literals matter.</summary>
    internal static string StripCommentsAndLiterals(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var nl = text.IndexOf('\n', i);
                if (nl < 0) break;
                i = nl;                       // keep the newline: line structure is load-bearing
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                sb.Append(' ');
                continue;
            }
            if (text.AsSpan(i).StartsWith("\"\"\""))          // raw string
            {
                var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 3;
                sb.Append(' ');
                continue;
            }
            if (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '"')   // verbatim
            {
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                        i++; break;
                    }
                    i++;
                }
                sb.Append(' ');
                continue;
            }
            if (text[i] == '"')                                                // regular
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '"') { i++; break; }
                    if (text[i] == '\n') break;
                    i++;
                }
                sb.Append(' ');
                continue;
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

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
            var code = StripCommentsAndLiterals(File.ReadAllText(path));
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

    [Fact]
    public void TheGuardCanSeeTheMutators_SoAnEmptyResultIsNotAFalsePass()
    {
        // The third state: a guard that measured nothing must not report its success state.
        // Without this, a broken directory probe or an over-eager stripper makes the
        // membership test below pass by finding nobody at all.
        Assert.True(Directory.Exists(TestsDir), $"cannot see the test sources at '{TestsDir}'");
        var mutators = Mutators();
        Assert.True(mutators.Count >= 5,
            $"expected at least the 5 known mutators of the --test-data statics under '{TestsDir}', "
            + $"found {mutators.Count} ({string.Join(", ", mutators.Select(m => m.ClassName))}) — "
            + "the probe is broken, not the tree.");

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
