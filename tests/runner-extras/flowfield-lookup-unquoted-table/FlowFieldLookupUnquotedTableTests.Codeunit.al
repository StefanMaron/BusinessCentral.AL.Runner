// Regression test — RecordPatches.AlSourceParser.RxCalcFormulaParts silently failing to
// parse a lookup() FlowField CalcFormula whose SOURCE TABLE NAME is an unquoted AL
// identifier (legal AL: quotes are only required when a name contains spaces).
//
// RED (before the fix): RxCalcFormulaParts required the CalcFormula's table-name group
// to be double-quoted unconditionally — `lookup("Some Table".Field where(...))` parsed
// fine, but `lookup(SomeTable.Field where(...))` (no spaces, so AL allows/prefers it
// unquoted) did not match the regex at all. TryParseCalcFormula returned null, so the
// field's NCLMetaField.CalculationFormula was left at NCLMetaCalculationFormula.
// EmptyFormula. FlowFieldPatches.RecordImpl_CalcFieldsAsync_3 treats EmptyFormula as a
// silent no-op (matches its Filters==null / self-referencing guard), so CalcFields()
// left the target field at its type default (0) instead of the real looked-up value —
// with no error at all, just a silently wrong 0. Verified live against the real bug
// this fixes: Pageworks' PageworksDSFieldMapLine.TargetTableNo (`lookup(
// PageworksDSFieldConfigLine.TargetTableNo where(...))`, unquoted source table) always
// calculated to 0, and downstream `RecordRef.Open(FieldLine.TargetTableNo)` then threw
// "no NCLMetaTable for table 0" (Codeunit50287 in the Pageworks suite).
//
// GREEN (after the fix): RxCalcFormulaParts accepts an unquoted single-word table name
// via the same quoted-or-bare alternation the field-name group already had, so the
// formula parses, NCLMetaField.CalculationFormula carries the real Lookup formula, and
// CalcFields() resolves the real value.
codeunit 61701 "FFL Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "FFL Assert";

    // Positive: the FlowField must resolve to the REAL looked-up value, not just "did not
    // throw" — a stub/broken parse that always yields 0 would still let CalcFields()
    // "succeed" (no exception), so the assertion specifically checks the non-default value.
    [Test]
    procedure LookupFlowFieldWithUnquotedSourceTableResolvesRealValue()
    var
        ConfigLine: Record FFLConfigLine;
        FieldLine: Record "FFL Field Line";
    begin
        ConfigLine.Init();
        ConfigLine.Validate(ReportId, 1);
        ConfigLine.Validate(LineNo, 10000);
        ConfigLine.Validate(TargetTableNo, 61700);
        ConfigLine.Insert(true);

        FieldLine.Init();
        FieldLine.Validate(ReportId, 1);
        FieldLine.Validate(ConfigLineNo, 10000);
        FieldLine.Validate(LineNo, 10000);
        FieldLine.Insert(true);

        FieldLine.CalcFields(TargetTableNo);
        Assert.AreEqual(61700, FieldLine.TargetTableNo,
            'CalcFields on a lookup() FlowField whose source table name is unquoted must resolve the real seeded value, not silently stay 0');
    end;

    // Negative control: when NO matching config line exists at all, the FlowField must
    // still (faithfully) resolve to 0 — distinguishing "genuinely no match" from the bug
    // above ("always 0 regardless of whether a match exists, because the formula never
    // parsed"). This proves the fix did not turn the lookup into an always-matches stub.
    [Test]
    procedure LookupFlowFieldWithNoMatchingConfigLineStaysZero()
    var
        FieldLine: Record "FFL Field Line";
    begin
        FieldLine.Init();
        FieldLine.Validate(ReportId, 2);
        FieldLine.Validate(ConfigLineNo, 99999); // no FFLConfigLine row seeded for this key
        FieldLine.Validate(LineNo, 10000);
        FieldLine.Insert(true);

        FieldLine.CalcFields(TargetTableNo);
        Assert.AreEqual(0, FieldLine.TargetTableNo,
            'CalcFields on a lookup() FlowField with no matching source row must resolve to 0 (faithful "not found"), proving the fix reads real data rather than always matching');
    end;
}
