/// <summary>#5339: a FlowField over a table that does not exist, so the compile drops this table.</summary>
table 72213 "MOD Dropped Table"
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
