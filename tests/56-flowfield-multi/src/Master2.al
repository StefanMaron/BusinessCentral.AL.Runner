table 56560 "FFM Master"
{
    fields
    {
        field(1; Code1; Code[20]) { }
        field(2; Code2; Code[20]) { }
        field(3; "Exists Flag"; Boolean)
        {
            CalcFormula = exist("FFM Child" where(C1 = field(Code1), C2 = field(Code2)));
            FieldClass = FlowField;
        }
    }
    keys { key(PK; Code1, Code2) { Clustered = true; } }
}

table 56561 "FFM Child"
{
    fields
    {
        field(1; C1; Code[20]) { }
        field(2; C2; Code[20]) { }
    }
    keys { key(PK; C1, C2) { Clustered = true; } }
}
