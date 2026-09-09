// LiveNavTestPage: subpage parts — building a part live and compiling its SubPageLink.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;
internal partial class LiveNavTestPage
{
    /// <summary>
    /// The subpage part hosted by <paramref name="controlId"/>, driven live over its own
    /// source table with the SubPageLink applied.
    ///
    /// Previously this handed back a bare MockITestPart whose Creatable is false, so BC's
    /// NavTestPageBase.ALNew() — which consults TestPage.Creatable — refused every insert
    /// made through a part with "New method failed because Insert is not allowed.
    /// Page = , Id = 0". A part that cannot be built now refuses by NAME rather than
    /// answering as an empty page that silently reports no rows and accepts no inserts.
    /// </summary>
    public override ITestPart GetPart(int controlId)
    {
        if (_tornDown) throw MakeTestPageNotOpenException();
        if (_parts.TryGetValue(controlId, out var cached)) return cached;

        // A part whose own Visible — or that of any group enclosing it — is the compile-time
        // LITERAL false is not rendered into the test page's control tree at all, exactly as
        // an eliminated FIELD control is not (see LiveNavTestPage.GetField, which has done
        // this since #1778). Returning null is what makes that faithful: the caller is
        // NavTestPageBase.GetPart(int,bool), a precompiled BC method, and when ITestPage.GetPart
        // answers null it raises BC's own NavTestPartNotFoundException ("The part with ID = ...
        // was not found on the page.") itself — so this part gets the EXACT exception real BC
        // raises, not a runner-invented one, and not a RunnerOutOfScopeException, which would
        // wrongly classify implementable BC behaviour as out of scope.
        //
        // Measured on a real service tier by corpus codeunit 60346
        // (StefanMaron/BusinessCentral.AL.Language.Tests#227, all 8 cloud legs): the suite's
        // first revision asserted the PAIR — a Visible = true part answering true and a
        // Visible = false part answering false on one open host — and every leg falsified it
        // identically with "The part with ID = 1318487454 was not found on the page." The
        // suite's own fixture comment records the conclusion: reaching a part declared
        // Visible = false errors; it does not yield a handle reporting false.
        //
        // NOTE what this does NOT say. NavTestPart genuinely overrides ALVisible/ALEnabled in
        // the IL as `return testPart.Visible` / `return testPart.Enabled`, reading the part
        // control's own metadata — those overrides are real and are not being contradicted.
        // What the tier establishes is that AL cannot REACH a control whose Visible would be
        // false, so those overrides can never be observed returning false. The claim here is
        // about reachability, not about what the accessors return.
        //
        // A Visible bound to a variable or an expression is never eliminated this way, even
        // while it currently evaluates false — see
        // RunnerPageInstance.ControlIsCompileTimeEliminated for the literal-vs-expression
        // distinction and the ancestor walk, which this shares with the field path rather
        // than re-deriving.
        if (_page?.ControlIsCompileTimeEliminated(controlId) == true) return null!;

        if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
            Console.Out.WriteLine($"[MockTestPage.GetPart] controlId={controlId} pageId={_pageId} _page={(_page == null ? "null" : "set")} _page.Form={( _page?.Form == null ? "null" : _page.Form.GetType().FullName)}");

        // BOTH branches are runner gaps, which is why one factory serves them. The second one
        // reads like an AL-authoring error and is not: the AL compiler resolves a part by NAME
        // and emits its control id, so an id that reaches here always named a real part on the
        // real page. Finding no part for it means the runner's page metadata is incomplete.
        var definition = _page?.TryGetPartDefinition(controlId)
            ?? throw TestPageShapeGap.Part(
                $"TestPage part {controlId} (page {_pageId})",
                "the runner could not resolve this control to a subpage part"
                + (_page == null
                    ? "; no AL page object was built for the hosting page, so its part definitions "
                      + "are unavailable — see AlPageMetadataRegistry"
                    : "; the hosting page's metadata declares no part with this control id"));

        var partPageId = definition.PagePartID;
        if (_owner == null)
            throw TestPageShapeGap.Part(
                $"TestPage part {controlId} → page {partPageId}",
                "the hosting page was built without an ITreeObject owner, so the runner has "
                + "nothing to construct the part's own page under");

        var built = TestPageFactory.TryBuild(_owner, partPageId, out var why);

        // A PART is a page, so it gets the same three-way classification the TestPage handle
        // site gives a top-level page — see TestPageClientConstructionRule. This used to
        // collapse the first two answers: "TryBuild produced no record" was read as "this page
        // cannot be driven", and a part page that simply declares no SourceTable (a CardPart
        // whose controls bind to page globals — the info-box shape, ordinary legal AL) was
        // refused out-of-scope the moment a test touched it (issue #2195).
        //
        // THE REASON A RECORD-LESS PART IS SAFE. It is NOT "symmetric with #2090's host fix" —
        // that would be an argument from shape, and the host and the part are different
        // objects with different added behaviour. It is this:
        //
        //   The ONLY behaviour LiveNavTestPart adds over LiveNavTestPage is the SubPageLink.
        //   Every SubPageLink entry — field(), const() and filter() alike — names a field of
        //   the PART's OWN source table (link.FieldID is resolved against it — see SubPageLinks
        //   below). A part page that declares no source table therefore cannot express one,
        //   so `links` is necessarily EMPTY, ApplyLink has
        //   nothing to apply, and the wrapper degenerates to exactly LiveNavTestPage over a
        //   null record — the shape #2007 established, where every Rec-dependent member
        //   refuses BY NAME through RequireRecord instead of answering a default.
        //
        // "Necessarily", not "in the cases we tried": it is a property of what a SubPageLink
        // can refer to, which is why this does not need a per-part audit. Controls bound to
        // page globals resolve through RunnerPageInstance's source-expression table and never
        // reach a record at all, which is the whole point of the shape.
        //
        // Measured on real BC by corpus codeunit 60803 "Test Page NoSrc Part Tests"
        // (StefanMaron/BusinessCentral.AL.Language.Tests commit ef52b7e9, PR #80), all eight
        // arms green on BC 27.5 and BC 28.3.
        //
        // FIXED (issue #2201): the part page object is now, where possible, the SAME
        // RunnerPageInstance the host's own AL reaches through CurrPage.<part> —
        // RunnerPageInstance.AdoptFromHost goes through BC's own NavForm.GetPart(int) on
        // the host, exactly the door the host's compiled AL uses. Only when that cannot
        // produce a live object (the host has no NavForm, the control names no part there,
        // or reifying the adopted object throws) does this fall back to the disconnected
        // instance TryBuild/TryBuildRecordless constructs, which is the ENTIRE previous
        // behaviour and stays exactly as faithful as it always was.
        NavRecord? partRecord;
        RunnerPageInstance? partPage;
        // Whether partPage came from AdoptFromHost — that path already raised the part's
        // OnOpenPage itself (once, at reification — see AdoptFromHost), so the fallback
        // raise below must not run a second time on an adopted instance.
        bool adopted;
        var partKind = TestPageClientConstructionRule.Resolve(
            recordBuilt: built != null,
            pageShapeKnown: RecordPatches.IsPageShapeKnown(partPageId),
            pageDeclaresSourceTable: RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(partPageId));

        if (partKind == TestPageClientKind.LiveOverRecord)
        {
            partRecord = built!.Record;
            var fromHost = RunnerPageInstance.AdoptFromHost(_page?.Form, controlId, partPageId, partRecord, recordless: false);
            adopted = fromHost != null;
            partPage = fromHost ?? built.Page;
            // AdoptFromHost may have reused a record ALREADY bound on the adopted instance
            // (a SourceTableTemporary part the host already populated — see AdoptFromHost's
            // "alreadyLive" branch) instead of the fresh one just built above. This part's
            // OWN record must follow whichever one the live page object actually ended up
            // bound to, or navigation/Insert/Delete would act on an empty record nobody else
            // can see while the control tree reads the real one.
            if (adopted && fromHost!.Record is { } liveRecord) partRecord = liveRecord;
        }
        else if (partKind == TestPageClientKind.LiveRecordless)
        {
            partRecord = null;
            // No record and none needed. Both AdoptFromHost and TryBuildRecordless answering
            // null is a different failure — the runner has no metadata to build the part page
            // object from, so there would be no control tree either — and falls through to
            // the refusal below.
            var fromHost = RunnerPageInstance.AdoptFromHost(_page?.Form, controlId, partPageId, recordToBind: null, recordless: true);
            adopted = fromHost != null;
            partPage = fromHost ?? TestPageFactory.TryBuildRecordless(_owner, partPageId);
            if (partPage == null)
                throw TestPageShapeGap.Part(
                    $"TestPage part {controlId} → page {partPageId}",
                    PartNotLive(why));
        }
        else
        {
            throw TestPageShapeGap.Part(
                $"TestPage part {controlId} → page {partPageId}",
                PartNotLive(why));
        }

        // The parent record is only needed to evaluate FIELD SubPageLink pairs (issue #2053).
        // A part with no FIELD link never reads it — a CONST/FILTER link is evaluated against
        // a literal, and a FIELD link can only be declared against a parent SourceTable field,
        // so a SourceTable-less host (the Worksheet-dialog shape, legal AL) always lands in the
        // parent-less case. Demanding the record up front turned every part access on such a
        // host into a refusal the operation never required.
        var links = SubPageLinks(definition, partPageId);
        var part = new LiveNavTestPart(
            partRecord, RecordPatches.GetPageControlFieldMap(partPageId),
            RecordPatches.GetInsertAllowedForPage(partPageId), partPage, _owner, partPageId,
            parentRecord: LiveNavTestPart.AnyFieldLink(links) ? RequireRecord($"subpage part {controlId}") : null, links: links);
        // A part is never MarkOpened — BC opens the HOST, and the part comes up inside it —
        // so _staticEditable sat at its constructor default of true for every part, whatever
        // the host was opened as. That made a part of a read-only page report itself editable,
        // and (once the new-row line landed) would have offered a blank line on a page opened
        // with OpenView. Apply the same rule MarkOpened applies to a top-level page, with the
        // host's already-resolved editability standing in for the open mode.
        part.MarkPartOf(this);

        // OnOpenPage on the PART, and WHY IT IS RAISED HERE rather than anywhere more obvious.
        //
        // The obvious place is RunnerTestPageState.MarkOpened, which is where a top-level
        // page's OnOpenPage is raised, and where anyone looking for this will look first. It
        // cannot go there: MarkOpened runs when BC opens the HOST, and at that moment no part
        // exists — the runner builds parts LAZILY, on the first AL access, which is this
        // method. So this is the earliest moment a part's trigger CAN run, and since the part
        // is not observable before it, running it here is indistinguishable from BC's
        // "the subpage opens with its host".
        //
        // WHY IT IS PART OF THE #2195 FIX AND NOT A SEPARATE CONCERN. No part has ever had its
        // OnOpenPage raised, and that was invisible while every part had a source table: such
        // a part's observable state lives in the record, and the rowset is there with or
        // without the trigger. A part page with NO source table has no record — every one of
        // its controls is bound to a page global, and the part page's own AL is the ONLY thing
        // that ever puts a value in one. So lifting the out-of-scope refusal WITHOUT this
        // would have replaced a loud failure with a part whose every control reads blank, and
        // blank is indistinguishable from a legitimately empty value: the test goes green, or
        // fails one assertion later against a value it was never told was never computed.
        // That is precisely the silent default `.claude/rules/loud-failures.md` exists to
        // prevent, and it is why removing the throw without this line would have made the
        // runner LESS honest, not more.
        //
        // The corpus arms that read a specific value rather than merely "not refused" are what
        // pin it: codeunit 60803's controls read 'Hello', which only its OnOpenPage can set
        // (StefanMaron/BusinessCentral.AL.Language.Tests commit ef52b7e9, green on BC 27.5 and
        // BC 28.3).
        //
        // Raised BEFORE the part is cached so a re-entrant GetPart during the trigger cannot
        // observe a half-built part; raised after MarkPartOf so the trigger sees the
        // editability the host resolved.
        //
        // NOT raised again when `adopted` is true: AdoptFromHost already raised it, exactly
        // once, at the moment it reified the host's own shared instance (issue #2201) —
        // raising it a second time here would clobber whatever the host's own AL (or an
        // earlier TestPage touch) already wrote through that same instance.
        if (!adopted) part.RaiseOnOpenPage();

        // Position the part on its SubPageLink-matched row and run OnAfterGetRecord/
        // OnAfterGetCurrRecord — issue #2677, measured against real BC (corpus PR
        // StefanMaron/BusinessCentral.AL.Language.Tests#141): a linked part loads on EVERY
        // GetPart touch this method reaches (see ReloadLinkedRow's doc comment for why this
        // is deliberately NOT once-guarded — a GetPart touch normally happens only once per
        // part per TestPage anyway, since the `_parts` cache at the top of this method
        // short-circuits repeats; what actually keeps a linked part in sync across host
        // navigation is <see cref="LiveNavTestPage.Loaded"/> calling this again on every
        // parent row load — see that method).
        //
        // A recordless part (LiveRecordless branch) has no cursor — ReloadLinkedRow no-ops
        // on a null Record — so its OnOpenPage (just raised, or raised inside AdoptFromHost)
        // is the only trigger such a part gets, exactly as before.
        part.ReloadLinkedRow();

        _parts[controlId] = part;
        return part;
    }

