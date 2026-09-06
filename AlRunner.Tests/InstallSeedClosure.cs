// InstallSeedClosure — the fixture shape #1867's two caching suites need, WITHOUT the Base
// Application floor (#2364).
//
// WHAT THE CALLERS ACTUALLY NEED, AND WHY IT IS NOT BASE APPLICATION
//   InstallBaselineDiskCacheTests and InstallSeedDepCompanyCacheTests are about the CACHING of
//   install-trigger state. The one thing that caching needs in order to be observable is a
//   dependency closure whose install triggers WRITE ROWS: with an empty snapshot the codec
//   logs `not persisting: snapshot has 0 DataAccessSource(s), expected exactly 1`, nothing is
//   written, no DISK-HIT/DISK-WRITE marker exists, and every assertion in both suites would
//   pass having observed nothing.
//
//   Both suites used to buy that closure by declaring `"application"` in their generated
//   manifests, which loads the whole Base Application closure on every runner invocation
//   (~70 s cold, ~6 s warm each — .claude/rules/no-base-app-in-csharp-tests.md). Neither suite
//   ever asserted anything about Base Application; it was Base App's own install-time writes
//   they were riding on. A dependency app carrying ONE table and ONE Subtype=Install codeunit
//   that inserts two rows produces the same non-empty snapshot for none of the cost.
//
//   Measured on this fixture (BC 28.1, perf stat instructions-retired, two bundles in both
//   arms so only the floor differs): 249.6e9 -> 63.7e9 instructions cold (-74.5%) and
//   47.9e9 -> 14.9e9 warm (-68.9%); wall 33.9 s -> 10.9 s cold and 6.9 s -> 2.8 s warm.
//
// THE SEED IS A DEPENDENCY, NOT A BUNDLE
//   Only <see cref="Closure.BundleDir"/> is handed to the runner. The seed app sits beside it
//   and is picked up by SiblingCompile.BuildSiblingSourceDeps, which compiles a declared
//   dependency that ships only as AL source in a sibling directory. That matters for the
//   assertions rather than for speed (measured: one app group vs two is within noise, 14.8e9
//   vs 14.9e9 instructions): passing the seed as its own bundle would give it an app group of
//   its own whose dependency closure is EMPTY, so it seeds nothing, is never persisted, and
//   logs a fresh MISS on every process forever — which would force "no key was recomputed"
//   to be written as a weaker key-set intersection instead of the flat count it can be here.
//
// EVERY CLOSURE IS UNIQUE TO ITS INVOCATION
//   The seed app's id and its seeded Description both carry a fresh GUID, so the dependency
//   assembly compiles to an MVID no earlier run has produced — and therefore to an
//   InstallTriggerRunner.CurrentDependencySetKey(), and an on-disk entry, that is guaranteed
//   absent at the start. Without that, neither suite could tell a genuine first-run MISS from
//   a hit on an entry some earlier run left behind.
//
// THE AL ASSERTION IS THE NON-VACUITY GUARD
//   The bundle's test reads the seeded rows back by VALUE — the marker text, a positive
//   decimal, a negative decimal, and the row count. A cache that restored nothing, or restored
//   a truncated or re-lengthened value, fails that test rather than merely running fast. It is
//   deliberately not an `Assert.IsTrue(true)`-shaped "the row exists" check: see
//   .claude/rules/tdd.md.

namespace AlRunner.Tests;

internal static class InstallSeedClosure
{
    /// <summary>
    /// One writable dependency closure on disk.
    /// </summary>
    /// <param name="BundleDir">The directory to hand the runner. The seed app is NOT in this
    /// list — it is resolved as a sibling source dependency.</param>
    /// <param name="SeedDir">The seed app's directory, for tests that need to name it.</param>
    /// <param name="Marker">The GUID text the seed writes into row SEED-1's Description, and
    /// the bundle's test asserts back. Unique per call.</param>
    internal sealed record Closure(string BundleDir, string SeedDir, string Marker);

    /// <summary>The rows the seed app's OnInstallAppPerCompany trigger inserts.</summary>
    internal const int SeededRowCount = 2;

