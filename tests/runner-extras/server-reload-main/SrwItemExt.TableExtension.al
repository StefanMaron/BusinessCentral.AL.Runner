/// <summary>
/// Adds a field with an OnValidate trigger to "SRW Item" (defined in
/// server-reload-dep). This tableextension does NOT exist when a --server
/// session's first runTests request loads server-reload-dep alone — it only
/// arrives on the SECOND request, once this bundle is also in the sourcePaths
/// (issue #1860).
/// </summary>
tableextension 64450 "SRW Item Ext" extends "SRW Item"
{
    fields
    {
        field(64451; "Extra"; Text[100])
        {
            DataClassification = SystemMetadata;

            trigger OnValidate()
            begin
                Rec.Log := 'validated:' + Rec."Extra";
            end;
        }
    }
}
