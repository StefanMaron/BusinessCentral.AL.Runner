// TestPageGoToRecordNotFoundRestoreRefreshTests — pins the C# CONTRACT issue #2537's fix
// depends on, not "what BC does" (that's the job of the companion corpus PR,
// StefanMaron/BusinessCentral.AL.Language.Tests#123, which proves the AL-observable behavior
// against real BC 27.0/27.3/27.5/28.0/28.1/28.2/28.3/28.4 -- 8/8 legs green -- once merged).
//
// NavRecord.ALSetPosition (real, unmodified BC engine code in Ncl.dll) only writes the
// primary-key columns of the record buffer from a parsed position string; it does not
// re-fetch or otherwise refresh non-key columns. FindRowFromTableFieldValues's not-found
// restore used to call `record.ALSetPosition(original); Loaded(true);` alone, which left
// non-key fields holding whatever row the internal not-found scan last visited, under the
// restored row's own key. The fix re-finds the original row through the same
// MoveFirst/MoveNextDataRow path a normal search already uses -- which refreshes every
// field via NavRecord.ALFindFirstAsync/ALNextAsync, not a key-only SetPosition -- instead of
// restoring via a raw position write. There is no reflection surface that exercises this
// without a loaded BC runtime/session, so what's provable here is that the source no longer
// takes the key-only restore shortcut.
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageGoToRecordNotFoundRestoreRefreshTests
{
    private static string MockTestPageSource()
    {
        var dir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        // FindRowFromFieldValues moved to MockTestPage.LivePage.Filters.cs in #3676's
        // mechanical split; the body this brackets is unchanged.
        var path = Path.Combine(repoRoot, "AlRunner", "Patches", "MockTestPage.LivePage.Filters.cs");
        Assert.True(File.Exists(path), $"expected to find {path}");
        return File.ReadAllText(path);
    }

    // The scan body FindRowFromTableFieldValues runs. Since #3312 the method itself is a
    // one-line delegation: FindRowFromControlFieldValue needs the same scan started from the
    // CURRENT row rather than from the top, so the body moved into a shared
    // FindRowFromFieldValues that both entry points call with a start policy. The not-found
    // restore this class pins lives in that shared body, unchanged and still reached by
    // FindRowFromTableFieldValues -- so this locator follows it rather than asserting the
    // body sits inline under the public signature, which is a detail #2537 never claimed.
    private static string FindRowFromTableFieldValuesBody(string source)
    {
        var start = source.IndexOf("private bool FindRowFromFieldValues(int[] fieldNos, object[] values, bool forward, bool startFromCurrentRow)", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not locate FindRowFromFieldValues in MockTestPage.cs");

        // Take a generous window past the signature -- enough to contain the whole method
        // body without needing a real brace-matcher for a test this narrow. Widened with
        // #3312, which added the start-policy commentary ahead of the scan; 4000 stopped
        // short of the not-found restore this class is actually about.
        var window = source.Substring(start, Math.Min(9000, source.Length - start));
        return window;
    }

    [Fact]
    public void NotFoundRestore_NoLongerUsesTheKeyOnlyPositionWrite()
    {
        var body = FindRowFromTableFieldValuesBody(MockTestPageSource());

        // This exact shape -- ALSetPosition immediately followed by Loaded(true) as the
        // ENTIRE not-found restore -- is what left non-key fields stale, because
        // ALSetPosition alone never re-reads the row's non-key columns.
        var oldShape = Regex.IsMatch(body, @"record\.ALSetPosition\(original\);\s*Loaded\(true\);");
        Assert.False(oldShape,
            "FindRowFromTableFieldValues's not-found path still restores via a bare " +
            "ALSetPosition(original) + Loaded(true) -- this reintroduces #2537's stale " +
            "non-key-field defect.");
    }

    [Fact]
    public void NotFoundRestore_RefindsTheOriginalRowByItsOwnPrimaryKey()
    {
        var body = FindRowFromTableFieldValuesBody(MockTestPageSource());

        // The restore must capture the original row's OWN primary-key field numbers/values
        // before scanning moves the cursor away, then re-locate that row by walking forward
        // through MoveFirst/MoveNextDataRow -- the same load path a normal search already
        // uses, which refreshes every field (not just the key).
        Assert.Contains("originalKeyFieldNos", body, StringComparison.Ordinal);
        Assert.Contains("originalKeyValues", body, StringComparison.Ordinal);
        Assert.Contains("MoveNextDataRow()", body, StringComparison.Ordinal);
    }
}
