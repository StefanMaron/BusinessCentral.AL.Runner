// LiveNavTestPage: control-to-field resolution for ITestPage.GetField.
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
    public override ITestField GetField(int id)
    {
        if (_tornDown) throw MakeTestPageNotOpenException();

        // A control whose OWN Visible, or that of any group enclosing it, is the compile-time
        // LITERAL false is dead-code-eliminated on real BC — it never exists on the runtime
        // page at all. Returning null here is what makes that faithful: the caller is
        // NavTestPageBase.GetField(int,bool) (a precompiled BC method, not ours), and when
        // ITestPage.GetField answers null it raises BC's own NavTestFieldNotFoundException
        // ("The field with ID = ... is not found on the page.") itself — so this control gets
        // the EXACT exception real BC raises, not a runner-invented one. A Visible bound to a
        // variable/expression is never eliminated this way, even while it is currently false;
        // see RunnerPageInstance.ControlIsCompileTimeEliminated for the literal-vs-expression
        // distinction and the ancestor walk.
        if (_page?.ControlIsCompileTimeEliminated(id) == true) return null!;

        // A control bound to a Rec field resolves against the record, as before. Non-null:
        // _controlIdToFieldNo is only ever populated (RecordPatches.GetPageControlFieldMap)
        // for a page that declares a SourceTable, so a hit here implies _record is set.
        if (_controlIdToFieldNo.TryGetValue(id, out var tableFieldNo))
        {
            // Keyed by CONTROL id, not by table field number. A page may show one field
            // through more than one control -- twice under different conditions, or once in
            // each of two groups with different visibility -- and each of those controls
            // carries its own Visible / Editable / Enabled. Keying by field number handed the
            // second control the instance built for the first, which holds the FIRST
            // control's id, so every property read answered for the wrong control.
            //
            // Real BC keeps them apart: corpus test "TPSF Tests" (codeunit 60263) opens a
            // card with two controls over one Text field, the second declaring
            // Editable = false, and reads them independently on all 8 BC versions.
            //
            // Sharing the instance bought nothing. LiveNavTestField holds only readonly
            // state -- the record, the field number, the page, the control id and the
            // edited callback -- and every value it reads or writes goes to the record, so
            // two instances over one field see each other's writes exactly as one did.
            // _pageVariableFields beside it is already keyed this way.
            if (!_fields.TryGetValue(id, out var field))
                _fields[id] = field =
                    new LiveNavTestField(_record!, tableFieldNo, _page, id,
                        MarkEdited, PromoteNewRowLineForWrite, ActivateControl, _validationErrors);
            return field;
        }

        // Otherwise it may be bound to a page VARIABLE — resolvable only through the page's
        // own binding table (NavForm.SourceExpressions).
        var expression = _page?.TryGetSourceExpression(id);
        if (expression != null)
        {
            if (!_pageVariableFields.TryGetValue(id, out var pageField))
                _pageVariableFields[id] = pageField =
                    new PageVariableTestField(_page!, expression, id, _validationErrors);
            return pageField;
        }

        // Neither — and from here there are TWO different answers, which this site used to
        // collapse into one runner-gap refusal (issue #3313).
        //
        // If the page declares no control with this id AT ALL, the id is not in the page's
        // control-id space and BC refuses it BY DESIGN. NavTestPageBase.GetField(int,bool) —
        // the precompiled BC method the AL compiler emits for TestPage.GetField(Id) — is
        // `fields.TryGetValue(id, ...)` over the page's own control dictionary, falling back
        // to ITestPage.GetField(id), and raises its own NavTestFieldNotFoundException ("The
        // field with ID = N is not found on the page.") when that answers null. Returning
        // null hands BC's method exactly the input it is written to refuse, so AL sees BC's
        // own exception rather than one the runner invented.
        //
        // The reachable case in ordinary AL is confusing GetField's id space with the source
        // table's: `Host.Lines.GetField(Rec.FieldNo(Descr))`. Table field numbers are keys in
        // neither dictionary. Answering a field for one was a SILENT WRONG ANSWER in the sense
        // .claude/rules/loud-failures.md names — AL got a handle it could never have obtained
        // on a real tier, and nothing looked broken until the same test ran against one.
        //
        // Measured on a real service tier by corpus codeunit 60346
        // (StefanMaron/BusinessCentral.AL.Language.Tests#227, all 8 cloud legs): the suite's
        // first revision passed table field 3 and every leg answered "The field with ID = 3
        // is not found on the page." The corpus test asserts alongside it that 3 really IS a
        // valid table field number on that part's source table, so the refusal it pins is
        // about the ID SPACE and not about a meaningless argument.
        //
        // If the page DOES declare the control and the runner merely could not resolve its
        // binding, that is a genuine runner gap and keeps the named refusal below — this
        // narrows what gets reported as unimplemented, it does not widen it. Only a live page
        // object can answer the declaration question, so a page the runner built no metadata
        // for keeps the gap refusal too, which is the honest answer there: the runner does not
        // know whether BC would have found the control.
        if (_page?.DeclaresControl(id) == false) return null!;

        // Historically `id` was handed to the record as a FIELD NUMBER, which produced "The
        // supplied field number '<hash>' cannot be found in the '<table>' table" — a
        // control-name hash reported as a missing field, blaming the table for the runner's
        // own inability to resolve the control. Say what actually happened.
        throw TestPageShapeGap.ControlBinding(
            $"TestPage control {id}",
            "this control is bound neither to a field of the page's "
            + $"source table nor to a page variable the runner could resolve (table "
            + $"{_record?.MetaTable?.TableName ?? "?"}"
            + (_page == null
                ? "; no AL page object was built for this page, so page-variable-bound controls "
                  + "cannot be resolved — see AlPageMetadataRegistry"
                : "; the page object has no source expression for this control id")
            + ")");
    }
}
