// MissingCodeunitDependencyTableTests — the second half of #3399.
//
// _knownDependencyCodeunits maps a well-known codeunit id to the name and package
// BuildMissingCodeunitMessage prints. Nothing checked it against the shipped packages, and
// four of its six rows were wrong — a table whose whole job is to tell a user which .app to
// go and find.
//
// HOW THE CORRECT VALUES WERE ESTABLISHED
//   Every Microsoft .app in ~/.al-runner/test-apps and ~/.al-runner/platform-apps was opened
//   (a BC .app is a NAVX-prefixed zip, and the runtime packages nest a second .app carrying
//   src/*.al), and the `codeunit <id> <name>` declarations were read out of the AL sources —
//   113 app files, 12,540 .al sources, BC 28.1.49838.53910. SymbolReference.json alone is NOT
//   sufficient: the runtime platform apps ship without one, which is why a symbols-only sweep
//   returned "not found" for 310/130000/130440/131000 and would have read as a finding.
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
//   Test Runner's codeunits are 130450-1304xx (Microsoft_Test Runner.app), not 130500.
//   Library - Variable Storage is 131004 (Microsoft_Library Variable Storage.app), not 130440.
//
//   Cross-checked on BC 28.4.53241.54447, whose artifact set carries only test-apps: the two
//   ids it can see, 130002 and 130500, agree. The other four are unmeasurable there — which
//   is an absence of measurement, not a disagreement.
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
