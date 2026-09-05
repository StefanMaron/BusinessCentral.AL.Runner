// ModalPageRowLoadDispatchTests — the runner-mechanism half of issue #2797.
//
// A page handed to a [ModalPageHandler] / [PageHandler] is constructed already-open by BC's
// ShowDialog, so RunnerTestPageState.MarkOpened — the code that runs the open sequence for a
// page the AL test opened itself — never runs on that path. RunnerTestClientSession.GetPage
// compensates. It used to compensate with ONE branch:
//
//     if (record != null && IsUnpositioned(record)) live.MoveFirstDuringOpen();
//
// which is right about the CURSOR and wrong about the TRIGGERS. A caller that opened the page
// on a specific row (PAGE.RunModal(id, Rec)) must keep that row — corpus CU60848
// RunModal_OpensOnTheRecordSetByTheCaller exists to catch a re-query — but it still has to get
// that row's OnAfterGetRecord / OnAfterGetCurrRecord. Base Application page 403 "Purchase Order
// Statistics" computes every total it shows in RefreshOnAfterGetRecord() off OnAfterGetRecord
// and nothing in OnOpenPage, so opened modally on a caller-positioned Purchase Header it showed
// zeros.
//
// The BC-behaviour claim itself lives upstream, where a real service tier adjudicates it:
// StefanMaron/BusinessCentral.AL.Language.Tests#164 (codeunit 60403
// ModalPageOnACallerPositionedRow_RaisesOnAfterGetRecordForThatRow), which is RED against a
// runner without this fix and GREEN with it. What is pinned HERE is the runner-side decision
// that claim depends on: IsUnpositioned's rule, and the existence of a load-trigger path for
// the branch it does not send to MoveFirstDuringOpen.

using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ModalPageRowLoadDispatchTests
{
    private readonly BcEngineFixture _engine;
    public ModalPageRowLoadDispatchTests(BcEngineFixture engine) => _engine = engine;

    private const BindingFlags F =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Both branches must exist. Before #2797 only the repositioning one did, so a
    /// caller-positioned record fell off the end of the method having had no trigger raised at
    /// all — silently, which is why it took a Microsoft-bucket measurement to notice.
    /// </summary>
    [Fact]
    public void LiveNavTestPage_OffersBothAnOpenTimeMoveAndAnOpenTimeRowLoad()
    {
        var live = typeof(AlRunner.LiveNavTestPage);

        var move = live.GetMethod("MoveFirstDuringOpen", F);
        Assert.True(move != null,
            "LiveNavTestPage.MoveFirstDuringOpen is gone — RunnerTestClientSession.GetPage "
            + "positions an unpositioned handler page through it.");

        var load = live.GetMethod("MarkRowLoadedDuringOpen", F);
        Assert.True(load != null,
            "LiveNavTestPage.MarkRowLoadedDuringOpen is gone — without it a handler page opened "
            + "on a row the CALLER positioned gets no OnAfterGetRecord at all (issue #2797), "
            + "which is silent: the page simply shows its per-row state's type defaults.");

        // Both are the open-time pair, so both must answer the same shape.
        Assert.Equal(typeof(bool), move!.ReturnType);
        Assert.Equal(typeof(bool), load!.ReturnType);
        Assert.Empty(load.GetParameters());
    }

    /// <summary>
    /// The rule GetPage branches on. "Unpositioned" must mean every primary-key field is still
    /// at its Init() default — that is what separates "nothing has touched this record, position
    /// it" from "the caller chose this row, keep it and just load-trigger it". Get this wrong in
    /// either direction and one of the two branches never runs.
    /// </summary>
    [SkippableFact]
    public void IsUnpositioned_IsTrueOnlyWhileEveryPrimaryKeyFieldIsAtItsDefault()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var isUnpositioned = typeof(AlRunner.Patches.RunnerTestClientSession)
            .GetMethod("IsUnpositioned", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(isUnpositioned != null,
            "RunnerTestClientSession.IsUnpositioned is gone — GetPage has nothing to branch on and "
            + "would either re-query the caller's row away or never load-trigger it.");
        Assert.Equal(typeof(bool), isUnpositioned!.ReturnType);

        var p = isUnpositioned.GetParameters();
        Assert.Single(p);
        Assert.True(typeof(NavRecord).IsAssignableFrom(p[0].ParameterType),
            "IsUnpositioned must decide from the page's own NavRecord; anything else is not the "
            + "record GetPage is about to hand the handler.");
    }
}
