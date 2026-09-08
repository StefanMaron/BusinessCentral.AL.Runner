using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #3552 — a table the runner COMPILED gets its NCLMetaTable from BC's own emitted metadata
/// document (captured by #3548) instead of from the runner's AL-source derivation, and a table
/// with no document keeps the derivation.
///
/// The four values asserted here are the ones the derivation got wrong, and none of them is
/// reachable from AL: <c>Editable</c> and the enum type live on
/// <c>Types.Metadata.MetaField</c> and no AL surface exposes either for a table field, while
/// <c>DataClassification</c> reaches AL only through the Field virtual table, which needs the
/// Base Application floor this fixture may not declare
/// (<c>.claude/rules/no-base-app-in-csharp-tests.md</c>). The AL-observable half of the same
/// change — per-field DataClassification, the relation columns and SystemCreatedBy's
/// relation — is adjudicated by a real service tier in corpus PR #292, not here.
///
/// Observed through <c>AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=2</c>, the same shape
/// <see cref="ObjectMetadataCaptureTests"/> uses for the capture it pins: the two construction
/// routes produce the same TYPE, so nothing downstream can be asked which one ran.
///
/// Warm assertions are identical to the cold ones and run against the same cache directory
/// (<c>.claude/rules/local-test-scope.md</c>): BC's Emit runs only on a compile-cache MISS, so
/// a warm run that lost the document would silently fall back to the derivation while the run
/// stayed green.
///
/// Spawns the real runner; needs the BC artifact cache. Skips when absent.
/// </summary>
public class TableMetadataFromBcDocumentTests
{
    private const int TableId = 70680;
    private const int EnumId = 70680;

