// TestPageGoToRecordPositionCaptionTests — pins the C# CONTRACT issue #2515's fix depends
// on, not "what BC does" (that's the job of the companion corpus PR,
// StefanMaron/BusinessCentral.AL.Language.Tests#122, which proves the AL-observable
// behavior against real BC 27.5/28.3 once merged).
//
// NavRecord.ALGetPosition()'s default overload (useCaptions: true) encodes the cursor
// position string using field CAPTIONS, and ALSetPosition decodes it through the same
// SETVIEW-style filter parser AL filter views use, resolving each token by caption. On a
// table with two fields sharing a caption -- legal AL, common on older tables -- that
// decode raises BC's own NavNCLFieldNotFoundException instead of positioning. NavRecord is
// real, unmodified BC engine code (Ncl.dll) we cannot rewrite the body of (see
// precompiled-dll-respect.md), so the fix is which of its two public overloads
// MockTestPage.cs calls, not a patch to NavRecord itself. There is no reflection surface
// that exercises this without a loaded BC runtime/session, so what's provable here is that
// the source no longer calls the caption-encoding default overload at any of its
// GoToRecord-reachable cursor-position call sites.
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageGoToRecordPositionCaptionTests
{
    private static string MockTestPageSource()
    {
        // AlRunner.Tests/bin/<config>/<tfm>/ -> repo root is four levels up.
        var dir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "AlRunner", "Patches", "MockTestPage.cs");
        Assert.True(File.Exists(path), $"expected to find {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void NoCallSite_UsesTheCaptionEncodingDefaultOverload()
    {
        var source = MockTestPageSource();

        // A bare `.ALGetPosition()` -- no argument -- resolves to NavRecord's
        // useCaptions:true default, which is exactly the overload issue #2515 showed
        // throws on a table with a duplicate field caption. Every call site in this file
        // must pass the overload explicitly.
        // Exclude comment lines -- the fix's own explanatory comment names the default
        // overload in prose, which is not a call site.
        var codeLines = source.Split('\n');
        var bareCallCount = 0;
        foreach (var line in codeLines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//")) continue;
            var codePart = Regex.Replace(line, @"//.*$", "");
            bareCallCount += Regex.Matches(codePart, @"\.ALGetPosition\(\s*\)").Count;
        }
        Assert.True(bareCallCount == 0,
            $"found {bareCallCount} bare ALGetPosition() call(s) in MockTestPage.cs -- " +
            "these resolve to NavRecord's useCaptions:true default and reintroduce #2515's " +
            "ambiguous-caption throw on the GoToRecord not-found path.");
    }

    [Fact]
    public void EveryPositionCaptureCallSite_ExplicitlyDisablesCaptions()
    {
        var source = MockTestPageSource();

        // Every call this file makes to capture a cursor position for later restore
        // (FindRowFromTableFieldValues' not-found restore, EnterNewRowLine's return
        // position, and GetBookmark) must resolve field-by-NUMBER, matching every other
        // cursor move in this class (ALSetPosition/GetFieldValue already take field
        // numbers, never captions).
        var explicitCalls = Regex.Matches(source, @"\.ALGetPosition\(\s*useCaptions:\s*false\s*\)");
        Assert.Equal(3, explicitCalls.Count);
    }
}
