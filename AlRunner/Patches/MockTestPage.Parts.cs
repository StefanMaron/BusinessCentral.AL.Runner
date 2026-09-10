// MockTestPage.Parts.cs — subpage parts: the SubPageLink entry shape and the live part page.
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
/// <summary>
/// One entry of a part's SubPageLink, in the shape BC's compiler writes into
/// <c>InfopartPageDefinition.SubFormLink</c>: the PART's field it constrains, the kind, and
/// either the PARENT's field number (FIELD) or the compiled literal / filter expression
/// (CONST / FILTER) — see <c>MockTestPage.SubPageLinks</c> for the representation.
/// </summary>
internal readonly record struct SubPageLinkEntry(
    int PartFieldNo, Microsoft.Dynamics.Nav.Types.Metadata.FilterType Kind, int ParentFieldNo, string Value);

/// <summary>
/// A subpage part driven live: its own page over its own source table, showing only the
/// rows the SubPageLink selects for the parent's CURRENT row.
///
/// The link is re-applied before every operation rather than once at construction, because
/// NavTestPageBase caches parts for the life of the page: a filter applied once would go
/// stale the moment the AL test moved the parent to another row, and the part would then
/// show the previous row's children — a wrong answer that no assertion in the part itself
/// could distinguish from a right one.
/// </summary>
internal sealed class LiveNavTestPart : LiveNavTestPage, ITestPart
{
    // Null only when _links has no FIELD entry (issue #2053: a part on a SourceTable-less host
    // has no parent record and needs none; a CONST/FILTER-only part never reads one either) —
    // every read below sits inside a FIELD case, so a null parent is never dereferenced.
    private readonly NavRecord? _parentRecord;
    private readonly SubPageLinkEntry[] _links;

    /// <param name="record">The part page's own source-table cursor, or null when the part
    /// page declares NO SourceTable (issue #2195) — a CardPart bound to page globals, the
    /// info-box shape. Nothing in THIS class needs it in that case, and the reason is a
    /// property of SubPageLink rather than an observation about the parts seen so far: the
    /// only behaviour this class adds over LiveNavTestPage is the link, every SubPageLink
    /// entry names a field of the part's OWN source table, so a part with no source table
    /// cannot express one and <see cref="_links"/> is necessarily empty. Everything else is
    /// the base class's null-record path, where each Rec-dependent member refuses by name.</param>
    /// <param name="parentRecord">The host's current-row cursor; required only when
    /// <paramref name="links"/> carries a FIELD entry (<see cref="AnyFieldLink"/>).</param>
    public LiveNavTestPart(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page, object owner, int pageId,
        NavRecord? parentRecord, SubPageLinkEntry[] links)
        : base(record, controlIdToFieldNo, creatable, page, owner, pageId)
    {
        _parentRecord = parentRecord;
        _links = links;
    }

    // Constant true, and -- since #3313 -- for a stated reason rather than as an unexplained
    // hardcode. NavTestPart.ALVisible/ALEnabled read exactly these two properties
    // (`return testPart.Visible` / `return testPart.Enabled` in Ncl.dll), so hardcoding them
    // would normally be exactly the silent answer .claude/rules/loud-failures.md refuses.
    // What makes it faithful is the reachability rule LiveNavTestPage.GetPart now enforces: a
    // part control whose Visible is the compile-time literal false is not in the test page's
    // control tree at all, so no LiveNavTestPart is ever constructed for one and AL has no
    // handle on which to call either accessor. Every part that reaches this class is therefore
    // one BC would render, for which both answers are true.
    //
    // Measured on a real service tier by corpus codeunit 60346
    // (StefanMaron/BusinessCentral.AL.Language.Tests#227): its first revision tried to assert
    // the false arm of each pair, and all 8 cloud legs answered "The part with ID = ... was
    // not found on the page." instead -- Visible() and Enabled() can never be observed
    // returning false from AL.
    //
    // The limit of that argument, stated so a later reader need not re-derive it: a part whose
    // Visible is an EXPRESSION currently evaluating false IS reachable (expressions are never
    // compile-time eliminated -- see ControlIsCompileTimeEliminated), and this would answer
    // true for it. No service tier has measured that shape, because the corpus fixture
    // declares the literal, so it is not guessed at here.
    public bool Enabled => true;
    public bool Visible => true;

