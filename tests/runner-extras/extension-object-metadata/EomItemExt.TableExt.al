/// #1711(a): an Option field ADDED BY A TABLEEXTENSION.
///
/// The extension-field parse used to drop OptionMembers, so this field reached
/// NCLOptionMetadata with an empty option string and Format() of any value answered
/// with nothing to answer from — the same defect class #1674 fixed for base tables.
/// The blank first member is deliberate: it is what makes the member COUNT, not just
/// the member names, observable.
tableextension 64221 "EOM Item Ext" extends "EOM Item"
{
    fields
    {
        field(50000; "EOM Status"; Option)
        {
            DataClassification = CustomerContent;
            OptionMembers = ,Open,Released;
        }
        field(50001; "EOM Plain"; Integer)
        {
            DataClassification = CustomerContent;
        }
    }
}
