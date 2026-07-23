// Source table for the lookup() FlowField below. Deliberately named as a
// SINGLE-WORD identifier (no spaces) — the exact shape that AL allows (and
// idiomatically prefers) UNQUOTED in a `lookup(TableName.Field where(...))`
// CalcFormula, and the exact shape RxCalcFormulaParts previously failed to
// parse (its table-name group required double-quotes, unconditionally).
table 61700 FFLConfigLine
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; ReportId; Integer) { }
        field(2; LineNo; Integer) { }
        field(3; TargetTableNo; Integer) { }
    }

    keys
    {
        key(PK; ReportId, LineNo) { Clustered = true; }
    }
}

table 61701 "FFL Field Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; ReportId; Integer) { }
        field(2; ConfigLineNo; Integer) { }
        field(3; LineNo; Integer) { }
        // Mirrors Pageworks' PageworksDSFieldMapLine.TargetTableNo exactly:
        // `lookup(PageworksDSFieldConfigLine.TargetTableNo where(...))` — an
        // UNQUOTED single-word source table name (FFLConfigLine, no spaces).
        field(50; TargetTableNo; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = lookup(FFLConfigLine.TargetTableNo where(ReportId = field(ReportId), LineNo = field(ConfigLineNo)));
            Editable = false;
        }
    }

    keys
    {
        key(PK; ReportId, ConfigLineNo, LineNo) { Clustered = true; }
    }
}
