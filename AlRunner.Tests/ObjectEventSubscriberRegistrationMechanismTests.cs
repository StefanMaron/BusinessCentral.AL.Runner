using System;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// EventSubscriberPatches.EnsureRegistryFresh matches candidate subscriber attributes
// PURELY BY DUCK TYPING via reflection: a class whose simple Name starts with "Codeunit",
// hosting a method decorated with an attribute whose simple Type.Name is exactly
// "NavEventSubscriberAttribute" and which exposes a `TargetObjectId` property (itself
// exposing `ObjectType`/`ObjectNumber` int properties) plus a `TargetMethodName` string
// property (see TryReadAttribute). BC's real compiled attribute type
// (Microsoft.Dynamics.Nav.Runtime.NavEventSubscriberAttribute) has exactly this shape.
// A hand-written stand-in with the same shape is therefore a faithful mechanism probe of
// the SCANNING + ROUTING half of the fix, without needing the real BC-compiled Ncl.dll
// type or a full AL compile — the actual end-to-end dispatch-against-real-BC-semantics
// claim is proven separately by the corpus test (StefanMaron/BusinessCentral.AL.Language.Tests,
// Codeunit 60986 "Test Manual ObjectEvent", verified green against real BC 27.5/28.3 in
// PR #45 before this runner fix landed).
internal sealed class NavEventSubscriberAttribute : Attribute
{
    public NavEventSubscriberAttribute(int objectType, int objectId, string methodName)
    {
        TargetObjectId = new FakeTargetObjectId(objectType, objectId);
        TargetMethodName = methodName;
    }

    public object TargetObjectId { get; }
    public string TargetMethodName { get; }
}

internal sealed class FakeTargetObjectId
{
    public FakeTargetObjectId(int objectType, int objectNumber)
    {
        ObjectType = objectType;
        ObjectNumber = objectNumber;
    }

    public int ObjectType { get; }
    public int ObjectNumber { get; }
}

/// <summary>
/// Stand-in for what BC's AL compiler emits for a codeunit hosting [EventSubscriber]
/// methods that target a manually-declared event published from a Page/Report/Query/
/// XmlPort object's own code (issue #1794). ObjectType ordinals below are
/// Microsoft.Dynamics.Nav.Types.ObjectType — Page=8, Report=3, Query=9, XmlPort=6 —
/// confirmed via reflection over Microsoft.Dynamics.Nav.Types.dll (see
/// CodeunitEventDispatcher.cs's ObjectTypeToEventPublisherKind doc comment), not guessed.
/// </summary>
public class Codeunit99989ObjectEventMechanismFixture
{
    [NavEventSubscriberAttribute(8, 60977, "OnAfterManualPageEventPub")]
    public void OnPageEventSub() { }

    [NavEventSubscriberAttribute(3, 60978, "OnAfterManualReportEventPub")]
    public void OnReportEventSub() { }

    [NavEventSubscriberAttribute(9, 60981, "OnAfterManualQueryEventPub")]
    public void OnQueryEventSub() { }

    [NavEventSubscriberAttribute(6, 60982, "OnAfterManualXmlPortEventPub")]
    public void OnXmlPortEventSub() { }

    [NavEventSubscriberAttribute(2, 60979, "OnUnsupportedFormEvent")] // Form=2 — deliberately unmapped
    public void OnUnsupportedKindSub() { }
}