    /// <summary>
    /// Writes <c>&lt;root&gt;/&lt;tag&gt;/seed</c> and <c>&lt;root&gt;/&lt;tag&gt;/main</c> and
    /// returns them. Each closure gets its OWN parent directory so that sibling-source-dep
    /// discovery, which scans the parent of every bundle root, cannot offer one closure's seed
    /// app to another closure's bundle.
    /// </summary>
    /// <param name="root">A scratch directory owned by the caller.</param>
    /// <param name="tag">A short identifier, unique within <paramref name="root"/>. Becomes part
    /// of every AL object name, so two closures in one runner invocation never collide.</param>
    /// <param name="baseId">First object id. Uses <paramref name="baseId"/> (table),
    /// <c>+1</c> (install codeunit) and <c>+5</c> (test codeunit); callers must leave 10 ids
    /// clear.</param>
    internal static Closure Write(string root, string tag, int baseId)
    {
        var parent = Path.Combine(root, tag);
        var seedDir = Path.Combine(parent, "seed");
        var mainDir = Path.Combine(parent, "main");
        var seedId = Guid.NewGuid().ToString();
        var marker = Guid.NewGuid().ToString("N");
        var seedName = $"Seed {tag}";

        WriteSeedApp(seedDir, tag, baseId, seedId, seedName, marker);
        WriteBundle(mainDir, tag, baseId, marker, ($"{seedId}", seedName));
        return new Closure(mainDir, seedDir, marker);
    }

    /// <summary>
    /// The same closure, but with TWO bundles sharing the one seed app — so both app groups
    /// resolve to the SAME InstallTriggerRunner.CurrentDependencySetKey(). That identity is
    /// what <c>InstallSeedDepCompanyCacheTests</c>'s HIT direction is about, so it has to come
    /// from the apps genuinely sharing a dependency rather than from the two happening to
    /// declare nothing.
    /// </summary>
    /// <returns>The two bundle directories, in the order they should be passed.</returns>
    internal static (string first, string second, string marker) WriteSharedClosure(
        string root, string tag, int baseId)
    {
        var parent = Path.Combine(root, tag);
        var seedId = Guid.NewGuid().ToString();
        var marker = Guid.NewGuid().ToString("N");
        var seedName = $"Seed {tag}";

        WriteSeedApp(Path.Combine(parent, "seed"), tag, baseId, seedId, seedName, marker);
        var a = Path.Combine(parent, "main-a");
        var b = Path.Combine(parent, "main-b");
        WriteBundle(a, tag + "A", baseId, marker, (seedId, seedName));
        WriteBundle(b, tag + "B", baseId + 10, marker, (seedId, seedName));
        return (a, b, marker);
    }

