// SymbolAppFixture — build a REGISTRABLE .app package in-process, for tests that need one.
//
// The capability this adds, and why it was missing
// -----------------------------------------------
// `RecordPatches._bcAppPaths` — the list every derived table/extension/query index is rebuilt
// from — is populated only by `AddBcAppPath`, and its bundle-facing feeder
// `RecordPatches.RegisterBundleSymbolApps` registers a `.app` ONLY when
// `AppLoader.HasSymbolReference` is true. Two things follow, both measured rather than assumed,
// and each one silently defeats the obvious fixture:
//
//   * A bundle built from `.al` sources alone never touches `_bcAppPaths` at all. Its tables
//     live in `_parsedTables`.
//   * A .app synthesized by the LAYERED PRE-PASS does not either. Inspected byte-for-byte
//     (`AL_Runner_Watch_Schema_App_1_0_0_0.app`, 649 bytes): its zip holds exactly
//     `NavxManifest.xml` and `src/Tbl.al`, and no `SymbolReference.json`. SiblingCompile's
//     call to EmitAppPackageToFile passes no symbol reference, so nothing the runner
//     synthesizes for a dependency is registrable.
//
// So `_bcAppPaths` only ever holds packages carrying a real SymbolReference — and until now
// there was no way for a test to produce one: `AlRunner.Tests` contains zero checked-in `.app`
// files. Any question about `.app` registration, dependency symbol resolution or
// `_bcAppPaths` accumulation (#2755) hits that wall first.
//
// Why built rather than checked in
// --------------------------------
// `InProcessAppPackager.EmitAppPackageToFile` is the repo's own supported synthesis path,
// public, already used by the layered pre-pass, and it takes an optional `symbolReferenceJson`
// — so producing a registrable package needs no new machinery, only the argument the pre-pass
// declines to pass. Building it costs milliseconds in-process, with no subprocess: the C# suite
// already spawns the runner about 130 times and `no-base-app-in-csharp-tests.md` records that
// subprocess time as its single largest cost, so adding a spawn per test was not worth it.
//
// A checked-in binary would be faster still and deterministic, and it was rejected: it freezes
// one BC version's package shape into an artifact tested against eight BC legs, and somebody
// has to notice and regenerate it when the format moves. That is the same
// cached-artifact-diverges-from-the-pipeline hazard `precompiled-dll-respect.md` warns about,
// traded for a saving this does not need.
//
// `no-base-app-in-csharp-tests.md`: the manifest below declares `platform` and never
// `application`, so nothing here acquires the Base Application floor.
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

internal static class SymbolAppFixture
{
    /// <summary>A minimal but REAL SymbolReference.json declaring one table with two fields.
    /// The container names match what BcAppSymbolCache actually parses ("Tables", and the
    /// object containers beside it), so the package is not merely present-and-ignored.</summary>
    internal static byte[] SymbolReferenceForTable(Guid appId, string appName, int tableId, string tableName)
    {
        var doc = new
        {
            AppId = appId.ToString(),
            Name = appName,
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = new[]
            {
                new
                {
                    Id = tableId,
                    Name = tableName,
                    Fields = new object[]
                    {
                        new { Id = 1, Name = "Entry No.", TypeDefinition = new { Name = "Integer" } },
                        new { Id = 2, Name = "Payload",   TypeDefinition = new { Name = "Text", Length = 30 } },
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

    /// <summary>
    /// Write a one-table AL bundle under <paramref name="bundleDir"/> and emit it as a
    /// <c>.app</c> at <paramref name="outAppPath"/>.
    /// </summary>
    /// <param name="withSymbolReference">
    /// When false the package is emitted exactly as the layered pre-pass emits one — manifest
    /// plus sources, no SymbolReference — which is NOT registrable. That is the negative arm
    /// callers use to prove a registration assertion has teeth, so it is a parameter rather
    /// than a separate near-copy of this method.
    /// </param>
    internal static void WriteBundleAndApp(
        string bundleDir,
        string outAppPath,
        Guid appId,
        string appName,
        int tableId,
        string tableName,
        bool withSymbolReference)
    {
        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "{{appName}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{tableId}}, "to": {{tableId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundleDir, "Tbl.al"), $$"""
        table {{tableId}} "{{tableName}}"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(2; Payload; Text[30]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);

        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(bundleDir, "app.json"))
            ?? throw new InvalidOperationException("SymbolAppFixture: could not read the identity it just wrote");

        Directory.CreateDirectory(Path.GetDirectoryName(outAppPath)!);
        InProcessAppPackager.EmitAppPackageToFile(
            bundleDir, identity, outAppPath,
            withSymbolReference ? SymbolReferenceForTable(appId, appName, tableId, tableName) : null);
    }
}
