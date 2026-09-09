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

    /// <summary>
    /// A table in a source-compiled DEPENDENCY takes BC's document too, cold and across the
    /// dependency's own compile-cache HIT.
    ///
    /// <c>AlObjectMetadataRegistry</c> is keyed <c>(kind, id)</c> with no notion of which app
    /// compiled the object, so coverage was never limited to the app under test — but the
    /// replay path is different and could fail on its own: a dependency served from
    /// <c>compiled-deps</c> skips its own Emit, and its documents reach the run only through
    /// <c>DependencyLoader</c>'s <c>.object-metadata.json</c> sidecar. Losing that would show
    /// up on the second run only, with the first one green. Same two-process shape as
    /// <see cref="ObjectMetadataCaptureTests"/>'s dependency case, which is where it comes
    /// from.
    ///
    /// What is NOT covered by this, and is #3549: a dependency shipped as a precompiled
    /// <c>.app</c>, which never compiles here and so has no document at all.
    /// </summary>
    [SkippableFact]
    public void SourceCompiledDependencyTable_TakesBcsDocument_AcrossItsOwnCacheHit()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-table-metadata-dep");
        var depDir = Path.Combine(scratch, "dep-app");
        var testsDir = Path.Combine(scratch, "tests-app");
        var alCacheDir = Path.Combine(scratch, "al-out");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);

        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();
        const int DepTableId = 70670;
        const int DepEnumId = 70670;

        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "TMDep App",
          "publisher": "TMDep",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70670, "to": 70674 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Dep.al"), """
        enum 70670 "TMDep Kind"
        {
            Extensible = true;
            value(0; Plain) { }
            value(5; Fancy) { }
        }

        table 70670 "TMDep Thing"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Description; Text[50])
                {
                    DataClassification = EndUserIdentifiableInformation;
                    Editable = false;
                }
                field(3; Kind; Enum "TMDep Kind") { DataClassification = SystemMetadata; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "TMDep Tests",
          "publisher": "TMDep",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "TMDep App", "publisher": "TMDep", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70675, "to": 70679 } ],
          "runtime": "14.0"
        }
        """);
        var testsAlPath = Path.Combine(testsDir, "Tests.al");
        File.WriteAllText(testsAlPath, """
        codeunit 70675 "TMDep Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepThingRoundTrips()
            var
                Thing: Record "TMDep Thing";
            begin
                Thing.Init();
                Thing."Entry No." := 1;
                Thing.Insert();
                Thing.Get(1);
            end;
        }
        """);

        void AssertDepTableCameFromBcDocument(string output, string phase)
        {
            Assert.True(output.Contains($"[table-metadata] {DepTableId} source=bc-document"),
                $"{phase}: dependency table {DepTableId} did not take BC's document.\n{output}");

            var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            string Field(int no) => lines.LastOrDefault(
                l => l.StartsWith($"[table-metadata] {DepTableId} field={no} ", StringComparison.Ordinal))
                ?? throw new Xunit.Sdk.XunitException(
                    $"{phase}: no trace line for dependency field {no}.\n{output}");

            Assert.True(Field(2).Contains(" editable=False")
                        && Field(2).Contains(" dataClassification=EndUserIdentifiableInformation"),
                $"{phase}: the dependency's field 2 declares Editable = false and "
                + "EndUserIdentifiableInformation.\n" + Field(2));
            Assert.True(Field(3).Contains($" enumTypeId={DepEnumId} enumTypeName=TMDep Kind"),
                $"{phase}: the dependency's field 3 is Enum \"TMDep Kind\".\n" + Field(3));
        }

        var (run1, exit1) = RunRunner(testsDir, alCacheDir);
        Assert.True(exit1 == 0 && run1.Contains("1P/0F/0E"), $"run 1 (all cold) must pass:\n{run1}");
        AssertDepTableCameFromBcDocument(run1, "run 1 (cold)");

        // Touch the tests bundle only: its key changes (bundle MISS) while the dep's
        // synthesized .app stays byte-identical, so the dep is served from compiled-deps and
        // its Emit never runs.
        File.AppendAllText(testsAlPath, "\n// touched\n");

        var (run2, exit2) = RunRunner(testsDir, alCacheDir);
        Assert.True(exit2 == 0 && run2.Contains("1P/0F/0E"), $"run 2 (dep HIT) must pass:\n{run2}");
        Assert.Contains("source-cache HIT", run2);
        AssertDepTableCameFromBcDocument(run2, "run 2 (dependency compile-cache HIT)");
    }

    /// <summary>
    /// A base table extended by a KEY-ONLY tableextension in another app keeps the derivation.
    ///
    /// This is the case the merged-field count could not see. Fields and keys reach
    /// <c>MergeExtensionFields</c> through separate channels (#3216), and a key-only —
    /// or <c>modify(...)</c>-only — extension contributes no fields, so
    /// <c>_parsedExtensionFields</c> stays empty. A count-based guard therefore passed, the
    /// base app's own document won, and the extension's key vanished: measured on this
    /// fixture, <c>RecordRef.KeyCount()</c> answered 3 instead of 4, exit 0, no diagnostic.
    ///
    /// The claim is about a KEY the runner merges at runtime, so the count is the assertion
    /// and the trace line is the mechanism behind it — both are checked, because either alone
    /// can be satisfied by the wrong thing: the count alone would pass if the key came back by
    /// some other route, and the route alone says nothing about the key surviving.
    ///
    /// Cross-app on purpose. A same-app extension is merged into the emitting app's own
    /// document by BC's compiler; one contributed by a DIFFERENT app is runtime-merged state
    /// that no per-app document can express, which is the whole distinction the guard encodes.
    /// </summary>
    [SkippableFact]
    public void BaseTableWithAKeyOnlyExtensionInAnotherApp_KeepsTheDerivation()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-table-metadata-keyonly");
        var depDir = Path.Combine(scratch, "dep-app");
        var testsDir = Path.Combine(scratch, "tests-app");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);

        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();
        const int DepTableId = 70670;

        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "TMK Dep App",
          "publisher": "TMK",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70670, "to": 70674 } ],
          "runtime": "14.0"
        }
        """);
        // Two declared keys, so the count below distinguishes "the extension's key is missing"
        // from "keys are missing altogether".
        File.WriteAllText(Path.Combine(depDir, "Dep.al"), """
        table 70670 "TMK Dep Thing"
        {
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Description; Text[50]) { }
                field(3; Rank; Integer) { }
            }
            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
                key(ByDescription; Description) { }
            }
        }
        """);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "TMK Tests",
          "publisher": "TMK",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "TMK Dep App", "publisher": "TMK", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70675, "to": 70679 } ],
          "runtime": "14.0"
        }
        """);
        // Keys only — no fields block at all, which is what leaves _parsedExtensionFields empty.
        File.WriteAllText(Path.Combine(testsDir, "Tests.al"), """
        tableextension 70675 "TMK Key Only Ext" extends "TMK Dep Thing"
        {
            keys
            {
                key(ByRank; Rank) { }
            }
        }

        codeunit 70675 "TMK Tests"
        {
            Subtype = Test;

            [Test]
            procedure ExtensionKeyIsStillThere()
            var
                RRef: RecordRef;
                KRef: KeyRef;
                FRef: FieldRef;
            begin
                RRef.Open(70670);
                if RRef.KeyCount() <> 4 then
                    Error('KeyCount=%1, expected 4 (PK, ByDescription, ByRank, and the ' +
                          'platform key). A lower count means the extension''s key was dropped.',
                          RRef.KeyCount());

                // Name the key by the field it starts on: a count alone would be satisfied by
                // any third key at all.
                KRef := RRef.KeyIndex(3);
                FRef := KRef.FieldIndex(1);
                if FRef.Number() <> 3 then
                    Error('Key 3 starts on field %1, expected 3 (Rank) — the extension''s key.',
                          FRef.Number());
                RRef.Close();
            end;
        }
        """);

        var (output, exit) = RunRunner(testsDir, Path.Combine(scratch, "al-out"));
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"),
            $"the extension's key must survive:\n{output}");

        // ...and it survived because the table kept the derivation, not by some other route.
        Assert.Contains($"[table-metadata] {DepTableId} source=derived", output);
        Assert.DoesNotContain($"[table-metadata] {DepTableId} source=bc-document", output);
    }

    /// <summary>
    /// #3600 — a SAME-app, add-only tableextension no longer forces the derivation: BC's
    /// compiler already folded its field AND its key into the base table's own document, so
    /// excluding it was sending a table the document already answered correctly through the
    /// hand-derivation. Measured on <c>ObjectMetadataCapture</c> (see the issue): 5 of the
    /// al-language corpus's 6 previously-excluded tables are exactly this shape.
    ///
    /// Both halves are asserted, not just the route: a route-only check would also pass if the
    /// field or the key had silently vanished on the way to the document, which is the exact
    /// failure this guard exists to avoid (see the key-only regression test above). The field's
    /// Editable/DataClassification are set to NON-default values for the same reason
    /// <see cref="AssertCompiledTableCameFromBcDocument"/> does it: a default-valued assertion
    /// would still pass if the field came from nowhere at all.
    /// </summary>
    [SkippableFact]
    public void SameAppAddOnlyExtension_TakesBcsDocument_WithItsFieldAndKeyIntact()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-table-metadata-sameapp-addonly");
        var appDir = Path.Combine(scratch, "app");
        Directory.CreateDirectory(appDir);
        const int TableId = 70681;

        File.WriteAllText(Path.Combine(appDir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "TME App",
          "publisher": "TME",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70681, "to": 70689 } ],
          "runtime": "14.0"
        }
        """);
        // Table and tableextension in the SAME app.json — the shape that must now relax.
        File.WriteAllText(Path.Combine(appDir, "Objects.al"), """
        table 70681 "TME Base Thing"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Description; Text[50]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        tableextension 70682 "TME Add Only Ext" extends "TME Base Thing"
        {
            fields
            {
                field(50; "Extra Note"; Text[30])
                {
                    DataClassification = EndUserIdentifiableInformation;
                    Editable = false;
                }
            }
            keys
            {
                key(ByExtraNote; "Extra Note") { }
            }
        }

        codeunit 70681 "TME Tests"
        {
            Subtype = Test;

            [Test]
            procedure ExtensionFieldAndKeySurviveViaBcsDocument()
            var
                Thing: Record "TME Base Thing";
                RRef: RecordRef;
                KRef: KeyRef;
                FRef: FieldRef;
            begin
                Thing.Init();
                Thing."Entry No." := 1;
                Thing."Extra Note" := 'Hello';
                Thing.Insert();
                Thing.Get(1);
                if Thing."Extra Note" <> 'Hello' then
                    Error('Extra Note=%1, expected Hello — the extension field did not round-trip.',
                          Thing."Extra Note");

                RRef.Open(70681);
                if RRef.KeyCount() <> 3 then
                    Error('KeyCount=%1, expected 3 (PK, ByExtraNote, and the platform key). ' +
                          'A lower count means the extension''s key was dropped.', RRef.KeyCount());
                KRef := RRef.KeyIndex(2);
                FRef := KRef.FieldIndex(1);
                if FRef.Number() <> 50 then
                    Error('Key 2 starts on field %1, expected 50 (Extra Note) — the extension''s key.',
                          FRef.Number());
                RRef.Close();
            end;
        }
        """);

        // Cold, then warm on the SAME --cache dir: Emit (which registers the document) runs
        // only on a compile-cache MISS, and DependencyLoader/AL-output-cache replay is a
        // separate path from the cold Emit one — see TableMetadataFromBcDocumentTests's own
        // header. A warm-only assertion would miss a replay that silently lost the extension's
        // field/key; a cold-only one would miss a replay that silently lost the ROUTE.
        void AssertExtensionSurvivesViaBcsDocument(string output, string phase)
        {
            Assert.True(output.Contains($"[table-metadata] {TableId} source=bc-document"),
                $"{phase}: table {TableId} was not built from BC's document.\n{output}");

            var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            var field50 = lines.LastOrDefault(
                l => l.StartsWith($"[table-metadata] {TableId} field=50 ", StringComparison.Ordinal))
                ?? throw new Xunit.Sdk.XunitException(
                    $"{phase}: no trace line for the extension's field 50.\n{output}");
            Assert.True(
                field50.Contains(" editable=False") && field50.Contains(" dataClassification=EndUserIdentifiableInformation"),
                $"{phase}: the extension's field 50 must carry its own declared "
                + $"Editable/DataClassification, read straight off BC's document.\n{field50}");
        }

        var alCacheDir = Path.Combine(scratch, "al-out");
        var (cold, coldExit) = RunRunner(appDir, alCacheDir);
        Assert.True(coldExit == 0 && cold.Contains("1P/0F/0E"), $"cold run must pass:\n{cold}");
        AssertExtensionSurvivesViaBcsDocument(cold, "cold run");

        var (warm, warmExit) = RunRunner(appDir, alCacheDir);
        Assert.True(warmExit == 0 && warm.Contains("1P/0F/0E"), $"warm run must pass:\n{warm}");
        Assert.Contains("[cache] HIT", warm);
        AssertExtensionSurvivesViaBcsDocument(warm, "warm run (AL-output cache HIT)");
    }

    /// <summary>
    /// #3600's other half: a tableextension declaring <c>modify(...)</c> keeps the table on the
    /// derivation even though it is same-app and even though it also ADDS a field — a mixed
    /// extension is not "mostly safe", because <c>modify(...)</c>'s <c>&lt;FieldChange&gt;</c>
    /// only ever lands in the extension's own delta document, never in the base table's.
    ///
    /// What this does NOT claim: that the runner applies <c>modify(...)</c>'s property change
    /// (here, Description's ToolTip) at all — it does not, on either route, before
    /// or after this change; nothing in the AL-source parser keeps a
    /// <c>FieldModificationSyntax</c>'s property list past detecting its PRESENCE (see
    /// <c>TryParseTableExtensionFile</c>). That is a separate, pre-existing gap, filed
    /// separately. What this test proves is narrower and is the thing #3600 could get wrong:
    /// that detecting the modify(...) still routes to the derivation, and that doing so does not
    /// cost the extension's OTHER, legitimately-applied content — its added field.
    /// </summary>
    [SkippableFact]
    public void SameAppExtensionDeclaringModify_StillTakesTheDerivation_AndKeepsItsAddedField()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-table-metadata-sameapp-modify");
        var appDir = Path.Combine(scratch, "app");
        Directory.CreateDirectory(appDir);
        const int TableId = 70691;

        File.WriteAllText(Path.Combine(appDir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "TMM App",
          "publisher": "TMM",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70691, "to": 70699 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(appDir, "Objects.al"), """
        table 70691 "TMM Base Thing"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Description; Text[50]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        tableextension 70692 "TMM Modify Ext" extends "TMM Base Thing"
        {
            fields
            {
                field(50; "Extra Flag"; Boolean) { }
                modify(Description)
                {
                    ToolTip = 'Changed by the extension.';
                }
            }
        }

        codeunit 70691 "TMM Tests"
        {
            Subtype = Test;

            [Test]
            procedure AddedFieldSurvivesAlongsideAModifyBlock()
            var
                Thing: Record "TMM Base Thing";
            begin
                Thing.Init();
                Thing."Entry No." := 1;
                Thing."Extra Flag" := true;
                Thing.Insert();
                Thing.Get(1);
                if not Thing."Extra Flag" then
                    Error('Extra Flag did not round-trip — the extension''s added field was lost.');
            end;
        }
        """);

        var (output, exit) = RunRunner(appDir, Path.Combine(scratch, "al-out"));
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"), $"run must pass:\n{output}");

        // The modify(...) block is what must keep this on the derivation, not the lack of a
        // document: table 70691 IS compiled by this run, so #3548 captures one for it — the
        // guard has to actively exclude it.
        Assert.Contains($"[table-metadata] {TableId} source=derived", output);
        Assert.DoesNotContain($"[table-metadata] {TableId} source=bc-document", output);

        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var field50 = lines.LastOrDefault(
            l => l.StartsWith($"[table-metadata] {TableId} field=50 ", StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"no trace line for the extension's field 50.\n{output}");
        Assert.Contains(" dataClassification=CustomerContent", field50);
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