    /// <summary>
    /// The SubPageLink as compiled entries. All three kinds AL can declare are applied
    /// (issue #2469): FIELD (<c>ReportId = field(ReportId)</c>) as a SetRange to the parent's
    /// current value, CONST (<c>Kind = const(Attachment)</c>) as a single-value filter on the
    /// literal, FILTER (<c>Status = filter(Open | Released)</c>) as the expression itself.
    /// Before this, CONST and FILTER refused out-of-scope by name — but they are ordinary AL
    /// (10.9% of Base Application 28.1's SubPageLink entries, measured in the issue), not an
    /// unsupported surface, and the refusal cost 19 Tests-ERM tests in one bucket alone.
    ///
    /// What arrives here is the COMPILER's representation, never AL text — measured on BC
    /// 28.1's compiler output for corpus codeunit 60324 "TSPL Tests": an option member is its
    /// ORDINAL (<c>const(Attachment)</c> → <c>1</c>, <c>filter(Open | Released)</c> →
    /// <c>1|2</c>), <c>const(Database::"TSPL Header")</c> is the table id, and
    /// <c>const('SPECIAL')</c> on a Code field is the bare text <c>SPECIAL</c>.
    /// RecordPatches.DependencyPageMetadataXml produces the same shape for a precompiled
    /// dependency's page, so one consumer serves both routes.
    ///
    /// A part field id of 0 (a dependency page whose part field name could not be resolved —
    /// see DependencyPageMetadataXml.EmitSubFormLinkXml) refuses by name for every kind rather
    /// than filtering on no field: an unfiltered part shows other rows' children, which is a
    /// wrong answer, not a missing one.
    /// </summary>
    /// <summary>
    /// The detail both "could not be driven live" refusals report. They are ONE shape reached
    /// down two branches — the recordless path and the fall-through — and they carried
    /// byte-identical reason text written out twice, so the same gap could drift into claiming
    /// two different things depending on which branch found it (#2999).
    /// </summary>
    private static string PartNotLive(string? why)
        => "the part's own page could not be driven live" + (why == null ? string.Empty : $" ({why})");

