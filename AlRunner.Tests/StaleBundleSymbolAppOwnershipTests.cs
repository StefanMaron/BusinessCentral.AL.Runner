// StaleBundleSymbolAppOwnershipTests — issue #3049.
//
// WHAT THIS PINS, AND WHY IT CANNOT GO UPSTREAM
//   A real service tier publishes an app from the artifact its compiler just produced, so
//   "the app's own .app package is out of date relative to its source" is not a state BC can
//   be in and not a claim the corpus could adjudicate. It is a state the RUNNER is routinely
//   in: RecordPatches.RegisterBundleSymbolApps deliberately registers a prebuilt `.app` found
//   in the bundle root — it is where a bundle's own BC-compiler-assigned query column ids come
//   from — while BcCompiler compiles that same app from source. The al-language corpus ships
//   exactly such a package, `AL Language_AL Language Coverage Tests_1.0.0.0.app`, last rebuilt
//   at corpus PR #7 and listing 191 objects.
//
//   RecordPatches.BuildObjectOwnerIndex used to read an app's objects from a symbol reference
//   OR from its emitted assembly, never both, skipping any assembly whose app id a symbol
//   reference had already answered for. Every object added to the source since that package
//   was built therefore had NO owner (Guid.Empty) in AllObj, and real System Application code
//   — Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable, which compares
//   AllObj."App Runtime Package ID" against the caller's Published Application row — declined
//   the app its own table.
//
//   The BC-behaviour half of that is upstream and adjudicated on eight real BC legs:
//   Codeunit 60405 "Test Module Owns Own Table".AppCanRegisterItsOwnTableOnTheAllowedList
//   (BusinessCentral.AL.Language.Tests#181). It reproduces here only because the corpus
//   happens to carry a stale package — an accident of that repository, not something this
//   repository controls. Rebuilding it upstream would make the corpus test pass again with the
//   defect fully intact. So the condition is constructed deliberately here instead.
//
// HOW THE FIXTURE PROVES THE CONDITION IS REALLY PRESENT
//   The stale SymbolReference.json declares a GHOST table (68105) that exists in no source
//   file. Nothing but a registered, parsed bundle-root package can put that id into AllObj, so
//   `GhostTableFromTheStaleAppIsVisibleAndOwned` failing means the fixture never reproduced the
//   condition and the other assertions would be vacuous. The suite fails rather than passing
//   quietly, per .claude/rules/loud-failures.md.
//
// no-base-app-in-csharp-tests.md: the manifest below declares `platform` and never
// `application`. `target` is OnPrem because `Record "Published Application"` has scope OnPrem
// and reports AL0296 in a Cloud-target app — the same reason
// tests/runner-extras/published-application-system-table targets OnPrem.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class StaleBundleSymbolAppOwnershipTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const int ListedTableId = 68101;    // in source AND in the stale symbol reference
    private const int UnlistedTableId = 68102;  // in source ONLY — the regression case
    private const int TestCodeunitId = 68103;
    private const int GhostTableId = 68105;     // in the stale symbol reference ONLY

    [SkippableFact]
    public void SourceObjectMissingFromABundleRootApp_IsStillOwnedByThatApp()
    {
        TestArtifacts.SkipIfMissing();

        var bundleDir = Path.Combine(TestScratch.Dir("al-runner-stale-bundle-app-3049"), "stale-owner");
        var appId = Guid.NewGuid();
        WriteFixture(bundleDir, appId);

        var (output, exit) = RunRunner(bundleDir);

        // Every assertion below is written as an AL `Error`, so a FAIL line carries the reason.
        Assert.DoesNotContain("FAIL", output);
        Assert.DoesNotContain("ERROR", output);

        // The fixture guard first: without it the three ownership tests would be vacuous.
        Assert.Contains("PASS  Codeunit68103.GhostTableFromTheStaleAppIsVisibleAndOwned", output);
        Assert.Contains("PASS  Codeunit68103.SourceOnlyTableIsOwnedByThisApp", output);
        Assert.Contains("PASS  Codeunit68103.TableInBothSourceAndTheStaleAppIsOwnedByThisApp", output);
        Assert.Contains("PASS  Codeunit68103.APlatformTableIsNotOwnedByThisApp", output);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// Write the bundle sources, then emit a bundle-root <c>.app</c> whose
    /// SymbolReference.json is deliberately SHORT: it names the listed table and a ghost, and
    /// omits the unlisted one — the shape a package that predates a source change has.
    /// </summary>
    private static void WriteFixture(string bundleDir, Guid appId)
    {
        Directory.CreateDirectory(bundleDir);

        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "SBAO Stale Bundle App Owner",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "27.0.0.0",
          "idRanges": [ { "from": 68100, "to": 68109 } ],
          "runtime": "15.0",
          "target": "OnPrem"
        }
        """);

        File.WriteAllText(Path.Combine(bundleDir, "Listed.Table.al"), $$"""
        table {{ListedTableId}} "SBAO Listed"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "Entry No."; Integer) { DataClassification = SystemMetadata; } }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(bundleDir, "Unlisted.Table.al"), $$"""
        table {{UnlistedTableId}} "SBAO Unlisted"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "Entry No."; Integer) { DataClassification = SystemMetadata; } }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(bundleDir, "Tests.Codeunit.al"), $$"""
        codeunit {{TestCodeunitId}} "SBAO Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure OwnRuntimePackageId(): Guid
            var
                PublishedApplication: Record "Published Application";
                Mi: ModuleInfo;
            begin
                NavApp.GetCurrentModuleInfo(Mi);
                PublishedApplication.SetRange(ID, Mi.Id());
                if not PublishedApplication.FindFirst() then
                    Error('This bundle has no Published Application row of its own.');
                exit(PublishedApplication."Runtime Package ID");
            end;

            [Test]
            procedure GhostTableFromTheStaleAppIsVisibleAndOwned()
            var
                AllObj: Record AllObj;
                Empty: Guid;
            begin
                // The fixture guard. Table {{GhostTableId}} exists in NO source file: it is named only
                // by the bundle-root .app's SymbolReference.json. If it is not in AllObj, that
                // package was never registered and the three tests below prove nothing.
                if not AllObj.Get(AllObj."Object Type"::Table, {{GhostTableId}}) then
                    Error('The bundle-root .app was not registered: AllObj has no row for the ghost table {{GhostTableId}}.');
                if AllObj."App Runtime Package ID" = Empty then
                    Error('An object named by this app''s own symbol reference must carry an owner.');
                if AllObj."App Runtime Package ID" <> OwnRuntimePackageId() then
                    Error('The ghost table must be owned by this app, not by another one.');
            end;

            [Test]
            procedure SourceOnlyTableIsOwnedByThisApp()
            var
                AllObj: Record AllObj;
                Empty: Guid;
            begin
                // #3049: table {{UnlistedTableId}} is compiled from source in this process and is absent
                // from the stale bundle-root .app. Before the fix its AllObj owner was
                // Guid.Empty, and every System Application ownership check on it declined.
                if not AllObj.Get(AllObj."Object Type"::Table, Database::"SBAO Unlisted") then
                    Error('AllObj has no row for a table this bundle compiled from source.');
                if AllObj."App Runtime Package ID" = Empty then
                    Error('A table this app compiled from source must not be left unowned because its bundle-root .app is stale.');
                if AllObj."App Runtime Package ID" <> OwnRuntimePackageId() then
                    Error('A table this app compiled from source must carry this app''s runtime package id.');
            end;

            [Test]
            procedure TableInBothSourceAndTheStaleAppIsOwnedByThisApp()
            var
                AllObj: Record AllObj;
            begin
                // The half that already worked, kept so a fix that swapped one gap for another
                // cannot pass: reading the assembly must not cost the symbol reference's answer.
                if not AllObj.Get(AllObj."Object Type"::Table, Database::"SBAO Listed") then
                    Error('AllObj has no row for the table named in both source and the .app.');
                if AllObj."App Runtime Package ID" <> OwnRuntimePackageId() then
                    Error('A table named in both source and this app''s symbol reference must carry this app''s runtime package id.');
            end;

            [Test]
            procedure APlatformTableIsNotOwnedByThisApp()
            var
                AllObj: Record AllObj;
            begin
                // The negative direction, and what stops "stamp everything with this app's id"
                // passing the three tests above. AllObjWithCaption (2000000058) is a platform
                // object this bundle plainly does not own.
                if not AllObj.Get(AllObj."Object Type"::Table, Database::AllObjWithCaption) then
                    Error('AllObj has no row for AllObjWithCaption.');
                if AllObj."App Runtime Package ID" = OwnRuntimePackageId() then
                    Error('A platform table must not carry this app''s runtime package id — this app would then own it.');
            end;
        }
        """);

        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(bundleDir, "app.json"))
            ?? throw new InvalidOperationException("could not read the identity just written");

        InProcessAppPackager.EmitAppPackageToFile(
            bundleDir, identity,
            Path.Combine(bundleDir, "AL Runner_SBAO Stale Bundle App Owner_1.0.0.0.app"),
            StaleSymbolReference(appId));
    }

    /// <summary>
    /// A SymbolReference.json for this app that is deliberately out of date: it names the
    /// listed table and a ghost the source no longer has, and does NOT name the unlisted one.
    /// Container names match what BcAppSymbolCache parses, so the package is registered rather
    /// than present-and-ignored.
    /// </summary>
    private static byte[] StaleSymbolReference(Guid appId)
    {
        var doc = new
        {
            AppId = appId.ToString(),
            Name = "SBAO Stale Bundle App Owner",
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = new object[]
            {
                new
                {
                    Id = ListedTableId,
                    Name = "SBAO Listed",
                    Fields = new object[]
                    {
                        new { Id = 1, Name = "Entry No.", TypeDefinition = new { Name = "Integer" } },
                    },
                },
                new
                {
                    Id = GhostTableId,
                    Name = "SBAO Ghost",
                    Fields = new object[]
                    {
                        new { Id = 1, Name = "Entry No.", TypeDefinition = new { Name = "Integer" } },
                    },
                },
            },
            Codeunits = Array.Empty<object>(),
            Pages = Array.Empty<object>(),
            EnumTypes = Array.Empty<object>(),
            Queries = Array.Empty<object>(),
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc));
    }

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }
}