/// <summary>
/// Issue #1794: pins the REGISTRY half of the fix — EnsureRegistryFresh's attribute scan,
/// ObjectTypeToEventPublisherKind's ordinal routing, and _byObjectEventKey population —
/// which is a materially stronger claim than DispatchEventPublisherDeclTypeTests.cs's pure
/// decode-seam pin (that file only proves a declaring-type STRING decodes correctly; it
/// never touches the scanner or the registry at all).
///
/// Before this fix, neither GetObjectEventSubscribers nor ObjectTypeToEventPublisherKind
/// existed: a [NavEventSubscriberAttribute] targeting ObjectType Page/Report/Query/XmlPort
/// was read off the assembly (EnsureRegistryFresh's own `scannedAttrs` counter still counted
/// it) and then matched NONE of the scanner's branches (only Table=1 and Codeunit=5 were
/// handled) — so it was silently discarded. That is exactly the silent-drop shape
/// loud-failures.md forbids, and this test — run against pre-fix code — fails with a null
/// lookup for all four kinds, proving the RED state. Post-fix it is GREEN.
///
/// SHARED-STATE NOTE: ResetForReload() clears EventSubscriberPatches' global static
/// registries process-wide. Safe today only because no other test SOURCE FILE touches
/// EventSubscriberPatches (DispatchEventPublisherDeclTypeTests.cs only exercises the pure,
/// state-free TryDecodeEventPublisherDeclType seam) and because xUnit here runs one test
/// class's methods sequentially by default — this class isn't itself parallel-unsafe
/// against itself. If a SECOND test class is ever added that also calls ResetForReload/
/// EnsureRegistryFresh/GetObjectEventSubscribers (or any other EventSubscriberPatches
/// registry accessor), both classes must join a shared serial xUnit collection (see
/// BcEngineCollection.cs for the established DisableParallelization pattern) — otherwise
/// xUnit's cross-class parallelization will interleave two tests' resets/scans of the
/// same static dictionaries.
/// </summary>
public class ObjectEventSubscriberRegistrationMechanismTests
{
    [Theory]
    [InlineData("Page", 60977, "OnAfterManualPageEventPub", nameof(Codeunit99989ObjectEventMechanismFixture.OnPageEventSub))]
    [InlineData("Report", 60978, "OnAfterManualReportEventPub", nameof(Codeunit99989ObjectEventMechanismFixture.OnReportEventSub))]
    [InlineData("Query", 60981, "OnAfterManualQueryEventPub", nameof(Codeunit99989ObjectEventMechanismFixture.OnQueryEventSub))]
    [InlineData("XmlPort", 60982, "OnAfterManualXmlPortEventPub", nameof(Codeunit99989ObjectEventMechanismFixture.OnXmlPortEventSub))]
    public void ManuallyDeclaredObjectEvent_SubscriberIsDiscoveredAndRegistered(
        string publisherKind, int publisherId, string eventMethodName, string expectedSubscriberMethodName)
    {
        // Force a full re-scan so this file's already-loaded fixture type is picked up
        // regardless of what ran (or didn't) before this test in the same process.
        EventSubscriberPatches.ResetForReload();

        var subs = EventSubscriberPatches.GetObjectEventSubscribers(publisherKind, publisherId, eventMethodName);

        Assert.NotNull(subs);
        Assert.Single(subs!);
        Assert.Equal(expectedSubscriberMethodName, subs![0].Name);
        Assert.Equal(typeof(Codeunit99989ObjectEventMechanismFixture), subs[0].DeclaringType);
    }

    [Fact]
    public void MismatchedKindIdOrEventName_NoSubscriberReturned()
    {
        // Negative direction: proves the lookup is genuinely KEYED by (kind, id, event name)
        // — not a stub that returns "something registered" for any input. Cross-wiring any
        // one of the three must miss.
        EventSubscriberPatches.ResetForReload();
        EventSubscriberPatches.GetObjectEventSubscribers("Page", 60977, "OnAfterManualPageEventPub"); // warm the registry

        Assert.Null(EventSubscriberPatches.GetObjectEventSubscribers("Page", 60978, "OnAfterManualPageEventPub")); // wrong id
        Assert.Null(EventSubscriberPatches.GetObjectEventSubscribers("Report", 60977, "OnAfterManualPageEventPub")); // wrong kind, same id
        Assert.Null(EventSubscriberPatches.GetObjectEventSubscribers("Page", 60977, "SomeUnrelatedEvent")); // wrong event name
    }

    [Fact]
    public void UnmappedObjectTypeOrdinal_SubscriberIsNotSilentlyRegisteredAnywhere()
    {
        // Form (ObjectType ordinal 2) is deliberately NOT mapped by
        // ObjectTypeToEventPublisherKind (issue #1794 fixed Page/Report/Query/XmlPort only;
        // Form/Dataport/MenuSuite/etc. are a separate, not-yet-investigated gap per
        // no-assumption-fixes.md). Proves the fix does not overreach into "register
        // everything" — an attribute the scanner cannot classify must still be discarded,
        // not mis-filed under some other kind.
        EventSubscriberPatches.ResetForReload();

        Assert.Null(EventSubscriberPatches.GetObjectEventSubscribers("Page", 60979, "OnUnsupportedFormEvent"));
        Assert.Null(EventSubscriberPatches.GetObjectEventSubscribers("Report", 60979, "OnUnsupportedFormEvent"));
        Assert.Null(EventSubscriberPatches.GetObjectEventSubscribers("Query", 60979, "OnUnsupportedFormEvent"));
        Assert.Null(EventSubscriberPatches.GetObjectEventSubscribers("XmlPort", 60979, "OnUnsupportedFormEvent"));
    }
}
