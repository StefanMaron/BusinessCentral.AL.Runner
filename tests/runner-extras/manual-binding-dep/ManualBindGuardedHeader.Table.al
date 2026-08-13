table 62210 "Manual Bind Guarded Header"
{
    // Mirrors the reproducer table from issue #1729: a field OnValidate guarded by
    // Confirm Management.GetResponseOrDefault, so the confirm is raised from inside
    // the record-trigger execution path.
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Template; Boolean)
        {
            InitValue = true;

            trigger OnValidate()
            var
                ConfirmManagement: Codeunit "Confirm Management";
            begin
                if not Template or xRec.Template then
                    exit;
                if "Template Used" = '' then
                    exit;
                if not ConfirmManagement.GetResponseOrDefault('Clear Template Used?', false) then
                    Template := xRec.Template;
            end;
        }
        field(3; "Template Used"; Code[20]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}
