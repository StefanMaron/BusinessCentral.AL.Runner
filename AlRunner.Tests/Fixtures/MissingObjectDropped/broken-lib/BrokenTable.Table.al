/// <summary>#5339: a FlowField over a table that does not exist, so this table does not compile either.</summary>
table 72223 "MOD Uncompiled Table"
{
    fields
    {
        field(1; A; Integer) { }
        field(2; B; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = lookup("MOD Nonexistent Table".X where(Y = field(A)));
        }
    }
    keys { key(PK; A) { Clustered = true; } }
}