    private static SubPageLinkEntry[] SubPageLinks(
        Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition definition, int partPageId)
    {
        var links = new List<SubPageLinkEntry>();
        foreach (var link in definition.SubFormLink ?? new List<Microsoft.Dynamics.Nav.Types.Metadata.FilterDefinition>())
        {
            // Both of these are RUNNER gaps, not BC-shape gaps, and the distinction is measured
            // rather than assumed: DependencyPageMetadataXml.EmitSubFormLinkXml DELIBERATELY
            // writes FieldID 0 and a non-numeric FilterValue when it cannot resolve a field
            // NAME to an id, precisely so these two refusals fire. The read succeeded; the
            // answer is about the runner's own metadata reconstruction, which is the line
            // BcShapeGapException draws (#2995).
            if (link.FieldID <= 0)
                throw TestPageShapeGap.PartLink(
                    $"TestPage part → page {partPageId} SubPageLink ({link.FilterType})",
                    $"the part's own field this link constrains could not be resolved "
                    + $"(FieldID {link.FieldID}, {link.FilterType} '{link.FilterValue}')");
            switch (link.FilterType)
            {
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD:
                    if (!int.TryParse(link.FilterValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentFieldNo))
                        throw TestPageShapeGap.PartLink(
                            $"TestPage part → page {partPageId} SubPageLink",
                            $"a FIELD link's value must be the parent's field number, "
                            + $"but this one is '{link.FilterValue}'");
                    links.Add(new SubPageLinkEntry(link.FieldID, link.FilterType, parentFieldNo, string.Empty));
                    break;
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.CONST:
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FILTER:
                    links.Add(new SubPageLinkEntry(link.FieldID, link.FilterType, 0, link.FilterValue ?? string.Empty));
                    break;
                default:
                    // A BC SHAPE GAP, not a scope claim and not a runner gap — the one site in
                    // this file where the runner READ BC's own metadata and could not interpret
                    // what it held (#2946/#2995). FilterType has exactly FIELD/CONST/FILTER:
                    // measured on BC 28.1's Microsoft.Dynamics.Nav.Types.dll, and the runner's
                    // own EmitSubFormLinkXml writes only those three spellings, so a fourth
                    // value can ONLY have come from BC's compiled page metadata. That makes it a
                    // property of which BC build is on disk — it could be true on one matrix leg
                    // and false on another in the same run — which is exactly what may not be
                    // declarable as an expected out-of-scope surface. Refuse rather than treat
                    // it as one of the three and filter wrongly.
                    throw new AlRunner.Infrastructure.BcShapeGapException(
                        $"TestPage part → page {partPageId} SubPageLink",
                        "Microsoft.Dynamics.Nav.Types.Metadata.FilterType",
                        $"holds {link.FilterType}, which is not one of FIELD/CONST/FILTER; this part "
                        + $"links field {link.FieldID} by {link.FilterType} '{link.FilterValue}', and "
                        + "filtering it as any of the three would show the wrong rows rather than none");
            }
        }
        return links.ToArray();
    }
}
