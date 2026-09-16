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

    private sealed record Mutator(string ClassName, string File, bool HasCollection);

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

                // The attribute block runs back from the declaration to the nearest line
                // carrying code — a previous class's closing brace, a using, the namespace.
                // Deliberately NOT "back to the first blank line": stripping a /// doc comment
                // leaves blank lines, so an attribute above a doc comment would be lost and the
                // class reported as an offender it is not.
                var before = code[..start].Split('\n').ToList();
                if (before.Count > 0 && before[^1].Length == 0) before.RemoveAt(before.Count - 1);
                var attrs = new List<string>();
                for (var k = before.Count - 1; k >= 0; k--)
                {
                    if (before[k].AsSpan().IndexOfAny('{', '}', ';') >= 0) break;
                    attrs.Add(before[k]);
                }

                found.Add(new Mutator(decls[i].Groups[1].Value, Path.GetFileName(path),
                    string.Join('\n', attrs).Contains("[Collection(", StringComparison.Ordinal)));
            }
        }
        return found;
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
        Assert.Contains(mutators, m => m.HasCollection);
    }

    [Fact]
    public void EveryClassThatMutatesTheTestDataStatics_JoinsASerialCollection()
    {
        var offenders = Mutators().Where(m => !m.HasCollection).ToList();

        Assert.True(offenders.Count == 0,
            "these test classes mutate the process-wide --test-data statics (TestDataOptions, "
            + "TestDataNormalization, TestDataProvisioner, BackupReaderTool) but declare no "
            + "[Collection], so xunit runs them in parallel with the other mutators and each "
            + "sees the others' writes (#4220): "
            + string.Join(", ", offenders.Select(m => $"{m.ClassName} ({m.File})"))
            + $". Add [Collection({nameof(TestDataStaticsSerialCollection)}.Name)]. Joining a "
            + "different DisableParallelization collection is equally correct.");
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