    /// <summary>The Field system virtual table. The runner never compiles it, so no document
    /// exists for it and it must keep the derivation — the fallback direction, asserted
    /// against a real table rather than a contrived one.</summary>
    private const int FieldVirtualTableId = 2000000041;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TableMetadataFromBcDocument"));

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
    }

    private static (string output, int exit) RunRunner(string bundleDir, string alCacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        args.Append(" --verbose");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_TRACE_TABLE_METADATA_SOURCE"] = "2";
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps))
            psi.Arguments += $" --package-cache \"{platformApps}\"";
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>The last trace line for one field of the compiled table — the last, because the
    /// derivation traces the same field first and the assertion is about the state the run
    /// ends up serving.</summary>
    private static string FieldLine(string output, int fieldNo)
    {
        var prefix = $"[table-metadata] {TableId} field={fieldNo} ";
        var hit = output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(hit != null,
            $"no trace line for field {fieldNo} of table {TableId}. "
            + $"AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=2 emits one per field per build.\n{output}");
        return hit!;
    }

    private static void AssertCompiledTableCameFromBcDocument(string output, string phase)
    {
        Assert.True(output.Contains($"[table-metadata] {TableId} source=bc-document"),
            $"{phase}: table {TableId} was not built from BC's metadata document. "
            + $"It is compiled by this bundle, so #3548 captures one for it.\n{output}");

        // Field 2 declares BOTH non-default values. Editable is the shape #3545 names: the
        // derivation defaulted it to true, which is a plausible answer, so a test asserting
        // "some value" would pass on the defect.
        var f2 = FieldLine(output, 2);
        Assert.True(f2.Contains(" editable=False"),
            $"{phase}: field 2 declares Editable = false.\n{f2}");
        Assert.True(f2.Contains(" dataClassification=EndUserIdentifiableInformation"),
            $"{phase}: field 2 declares DataClassification = EndUserIdentifiableInformation, "
            + "not the table's CustomerContent.\n" + f2);

        // Field 3 pins the enum type and a THIRD DataClassification value, so no single
        // constant satisfies fields 1, 2 and 3 at once.
        var f3 = FieldLine(output, 3);
        Assert.True(f3.Contains($" enumTypeId={EnumId} enumTypeName=TMD Kind"),
            $"{phase}: field 3 is Enum \"TMD Kind\", so the metadata must name enum {EnumId} "
            + "rather than answer 0 / empty.\n" + f3);
        Assert.True(f3.Contains(" dataClassification=SystemMetadata"),
            $"{phase}: field 3 declares DataClassification = SystemMetadata.\n{f3}");

        // Field 1 declares none of the three: the control. If it came back editable=False or
        // carried an enum type, the values above would be coming from somewhere other than
        // each field's own declaration.
        var f1 = FieldLine(output, 1);
        Assert.True(f1.Contains(" editable=True"),
            $"{phase}: field 1 declares no Editable, so it stays editable.\n{f1}");
        Assert.True(f1.Contains(" dataClassification=CustomerContent"),
            $"{phase}: field 1 declares none, so it takes the table's CustomerContent.\n{f1}");
        Assert.True(f1.Contains(" enumTypeId=0"),
            $"{phase}: field 1 is an Integer, so it names no enum.\n{f1}");
    }

    [SkippableFact]
    public void CompiledTable_IsBuiltFromBcsMetadataDocument_ColdAndOnAWarmCacheHit()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-table-metadata-from-bc");
        var bundle = Path.Combine(scratch, "bundle");
        var alCacheDir = Path.Combine(scratch, "al-out");
        CopyDir(FixtureRoot, bundle);

        var (cold, coldExit) = RunRunner(bundle, alCacheDir);
        Assert.True(coldExit == 0 && cold.Contains("1P/0F/0E"), $"cold run must pass:\n{cold}");
        AssertCompiledTableCameFromBcDocument(cold, "cold run");

        // Same sources, same cache directory: the AL-output cache HITs and Emit never runs,
        // so the document reaches this run only through the replayed sidecar.
        var (warm, warmExit) = RunRunner(bundle, alCacheDir);
        Assert.True(warmExit == 0 && warm.Contains("1P/0F/0E"), $"warm run must pass:\n{warm}");
        Assert.Contains("[cache] HIT", warm);
        AssertCompiledTableCameFromBcDocument(warm, "warm run (AL-output cache HIT)");
    }

    /// <summary>
    /// Each table takes BC's document exactly ONCE per run, however many apps the bundle path
    /// holds.
    ///
    /// <c>BcRuntime.SetTestAssembly</c> runs once per emitted assembly, and the sweep that
    /// applies the document hangs off it, so a sweep with no ledger reloads every eligible
    /// table again on each call. That is not idempotent: BC's <c>AssignFromMetaTable</c>
    /// rebuilds the field array, and a data provider already open over the table then raises
    /// <c>NavObjectDefinitionChangedException</c>. Measured on <c>tests/runner-extras</c>
    /// before the fix — table 60710 reloaded eight times, one suite lost to EXEC-FAIL and an
    /// unrelated query test failing as collateral.
    ///
    /// Two sibling apps under one path is the smallest bundle that emits two assemblies, and
    /// it is the shape <c>tests/runner-extras</c> has. Measured on this fixture with the
    /// ledger removed: 4 loads for the first table and 3 for the second — so the count of one
    /// is a claim about the ledger, not an accident of there being nothing to repeat.
    /// </summary>
    [SkippableFact]
    public void EachTable_TakesBcsDocumentOnce_AcrossASiblingAppBundle()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-table-metadata-once");
        const int TableA = 70690;
        const int TableB = 70695;

        WriteSiblingApp(Path.Combine(scratch, "app-a"), "TMA", TableA, 70694);
        WriteSiblingApp(Path.Combine(scratch, "app-b"), "TMB", TableB, 70699);

        var (output, exit) = RunRunner(scratch, Path.Combine(scratch, "al-out"));
        Assert.True(exit == 0 && output.Contains("2P/0F/0E"), $"run must pass:\n{output}");

        foreach (var id in new[] { TableA, TableB })
        {
            var loads = output.Split('\n')
                .Count(l => l.TrimEnd('\r') == $"[table-metadata] {id} source=bc-document");
            Assert.True(loads == 1,
                $"table {id} must take BC's document exactly once; saw {loads}. "
                + "More than one means the sweep is running per assembly.\n" + output);
        }
    }

    /// <summary>One self-contained app: a table, and a test that writes a row to it so the
    /// table really is materialised rather than merely declared.</summary>
    private static void WriteSiblingApp(string dir, string prefix, int idFrom, int idTo)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "{{prefix}} App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idTo}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Objects.al"), $$"""
        table {{idFrom}} "{{prefix}} Thing"
        {
            fields { field(1; "Entry No."; Integer) { } }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        codeunit {{idFrom}} "{{prefix}} Tests"
        {
            Subtype = Test;

            [Test]
            procedure ThingRoundTrips()
            var
                Thing: Record "{{prefix}} Thing";
            begin
                Thing.Init();
                Thing."Entry No." := 1;
                Thing.Insert();
                Thing.Get(1);
            end;
        }
        """);
    }

    [SkippableFact]
    public void TableWithNoCapturedDocument_KeepsTheDerivation()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-table-metadata-fallback");
        var bundle = Path.Combine(scratch, "bundle");
        CopyDir(FixtureRoot, bundle);

        var (output, exit) = RunRunner(bundle, Path.Combine(scratch, "al-out"));
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"), $"run must pass:\n{output}");

        // The Field virtual table is built during this same run and the runner never compiles
        // it, so no document exists for it. Availability is what decides the route, so it must
        // report the derivation — and must never report the other one.
        Assert.Contains($"[table-metadata] {FieldVirtualTableId} source=derived", output);
        Assert.DoesNotContain($"[table-metadata] {FieldVirtualTableId} source=bc-document", output);
    }
}