    /// <summary>Whether any entry is a FIELD link — the only kind that reads the parent's row.</summary>
    internal static bool AnyFieldLink(SubPageLinkEntry[] links)
    {
        foreach (var link in links)
            if (link.Kind == Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD) return true;
        return false;
    }

    /// <summary>Filter the part's rowset to what its SubPageLink selects for the parent's
    /// current row: a FIELD entry to the parent's current value, a CONST entry to its literal,
    /// a FILTER entry to its expression (issue #2469).</summary>
    private void ApplyLink()
    {
        // Nothing to apply, and nothing to demand: an unlinked part shows its own table's
        // full rowset. The early return is what lets a part page with NO SourceTable exist
        // at all (issue #2195) — such a part cannot carry any SubPageLink, so it always
        // lands here, and without the return the RequireRecord below would refuse EVERY
        // cursor move on it naming "subpage link", a link the part does not have. The move
        // itself still refuses by its own name through the base class when the AL genuinely
        // asks a record-less part to navigate.
        if (_links.Length == 0) return;

        // Past this point the part is linked, which is only expressible against the part's
        // own source table — so it has one, and this is a guaranteed hit used for its record
        // rather than for its refusal.
        var record = RequireRecord("subpage link");
        foreach (var link in _links)
        {
            switch (link.Kind)
            {
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD:
                    record.ALSetRange(link.PartFieldNo, _parentRecord!.GetFieldValue(link.ParentFieldNo));
                    break;
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.CONST:
                    record.ALSetFilter(link.PartFieldNo, ConstFilterExpression(record, link.PartFieldNo, link.Value));
                    break;
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FILTER:
                    // Already in BC's filter grammar (the compiler wrote option members as
                    // ordinals; DependencyPageMetadataXml re-quoted AL identifiers) — BC's own
                    // filter parser, the one SetFilter uses, reads it. A malformed expression
                    // raises BC's own NavInvalidFilterExpressionException naming the text.
                    record.ALSetFilter(link.PartFieldNo, link.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// The compiled CONST literal as a filter expression BC's own filter parser reads as that
    /// ONE value. A Text/Code field gets the literal quoted: the compiler writes
    /// <c>const('SPECIAL')</c> as the bare text <c>SPECIAL</c>, and a bare literal is parsed as
    /// an EXPRESSION — a value containing <c>|</c>, <c>..</c>, <c>(</c> or <c>&amp;</c> would be
    /// read as operators, and an EMPTY literal would clear the filter (SetFilter's own rule for
    /// <c>''</c>) instead of selecting the blank value. Every other type — an option ordinal,
    /// a number, a boolean, a date — is handed over as written, exactly the text an AL
    /// SetFilter call would pass. A field the part's table does not declare is left to
    /// SetFilter, which refuses it with BC's own error naming the field number.
    /// </summary>
    internal static string ConstFilterExpression(NavRecord record, int fieldNo, string value)
    {
        var navType = record.MetaTable.TryGetFieldByNo(fieldNo, out var field) ? field.FieldNavType : (NavType?)null;
        var quote = value.Length == 0 || navType is NavType.Text or NavType.Code;
        return quote ? "'" + value.Replace("'", "''") + "'" : value;
    }

    public override bool MoveFirst() { ApplyLink(); return base.MoveFirst(); }
    public override bool MoveLast() { ApplyLink(); return base.MoveLast(); }
    public override bool MoveNext() { ApplyLink(); return base.MoveNext(); }
    public override bool MovePrevious() { ApplyLink(); return base.MovePrevious(); }

    /// <summary>True when this part carries a FIELD SubPageLink — i.e. its rowset depends on
    /// the PARENT's current row. This is the signal <see cref="LiveNavTestPage.Loaded"/> uses
    /// to decide whether a parent row-load should refresh this part too (issue #2677). A part
    /// with no link, or with only CONST/FILTER links, shows a rowset independent of the
    /// parent's cursor and is never re-positioned by a parent's cursor move; its own initial
    /// row-load still happens once, from GetPart.</summary>
    internal bool HasLinks => AnyFieldLink(_links);

    /// <summary>
    /// Whether a record's buffer holds an actual row rather than the blank one a page has
    /// before its cursor lands anywhere (#3029).
    ///
    /// <para>Read off the PRIMARY KEY, because that is what a position is made of and what
    /// distinguishes the two states here: measured while opening one card over an empty part,
    /// the host's position reads <c>Field1=0()</c> during EagerlyBuildParts and
    /// <c>Field1=0(H1)</c> on every call after its cursor lands. Comparing the position STRING
    /// against a literal would be reading a display format; comparing the key VALUES against
    /// their initialised state asks the same question of the data.</para>
    ///
    /// <para>A table whose whole primary key legitimately holds init values — an integer key at
    /// 0, a singleton — answers false here and so keeps the pre-#3029 behaviour on this path,
    /// which is the safe direction: the guard only ever SUPPRESSES a draft-line entry, so a
    /// false negative costs nothing that was not already happening.</para>
    /// </summary>
    private static bool HasCurrentRow(NavRecord record)
    {
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null || primaryKey.KeyFieldCount == 0) return true;
        for (var i = 0; i < primaryKey.KeyFieldCount; i++)
        {
            var fieldNo = primaryKey.KeyFieldsList[i].FieldNo;
            // NavValue's own "is this the type's zero" answer, so Code/Text compare against ''
            // and Integer/Decimal against 0 without this method knowing which it has.
            var value = record.GetFieldValue(fieldNo);
            if (value != null && !value.IsZeroOrEmpty) return true;
        }
        return false;
    }

    // The parent row this part was last positioned for, as a position string, or null when it
    // has never been positioned. Read at the top of ReloadLinkedRow to tell a re-entry for the
    // SAME parent row from a genuine parent move — see the comment there (#3029).
    private string? _lastReloadedForParentPosition;

    /// <summary>
    /// Position this part on the row matching its SubPageLink and, if one exists, run its
    /// OnAfterGetRecord/OnAfterGetCurrRecord — the row-load a real BC FactBox/subpage part
    /// gets automatically, both when its host opens AND every time the host's own cursor
    /// moves to a different row. Issue #2677, corpus PR
    /// StefanMaron/BusinessCentral.AL.Language.Tests#141 (8 BC legs, OBS probes measuring a
    /// SubPageLink-bound CardPart): with NOTHING ever touching <c>CurrPage.&lt;part&gt;</c> or
    /// <c>TestPage.&lt;part&gt;</c>, opening the host alone produces
    /// <c>HostOpen;PartOpen;HostAGCR;PartAGCR</c> — the part's OnOpenPage runs right after the
    /// host's, and its OnAfterGetRecord/OnAfterGetCurrRecord runs right after the host's own,
    /// entirely unprompted. A later touch adds nothing (already loaded). Navigating the HOST
    /// to a different row (GoToRecord) re-fires the part's trigger for the NEW row and does
    /// NOT re-fire it for the row just left. Before this fix nothing EVER positioned a part's
    /// own cursor at all — <c>TestPageFactory.TryBuild</c> hands back a BLANK, unfetched
    /// record, and only an explicit MoveXxx/GoToBookmark call on the PART ITSELF (which
    /// nothing makes on its behalf) ever reached <see cref="LiveNavTestPage.Loaded"/> — so a
    /// part whose entire per-row state comes from that trigger (the common FactBox-summary
    /// shape) stayed at its field defaults for the page's whole life.
    ///
    /// Deliberately reuses <c>Loaded(bool)</c> rather than <c>MoveFirst()</c>: MoveFirst()
    /// also flushes pending parts/rows — a state change appropriate to an AL-driven cursor
    /// move, not to a parent row simply becoming current. Only the two steps a parent move
    /// really does are taken: run the FOUND case's trigger, or park on the draft line.
    ///
    /// THE NOT-FOUND CASE IS NOT "SHOW NOTHING" (#2923). It used to be, and that was the
    /// remaining half of #2392 applied to parts: a client renders an editable, insert-allowed
    /// repeater with no matching rows as exactly one row — its blank new-row line — and a
    /// write with no <c>New()</c> and no <c>First()</c> of its own lands there. Corpus
    /// codeunit 60743 <c>EmptyEditableList_SetValueWithoutNewOrFirst_InsertsARow</c> measured
    /// that on a real service tier for a standalone page; codeunit 60996
    /// <c>EmptyLinkedPart_WriteWithoutFirst_ValidateSeesTheLinkedKey</c> measures it for a
    /// LINKED part, which is the shape Microsoft's own document tests use. Leaving the part
    /// unpositioned sent that write into a record nothing had ever positioned.
    ///
    /// <c>AbandonNewRowLine</c> first, because this method is re-entered on every parent move:
    /// a draft line the part was parked on belongs to the parent being left, and
    /// <c>Loaded()</c> would not have cleared it.
    ///
    /// NOT once-guarded: every call re-applies the link filter and re-finds, which is exactly
    /// what makes a GoToRecord-driven refresh work. A repeat call for the SAME still-current
    /// row (a second control read with no intervening parent move) re-runs
    /// OnAfterGetRecord/OnAfterGetCurrRecord too — unmeasured against BC for that specific
    /// case (probe 2 only read a control, which triggers a fresh <c>GetPart</c> lookup that
    /// the `_parts` cache already short-circuits before reaching here at all, so it never
    /// re-entered this method a second time for the SAME touch).
    ///
    /// A record-less part (<see cref="LiveNavTestPage.Record"/> null, the page-globals-only
    /// CardPart shape from #2195) has no cursor to position and nothing here to do — its
    /// OnOpenPage is the only trigger such a part gets.
    /// </summary>
    internal void ReloadLinkedRow()
    {
        if (Record is not { } record) return;
        ApplyLink();

        // IS THIS A PARENT MOVE, OR THE SAME ROW ARRIVING AGAIN? (#3029)
        //
        // This method is deliberately not once-guarded — re-applying the link and re-finding is
        // what makes a GoToRecord-driven refresh work, and that stays. But "the parent row
        // changed" and "the host called me again about the row I am already on" are different
        // events, and only the first ends the draft line's row.
        //
        // Opening one card over an empty part reaches here THREE times with the parent never
        // moving: EagerlyBuildParts -> GetPart, MoveFirstDuringOpen -> Loaded ->
        // RefreshLinkedParts, and ALGoToRecord -> FindRowFromFieldValues -> RefreshLinkedParts.
        // Each one used to abandon the draft line and enter it again, and entering it raises
        // the page's OnNewRecord — so merely opening a card cost three firings for a row the
        // test had not asked for yet, plus a fourth for its own First(). Measured; see the PR
        // body for the four stacks.
        var parentPosition = _parentRecord?.ALGetPosition(useCaptions: false);
        var sameParentRow = _lastReloadedForParentPosition != null
                            && _lastReloadedForParentPosition == parentPosition;
        _lastReloadedForParentPosition = parentPosition;

        // NO PARENT ROW YET, SO NO DRAFT LINE YET (#3029). EagerlyBuildParts runs while the
        // host is still opening and its own cursor has not landed anywhere — measured, the
        // parent's position reads `Field1=0()` there, against `Field1=0(H1)` on every later
        // call. A part linked to a parent row that does not exist is showing nothing, and
        // parking it on a draft line at that moment raised the page's OnNewRecord for a row no
        // parent owns.
        //
        // Only the ENTER is skipped, not the find or the Loaded() below it: the part's cursor
        // still has to be positioned, and MoveFirstDuringOpen's own reload — which happens once
        // the host HAS a row — enters the draft line properly. Suppressing the whole method here
        // would lose the part's OnAfterGetRecord for a part that does have rows.
        var parentHasNoRow = _parentRecord != null && !HasCurrentRow(_parentRecord);

        // A genuine parent move ends whatever the part was showing, draft line included. A
        // re-entry for the same row must NOT, or the draft line's started record is discarded
        // and started again.
        if (!sameParentRow) AbandonNewRowLine();

        var found = PageInstance?.RaiseOnFindRecord("-")
                    ?? record.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult();
        Loaded(found);
        if (!found && !parentHasNoRow) EnterNewRowLine(record);
    }

    public override bool FindRowFromTableFieldValues(int[] fieldNos, object[] values, bool forward)
    {
        ApplyLink();
        return base.FindRowFromTableFieldValues(fieldNos, values, forward);
    }

    /// <summary>
    /// Start a new row already carrying the link's values — for the fields BC actually
    /// carries them onto, which is NOT every linked field.
    ///
    /// <c>ApplyLink</c> above has just put every entry on the record as a filter, and
    /// <c>base.InsertEmptyRow</c> then asks BC's own <c>NavForm.NewRecord</c> to start the row
    /// (<c>RunnerPageInstance.TryNewRecord</c>), which runs
    /// <c>RecordImplementation.InitRecordFromFilters</c>. That method — Ncl 28.1,
    /// <c>InitRecordFromFilters(includeNonPrimaryKeyFields, includeIdenticalFilters,
    /// includeNonPrimaryKeyFieldsForFilterGroups)</c> — copies a field's filter onto the new
    /// record only when the filter is <c>FilterExpressionType.Equal</c> (exactly one value)
    /// AND one of: the field is part of the PRIMARY KEY, the page sets
    /// <c>PopulateAllFields</c>, or the caller names the filter's group.
    /// <c>NavForm.NewRecordAsync(bool)</c> passes <c>Array.Empty&lt;int&gt;()</c> for the
    /// groups, so on a TestPage it comes down to key membership.
    ///
    /// This loop therefore applies the same key-membership gate. Without it the runner
    /// stamped every single-valued link onto the new row regardless of the key, which real BC
    /// does not do: measured on all 8 BC legs of corpus codeunit 60324 "TSPL Tests", a
    /// <c>New()</c> through a part linked <c>Kind = const(Attachment)</c>, where Kind is not
    /// part of the line table's key, produced a row with Kind still at Comment — outside the
    /// part's own filter, which BC then reported as "The view is filtered, and the entry is
    /// outside the filter". The corpus pins both directions: the same const on a table whose
    /// key CONTAINS the field IS stamped.
    ///
    /// The loop is not redundant with BC's own step even for the fields it does write. It
    /// covers the record-only fallback in <c>LiveNavTestPage.InsertEmptyRow</c>, where there
    /// is no page to ask and BC's filter step never runs; where BC's step did run it writes
    /// the same values again, which is a no-op. A FIELD entry is read from the parent's
    /// current row; a CONST/FILTER entry is read back through BC's own range accessors on the
    /// filter <c>ApplyLink</c> just set, so a single-value filter answers the same typed value
    /// for min and max (an option ORDINAL arrives as a NavOption, not as the text "1") and a
    /// multi-value or open-ended one raises BC's own error and stamps nothing — the
    /// <c>Equal</c> half of the same rule, decided by BC rather than re-derived from text.
    /// </summary>
    public override void InsertEmptyRow(bool beforeCurrent)
    {
        ApplyLink();
        base.InsertEmptyRow(beforeCurrent);
        if (_links.Length == 0) return;
        var record = RequireRecord("subpage link");
        var primaryKeyFieldNos = PrimaryKeyFieldNos(record);
        // What was actually stamped, in stamping order — BC's own
        // `fieldsInitializedFromFilters`, which is the exact set its validate step runs over.
        var stamped = new List<(int FieldNo, NavValue Value)>();
        foreach (var link in _links)
        {
            // Not part of the primary key: BC leaves it at its Init() value, so the runner
            // must too — a stamped value here would put a row inside a filter BC would have
            // reported as outside it.
            if (!primaryKeyFieldNos.Contains(link.PartFieldNo)) continue;
            switch (link.Kind)
            {
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD:
                    var linked = _parentRecord!.GetFieldValue(link.ParentFieldNo);
                    record.SetFieldValue(link.PartFieldNo, linked);
                    stamped.Add((link.PartFieldNo, linked));
                    break;
                default:
                    if (TryGetSingleFilterValue(record, link.PartFieldNo, out var single))
                    {
                        record.SetFieldValue(link.PartFieldNo, single);
                        stamped.Add((link.PartFieldNo, single));
                    }
                    break;
            }
        }
        ValidateStampedFields(record, stamped);
    }

    /// <summary>
    /// Run OnValidate on the fields the link just stamped — <c>NavForm.NewRecordAsync</c>'s
    /// second step, which the runner did not perform at all (issue #2551, gap 2).
    ///
    /// <para>BC's body is two steps in order: copy the link's values onto a freshly reset
    /// buffer, then
    /// <c>if (ValidateFieldsInOnNewRecord) SourceTable.ValidateFieldsAsync(fieldsInitializedFromFilters, ...)</c>
    /// — OnValidate on exactly the fields step 1 copied, and nothing else. The runner performed
    /// step 1 and stopped, so a field carrying a value from the link arrived RAW: its own
    /// OnValidate never ran, and anything that trigger derives stayed at its Init() default
    /// while the field itself already held the linked value. That is a wrong answer rather than
    /// a missing feature, which is why it is fixed rather than declared out of scope.</para>
    ///
    /// <para><c>ValidateFieldsInOnNewRecord</c> is a plain auto-property with no setter anywhere
    /// in Ncl, so nothing in the decompiled runtime says which way it is set — only a service
    /// tier can answer it. It is answered: corpus codeunit 60653 "NRB Tests"
    /// (StefanMaron/BusinessCentral.AL.Language.Tests#150) measured on all eight BC legs that a
    /// New() through a field(...) link DOES run the stamped field's OnValidate. So the flag is
    /// set by whatever drives a TestPage's New(), and the runner validates unconditionally here
    /// rather than modelling a flag nothing it can see ever writes.</para>
    ///
    /// <para>Copy-then-validate, not validate-during-copy: BC hands its validate step the whole
    /// set after the copy loop finishes, so an OnValidate on the first stamped field already
    /// sees the others in place. Validating inside the loop would show it a half-stamped row.</para>
    ///
    /// <para>Deliberately NOT wrapped in the <c>CurrFieldNo</c> assignment that
    /// <c>ValueControl.SetValue</c> uses (#2705). That one models a PAGE-ORIGINATED write, and
    /// BC's step here is <c>SourceTable.ValidateFieldsAsync</c> — a record-level call, the same
    /// shape as <c>Rec.Validate</c>, which real BC leaves CurrFieldNo at 0 for. No corpus test
    /// pins CurrFieldNo during New(), so this follows the mechanism rather than guessing.</para>
    ///
    /// <para>Errors propagate. An OnValidate that refuses the linked value is BC refusing to
    /// start the row, and swallowing it here would hand the test a row real BC never creates.</para>
    /// </summary>
    private static void ValidateStampedFields(NavRecord record, List<(int FieldNo, NavValue Value)> stamped)
    {
        foreach (var (fieldNo, value) in stamped)
            record.ALValidateAsync(fieldNo, value, null).GetAwaiter().GetResult();
    }

    /// <summary>The field numbers making up the record's primary key — the membership test
    /// <c>NCLMetaField.FieldIsPartOfPrimaryKey</c> answers inside BC, read off the same
    /// <c>MetaTable.PrimaryKey</c> the AutoSplitKey path already uses so both agree on the key
    /// shape. Empty for a table whose key metadata is unavailable, which makes the caller stamp
    /// nothing rather than stamp on a guess.</summary>
    private static HashSet<int> PrimaryKeyFieldNos(NavRecord record)
    {
        var fieldNos = new HashSet<int>();
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null) return fieldNos;
        for (var i = 0; i < primaryKey.KeyFieldCount; i++)
            fieldNos.Add(primaryKey.KeyFieldsList[i].FieldNo);
        return fieldNos;
    }

}
