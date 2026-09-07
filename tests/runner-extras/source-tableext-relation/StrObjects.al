// The related table this bundle's tableextension points at. Compiled from AL SOURCE, so the
// relation's TARGET is a source-parsed table while the relation's HOST is a precompiled one —
// which is the crossing this bundle exists to pin.
table 65721 "STR Related"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { DataClassification = CustomerContent; }
    }

    keys { key(PK; "Code") { Clustered = true; } }
}

// A SOURCE-PARSED tableextension on a PRECOMPILED base table (Job, 167, Base Application).
//
// The base table's metadata comes from BC's own precompiled metadata; these fields come from
// RecordPatches.AlSourceParser's ParseFieldSyntax and are grafted on by MergeExtensionFields.
// RelationArms has to survive that graft, and nothing asserted that it does.
tableextension 65722 "STR Job Ext" extends Job
{
    fields
    {
        // The subject: a plain single-arm relation declared by a source-parsed extension.
        // No OnValidate and no ValidateTableRelation, so the relation check is the only thing
        // in Validate that can raise on it.
        field(65723; "STR Ext Rel Code"; Code[20])
        {
            DataClassification = CustomerContent;
            TableRelation = "STR Related"."Code";
        }

        // Control for the SECOND property: same shape, but ValidateTableRelation = false. A
        // change that switched relation checking on wholesale rather than reading both
        // properties makes this refuse a value real BC accepts — the inverse of #3286's
        // reported symptom, and equally silent.
        field(65724; "STR Ext Rel No Validate"; Code[20])
        {
            DataClassification = CustomerContent;
            TableRelation = "STR Related"."Code";
            ValidateTableRelation = false;
        }

        // Control: same extension, same type and length, NO TableRelation. This is what makes
        // the refusal an assertion about the relation rather than about extension fields in
        // general.
        field(65725; "STR Ext No Rel"; Code[20])
        {
            DataClassification = CustomerContent;
        }
    }
}