    /// <summary>
    /// A bundle whose dependency closure is the shared seed PLUS one extra dependency app, so
    /// its key differs from <see cref="WriteSharedClosure"/>'s by exactly one assembly. The
    /// extra app carries a table and no install trigger: the point is to change the dependency
    /// SET, not what it seeds.
    /// </summary>
    internal static string WriteBundleWithExtraDependency(
        string root, string tag, int baseId, string seedTag)
    {
        var parent = Path.Combine(root, seedTag);      // beside the shared seed app
        var seedName = $"Seed {seedTag}";
        var seedId = ReadAppId(Path.Combine(parent, "seed", "app.json"));
        var marker = ReadMarker(Path.Combine(parent, "seed", "Seed.al"));

        var extraId = Guid.NewGuid().ToString();
        var extraName = $"Extra {tag}";
        var extraDir = Path.Combine(parent, "extra-" + tag);
        Directory.CreateDirectory(extraDir);
        File.WriteAllText(Path.Combine(extraDir, "app.json"), $$"""
        {
          "id": "{{extraId}}",
          "name": "{{extraName}}",
          "publisher": "AL Runner Install Seed",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 4}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(extraDir, "Extra.al"), $$"""
        table {{baseId}} "Extra {{tag}} Table"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """);

        var mainDir = Path.Combine(parent, "main-" + tag);
        WriteBundle(mainDir, tag, baseId + 5, marker, (seedId, seedName), (extraId, extraName));
        return mainDir;
    }

    // ── the two app shapes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The seed app: one table, and one Subtype=Install codeunit whose OnInstallAppPerCompany
    /// trigger inserts <see cref="SeededRowCount"/> rows into it. That trigger is what
    /// InstallTriggerRunner.RunDependenciesOnly() fires, inside the window
    /// RecordPatches.CaptureInstallBaselineSnapshot() then captures — which is the whole reason
    /// the snapshot is non-empty and the disk tier has something to persist.
    ///
    /// The rows carry a positive decimal, a negative one and a blank text alongside the marker
    /// so the round-trip digest is over a value set with more than one NclType in it.
    /// </summary>
    private static void WriteSeedApp(
        string dir, string tag, int baseId, string appId, string appName, string marker)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "{{appName}}",
          "publisher": "AL Runner Install Seed",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 4}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Seed.al"), $$"""
        table {{baseId}} "Seed {{tag}} Table"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Description"; Text[50]) { }
                field(3; "Amount"; Decimal) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit {{baseId + 1}} "Seed {{tag}} Install"
        {
            Subtype = Install;

            // Fired by InstallTriggerRunner.RunDependenciesOnly() for every app group whose
            // dependency closure contains this app — the exact work the #1867 baseline cache
            // exists to avoid repeating, and the only reason the captured snapshot has rows.
            trigger OnInstallAppPerCompany()
            var
                SeedRow: Record "Seed {{tag}} Table";
            begin
                SeedRow.Init();
                SeedRow.Code := 'SEED-1';
                SeedRow.Description := '{{marker}}';
                SeedRow.Amount := 42.5;
                SeedRow.Insert(true);

                SeedRow.Init();
                SeedRow.Code := 'SEED-2';
                SeedRow.Description := '';
                SeedRow.Amount := -7.25;
                SeedRow.Insert(true);
            end;
        }
        """);
    }

    /// <summary>
    /// A bundle depending on the seed app (and optionally more), whose single test reads the
    /// seeded rows back BY VALUE. Whether this app group computed the dependency+company
    /// baseline or restored it from memory or from disk, these are the values it must see; a
    /// restore that dropped rows, truncated a text or re-lengthened a decimal fails here rather
    /// than merely being fast.
    /// </summary>
    private static void WriteBundle(
        string dir, string tag, int baseId, string marker, params (string Id, string Name)[] deps)
    {
        Directory.CreateDirectory(dir);
        var depJson = string.Join(",\n    ", deps.Select(d =>
            $$"""{ "id": "{{d.Id}}", "name": "{{d.Name}}", "publisher": "AL Runner Install Seed", "version": "1.0.0.0" }"""));
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Main {{tag}}",
          "publisher": "AL Runner Install Seed",
          "version": "1.0.0.0",
          "dependencies": [
            {{depJson}}
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId + 5}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        // The seed app's table is named after the SEED's tag, which is the first dependency's
        // name minus its "Seed " prefix — the bundle tag can differ (WriteSharedClosure gives
        // its two bundles distinct tags while they share one seed).
        var seedTag = deps[0].Name["Seed ".Length..];
        File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
        codeunit {{baseId + 5}} "Main {{tag}} Test"
        {
            Subtype = Test;

            [Test]
            procedure DependencyInstallSeedIsPresentWithItsValues()
            var
                SeedRow: Record "Seed {{seedTag}} Table";
            begin
                // [THEN] Both rows the dependency's OnInstallAppPerCompany trigger inserted are
                // present, with the values it wrote. Asserted value-by-value rather than as a
                // bare Get(), so a cache tier that restored an empty or partial snapshot fails
                // here instead of passing quietly.
                if SeedRow.Count() <> {{SeededRowCount}} then
                    Error('expected {{SeededRowCount}} seeded row(s), found %1', SeedRow.Count());

                SeedRow.Get('SEED-1');
                if SeedRow.Description <> '{{marker}}' then
                    Error('SEED-1 Description was ''%1''', SeedRow.Description);
                if SeedRow.Amount <> 42.5 then
                    Error('SEED-1 Amount was %1', SeedRow.Amount);

                SeedRow.Get('SEED-2');
                if SeedRow.Description <> '' then
                    Error('SEED-2 Description was ''%1''', SeedRow.Description);
                if SeedRow.Amount <> -7.25 then
                    Error('SEED-2 Amount was %1', SeedRow.Amount);
            end;
        }
        """);
    }

    // ── reading back what Write* produced ────────────────────────────────────────────────

    private static string ReadAppId(string appJsonPath)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>The marker is written into the seed's AL source; read it back rather than
    /// threading it through every call site.</summary>
    private static string ReadMarker(string seedAlPath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(seedAlPath), @"Description := '([0-9a-f]{32})';");
        if (!m.Success)
            throw new InvalidOperationException($"no seed marker found in {seedAlPath}");
        return m.Groups[1].Value;
    }
}
