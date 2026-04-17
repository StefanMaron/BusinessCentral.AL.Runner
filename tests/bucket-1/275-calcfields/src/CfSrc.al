/// Tables and source for CalcFields FlowField tests (issue #864).

// ── Child table — records linked to a parent ──────────────────────────────────
table 116000 "CF Child"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; Id; Integer) { DataClassification = ToBeClassified; }
        field(2; ParentId; Integer) { DataClassification = ToBeClassified; }
        field(3; Amount; Decimal) { DataClassification = ToBeClassified; }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

// ── Parent table — FlowFields computed from CF Child ─────────────────────────
table 116001 "CF Parent"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; Id; Integer) { DataClassification = ToBeClassified; }
        field(2; TotalAmount; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("CF Child".Amount where(ParentId = field(Id)));
        }
        field(3; ChildCount; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = count("CF Child" where(ParentId = field(Id)));
        }
        field(4; HasChildren; Boolean)
        {
            FieldClass = FlowField;
            CalcFormula = exist("CF Child" where(ParentId = field(Id)));
        }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
