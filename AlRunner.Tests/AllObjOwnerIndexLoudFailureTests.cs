// AllObjOwnerIndexLoudFailureTests — issue #3117, follow-up to #3107 (which fixed #3049).
//
// WHAT THIS PINS
//   RecordPatches.BuildObjectOwnerIndex answers "which app owns object (kind, id)". Whatever
//   it cannot answer for is left out of the index, and PopulateAllObjVirtualTable then writes
//   Guid.Empty into AllObj's "App Package ID" / "App Runtime Package ID" columns:
//
//       var owningAppId = ownerIndex.TryGetValue((normalized, id), out var owner)
//                       ? owner : Guid.Empty;
//
//   Leaving an object out is the CORRECT, deliberate answer when the runner genuinely does not
//   know who owns it (#3107's comment explains why attributing it to the current bundle would
//   be a permission granted on a guess). It is the WRONG answer when the runner failed to look,
//   because real System Application code — Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable —
//   compares those columns against the caller's Published Application row and simply declines.
//   "This app does not own it" and "we could not find out" become the same observable outcome:
//   no message, no exit-code change, a green run. That is the silent default
//   .claude/rules/loud-failures.md exists to prevent.
//
//   Before #3117 the read failures were swallowed by three bare `catch { continue; }` blocks.
//   Now they raise a RunnerOutOfScopeException naming the assembly or package that could not
//   be read.
//
// WHY THE READER IS INJECTED
//   The condition is "reading this assembly's type metadata throws". A test cannot make a real
//   R2R assembly's TypeDef table unreadable on demand, so AddEmittedAssemblyOwners takes the
//   name-reader as a parameter and the tests hand it a thrower. Same shape, and the same
//   reason, as Win32Stubs.FindCompiler(Func<string, bool>) / Win32StubsLoudFailureTests.
//
// WHY THIS IS NOT AN UPSTREAM CORPUS TEST
//   Object ownership as AL observes it through AllObj IS BC-observable, and that half of the
//   claim is already upstream and adjudicated on eight real BC legs — Codeunit 60405
//   "Test Module Owns Own Table" (BusinessCentral.AL.Language.Tests#181). This file asserts
//   something different and strictly runner-local: how the RUNNER reports its own failure to
//   read its own emitted assemblies. A BC service tier is never in that state and AL cannot
//   observe it, so there is nothing for the corpus to adjudicate.
using System;
using System.Collections.Generic;
using System.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AllObjOwnerIndexLoudFailureTests
{
    private static readonly Guid AppUnderTest = new("6EDA7750-0000-4000-8000-000000000001");
    private static readonly Guid SomeOtherApp = new("5A8EDAC8-0000-4000-8000-000000000002");

    /// <summary>Every prefix the scan asks for, answered with nothing.</summary>
    private static IEnumerable<string> NoTypes(string prefix) => Array.Empty<string>();

    // ----------------------------------------------------------------------------------
    // The failure path — the whole point of #3117.
    // ----------------------------------------------------------------------------------

    [Fact]
    public void ReaderThrowingOnCall_RaisesALoudRefusal_InsteadOfLeavingObjectsUnowned()
    {
        var index = new Dictionary<(string Kind, int Id), Guid>();

        var ex = Assert.Throws<RunnerOutOfScopeException>(() =>
            RecordPatches.AddEmittedAssemblyOwners(
                index, "MyApp.Emitted", AppUnderTest,
                _ => throw new BadImageFormatException("TypeDef table is truncated")));

        // Names WHAT could not be read...
        Assert.Contains("MyApp.Emitted", ex.Message);
        // ...and WHY, carrying the underlying failure rather than flattening it.
        Assert.Contains("BadImageFormatException", ex.Message);
        Assert.Contains("TypeDef table is truncated", ex.Message);
        // ...and what the consequence would have been, so the reader is not left guessing.
        Assert.Contains("no owning app", ex.Message);

        // The bug WAS the default. Assert against it explicitly: a swallow would have returned
        // normally and left this index empty, which is indistinguishable from "owns nothing".
        Assert.Empty(index);
    }

    [Fact]
    public void ReaderThrowingMidEnumeration_RaisesALoudRefusal_NotJustOnTheCall()
    {
        // The regression this guards: both real producers are LAZY — TypeNamesWithPrefix is a
        // `yield return` iterator and EnumerateWithPrefix(...).Select(...) is deferred — so the
        // pre-#3117 `try { names = ...; } catch { continue; }` wrapped only the assignment and
        // caught nothing the enumeration itself raised. A try that does not span the foreach
        // lets this escape unnamed; this test fails if that ever comes back.
        var index = new Dictionary<(string Kind, int Id), Guid>();

        var ex = Assert.Throws<RunnerOutOfScopeException>(() =>
            RecordPatches.AddEmittedAssemblyOwners(
                index, "Lazy.Emitted", AppUnderTest, ThrowsAfterFirst));

        Assert.Contains("Lazy.Emitted", ex.Message);
        Assert.Contains("InvalidOperationException", ex.Message);
        Assert.Contains("metadata-only", ex.Message);

        static IEnumerable<string> ThrowsAfterFirst(string prefix)
        {
            yield return prefix + "1";
            throw new InvalidOperationException("TypeNamesWithPrefix is metadata-only");
        }
    }

    [Fact]
    public void TheRefusal_IsNotPermanentlyOutOfScope_SoATryFunctionCannotTrapItIntoFalse()
    {
        // BcRuntime.NavApplicationObjectBase_TryInvoke traps a PERMANENTLY out-of-scope refusal
        // into `false`, and lets a "not-yet-implemented" one tear through
        // (RecordPatches.VirtualTableShapeGap.cs, TryFunctionOutOfScopeTrapTests). If this
        // refusal ever acquired a scope.md anchor instead, an AL [TryFunction] reading AllObj
        // would silently receive `false` — the exact silent default this issue is about, just
        // relocated. Pinned so that change cannot happen quietly.
        var ex = Assert.Throws<RunnerOutOfScopeException>(() =>
            RecordPatches.AddEmittedAssemblyOwners(
                new Dictionary<(string Kind, int Id), Guid>(), "Anything", AppUnderTest,
                _ => throw new BadImageFormatException("boom")));

        Assert.StartsWith("not-yet-implemented", ex.Reason);
        Assert.Contains("allobj-virtual-table", ex.Reason);
    }

    // ----------------------------------------------------------------------------------
    // The success path — so none of the above can be satisfied by a method that always throws.
    // ----------------------------------------------------------------------------------

    [Fact]
    public void EveryEmittedObjectKind_IsIndexedAgainstTheDeclaringAssemblysApp()
    {
        var index = new Dictionary<(string Kind, int Id), Guid>();

        RecordPatches.AddEmittedAssemblyOwners(index, "MyApp.Emitted", AppUnderTest, prefix => prefix switch
        {
            "Record"   => new[] { "Record60404" },
            "Codeunit" => new[] { "Codeunit60405" },
            "Page"     => new[] { "Page60406" },
            "Report"   => new[] { "Report60407" },
            "Query"    => new[] { "Query60408" },
            "XmlPort"  => new[] { "XmlPort60409" },
            _          => Array.Empty<string>(),
        });

        // Concrete ids and concrete kinds: a no-op implementation returning an empty map fails
        // every one of these, and so does one that indexes the wrong AL kind vocabulary.
        Assert.Equal(AppUnderTest, index[("table", 60404)]);
        Assert.Equal(AppUnderTest, index[("codeunit", 60405)]);
        Assert.Equal(AppUnderTest, index[("page", 60406)]);
        Assert.Equal(AppUnderTest, index[("report", 60407)]);
        Assert.Equal(AppUnderTest, index[("query", 60408)]);
        Assert.Equal(AppUnderTest, index[("xmlport", 60409)]);
        Assert.Equal(6, index.Count);
    }

    [Fact]
    public void ASymbolReferenceAnswer_OutranksTheAssemblyScan()
    {
        // #3049's union rule: the assembly pass only fills ids no symbol reference claimed, so a
        // table an app does not own keeps the owner it already had. Without this, the assembly
        // pass would let a bundle claim ownership of objects another app declares.
        var index = new Dictionary<(string Kind, int Id), Guid>
        {
            [("table", 60404)] = SomeOtherApp,
        };

        RecordPatches.AddEmittedAssemblyOwners(index, "MyApp.Emitted", AppUnderTest, prefix =>
            prefix == "Record" ? new[] { "Record60404", "Record60410" } : Array.Empty<string>());

        Assert.Equal(SomeOtherApp, index[("table", 60404)]);   // symbol answer preserved
        Assert.Equal(AppUnderTest, index[("table", 60410)]);   // unclaimed id filled in
    }

    [Fact]
    public void NamesThatCarryNoPositiveObjectId_AreIgnoredRatherThanIndexedAsZero()
    {
        var index = new Dictionary<(string Kind, int Id), Guid>();

        RecordPatches.AddEmittedAssemblyOwners(index, "MyApp.Emitted", AppUnderTest, prefix =>
            prefix == "Record"
                ? new[] { "Record0", "RecordHelper", "Record-5", "Record", "Record60404" }
                : Array.Empty<string>());

        // Only the one real object id survives; nothing lands under id 0 or a negative id.
        Assert.Equal(new[] { ("table", 60404) }, index.Keys.ToArray());
    }

    [Fact]
    public void AnAssemblyDeclaringNoEmittedObjects_IndexesNothingAndDoesNotThrow()
    {
        var index = new Dictionary<(string Kind, int Id), Guid>();
        RecordPatches.AddEmittedAssemblyOwners(index, "Empty.Emitted", AppUnderTest, NoTypes);
        Assert.Empty(index);
    }
}
