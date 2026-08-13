/// #1710: a pageextension whose object id is ALSO the id of `page 64223 "EOM Card"`.
/// AL gives `page` and `pageextension` separate id namespaces, so this is legal — and it
/// used to overwrite the page's entry, taking the page's SourceTable and its whole
/// control map with it.
pageextension 64223 "EOM Other Ext" extends "EOM Other"
{
    layout
    {
        addlast(content)
        {
            field(OtherNoteField; Rec."Note") { ApplicationArea = All; }
        }
    }
}

/// #1711(b): a field control added to "EOM Card" by a pageextension. Its control id lives
/// in THIS object's id space (BC's IdSpace.GetMemberId hashes the declaring object's id),
/// which is what a TestPage asks for when the test drives NoteField.
pageextension 64225 "EOM Card Ext" extends "EOM Card"
{
    layout
    {
        addlast(content)
        {
            field(NoteField; Rec."Note") { ApplicationArea = All; }
        }
        modify(DescField) { Editable = false; }
    }
}
