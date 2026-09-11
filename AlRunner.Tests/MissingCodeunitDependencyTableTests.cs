// MissingCodeunitDependencyTableTests — the second half of #3399.
//
// _knownDependencyCodeunits maps a well-known codeunit id to the name and package
// BuildMissingCodeunitMessage prints. Nothing checked it against the shipped packages, and
// four of its six rows were wrong — a table whose whole job is to tell a user which .app to
// go and find.
//
// HOW THE CORRECT VALUES WERE ESTABLISHED
//   Every Microsoft .app in ~/.al-runner/test-apps and ~/.al-runner/platform-apps was opened
//   (a BC .app is a NAVX-prefixed zip) and the `codeunit <id> <name>` declarations were read
//   out of both SymbolReference.json and the AL sources — 113 app files, BC 28.1.49838.53910.
//   Where both answer they agree exactly, on all six ids; the trap below is about making
//   symbols answer at all.
//
//   THE TRAP: reaching these ids needs TWO INDEPENDENT RECURSIONS, and implementing one
//   without the other answers a strict subset while looking like a complete result. All 113
//   apps carry a SymbolReference.json — none is missing one — so every failure here is the
//   sweep's, not the packages'. Measured over the 113 apps:
//
//     recurse nested .app?  recurse Namespaces?   ids found of the six
//     no                    no                    none
//     YES                   no                    130000, 130440, 131000
//     no                    YES                   130002, 130500
//     YES                   YES                   all six
//
//   The two axes are unrelated, and 310 needs BOTH — which is why it is the only id no
//   single-axis reader finds. Four apps (Application Test Library, Base Application, Business
//   Foundation, System Application) NEST a second .app carrying the real SymbolReference.json;
//   an outer-only sweep sees 109 of 113 and never opens them. Separately, a symbol file may
//   leave the TOP-LEVEL Codeunits array empty and put objects under Namespaces, so a reader
//   taking sr["Codeunits"] alone gets nothing from it. Per id:
//
//     130002, 130500      outer .app, Namespaces depth 3   -> needs the Namespaces walk
//     130000, 130440,     nested .app, depth 0 (42 top-    -> needs the .app recursion
//       131000              level codeunits in that file)
//     310                 nested .app, Namespaces depth 3  -> needs both
//
//   So neither shape is the rule, and neither axis alone is a safe default.
//
//   Both were implemented separately while establishing this table, and each looked right:
//   one found three ids, the other found two, and only doing both finds six. Check the app
//   count you scanned AND that you descended Namespaces; a zero from either is a property of
//   the reader. Reading the `codeunit <id> <name>` declarations out of the .al sources needs
//   neither recursion to be got right, which is why it was used as the independent check.
//
//   The measured answers, and what the table used to claim:
//
//     id      measured name             measured package            table said
//     310     No. Series                Business Foundation         "Base Application"      WRONG package
//     130000  Assert                    Application Test Library    "Library Assert (…)"    WRONG package
//     130002  Library Assert            Library Assert              Library Assert          correct
//     130440  Library - Random          Application Test Library    "Library Variable
//                                                                    Storage"               WRONG name+package
//     130500  Any                       Any                         "Test Runner"           WRONG name+package
//     131000  Library - Utility         Application Test Library    "Library - Test
//                                                                    Initialize"            WRONG name+package
//
//   130440 and 130500 are the pair #3399 flagged: docs/limitations.md had named them
//   Library - Random and Any before that entry was removed, and the doc was right on both.
//   Test Runner's codeunits are 130450-130471 (Microsoft_Test Runner.app), not 130500.
//   Library - Variable Storage is 131004 (Microsoft_Library Variable Storage.app), not 130440.
//
//   Cross-checked on BC 28.4.53241.54447 with the same nested-aware reader: that artifact set
//   carries only test-apps, so the two ids it can see — 130002 and 130500 — agree, and the
//   other four are absent from it. That absence is a missing measurement, not a disagreement.
//
// WHY PIN IT HERE rather than re-read the packages at test time: the packages are not present
// on every box that runs the unit suite, and a test that silently skips when they are missing
// asserts nothing. This pins the measurement; a BC version that genuinely moves one of these
// ids should fail here and be re-measured.

using System.Collections.Generic;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class MissingCodeunitDependencyTableTests
{
    /// <summary>
    /// id → (name, the package the id actually ships in), measured as described above.
    /// </summary>
    public static TheoryData<int, string, string> Measured => new()
    {
        { 310,    "No. Series",       "Business Foundation" },
        { 130000, "Assert",           "Application Test Library" },
        { 130002, "Library Assert",   "Library Assert" },
        { 130440, "Library - Random", "Application Test Library" },
        { 130500, "Any",              "Any" },
        { 131000, "Library - Utility","Application Test Library" },
    };

    [Theory]
    [MemberData(nameof(Measured))]
    public void TableRow_MatchesTheShippedPackage(int id, string name, string package)
    {
        var table = BcRuntime.KnownDependencyCodeunitsForTests;

        Assert.True(table.ContainsKey(id), $"codeunit {id} ({name}) is missing from _knownDependencyCodeunits");
        Assert.Equal(name, table[id].Name);
        Assert.Equal(package, table[id].Package);
    }

    [Fact]
    public void Table_CarriesNoRowThatWasNotMeasured()
    {
        // A row nobody measured is the defect #3399 reported, one row later. Adding one means
        // measuring it and extending Measured above.
        var measured = new HashSet<int>();
        foreach (var row in Measured) measured.Add((int)row[0]);

        foreach (var id in BcRuntime.KnownDependencyCodeunitsForTests.Keys)
            Assert.True(measured.Contains(id),
                $"codeunit {id} was added to _knownDependencyCodeunits without a measured row here");
    }

    [Fact]
    public void NoRow_ClaimsAnIdThatBelongsToADifferentToolkitApp()
    {
        var table = BcRuntime.KnownDependencyCodeunitsForTests;

        // The two specific inversions #3399 asked about, pinned by the thing that makes them
        // wrong rather than only by the corrected value.
        Assert.DoesNotContain("Test Runner", table[130500].Name);      // 130500 is Any; Test Runner starts at 130450
        Assert.DoesNotContain("Variable Storage", table[130440].Name); // Library - Variable Storage is 131004
    }
}
