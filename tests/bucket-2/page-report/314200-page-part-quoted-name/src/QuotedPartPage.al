// Regression test for issue #1600.
// A page with:
//   - fileupload() block containing '}' inside a string literal (the bug trigger)
//   - part(UnquotedName; "Page Name With Spaces") declarations (the affected area)
//   - angle-bracket action identifiers like group("<Action...>") (from the reporter's file)
//
// Before the fix, StripPatternedBlock's brace-depth counter did not skip AL
// single-quoted string literals. A '}' inside a ToolTip/Caption like
//   ToolTip = 'Upload } a document';
// caused the scanner to stop too early, stripping only part of the fileupload
// block and leaving the closing '}' in the source. This corrupted subsequent
// part() declarations, producing AL0104/AL0124 parse errors.
//
// The fix (FindMatchingCloseBrace) properly skips single-quoted string literals
// and line comments when scanning for the matching closing brace.
page 1320200 "Quoted Part Host Page"
{
    PageType = Card;

    layout
    {
        area(Content)
        {
            field(DummyField; '')
            {
                ApplicationArea = All;
                Caption = 'Info';
            }
        }
        area(FactBoxes)
        {
            // These two parts use the exact syntax pattern from the reporter's file:
            // unquoted control name, double-quoted page reference with spaces.
            part(QuotedPartFactboxA; "Quoted Part Factbox A")
            {
                ApplicationArea = All;
            }
            part(QuotedPartFactboxB; "Quoted Part Factbox B")
            {
                ApplicationArea = All;
            }
        }
    }

    actions
    {
        area(Processing)
        {
            // fileupload() is stripped by PrepareSourceForStandalone.
            // The ToolTip contains a '}' character — this is the trigger for the
            // old brace-counting bug: the naive counter would stop at this '}' and
            // leave the actual closing '}' of the fileupload block in the source,
            // corrupting the part() declarations that follow.
            fileupload(DocUpload)
            {
                ToolTip = 'Click to upload } a document';
                ApplicationArea = All;
            }
            // Angle-bracket identifiers exactly as in the reporter's CheckLABTestResults.page.al
            group("<Action1000000017>")
            {
                Caption = 'Functions';
                action("<Action1000000043>")
                {
                    Caption = 'Do Something';
                    ApplicationArea = All;

                    trigger OnAction()
                    begin
                        CurrPage.Update(false);
                    end;
                }
            }
        }
    }
}

// Two minimal factbox pages whose names contain spaces — the exact pattern
// that was misidentified as a parse error by the corrupted source.
page 1320201 "Quoted Part Factbox A"
{
    PageType = ListPart;

    layout
    {
        area(Content)
        {
            field(InfoA; 'FactboxA')
            {
                ApplicationArea = All;
                Caption = 'Info A';
            }
        }
    }
}

page 1320202 "Quoted Part Factbox B"
{
    PageType = ListPart;

    layout
    {
        area(Content)
        {
            field(InfoB; 'FactboxB')
            {
                ApplicationArea = All;
                Caption = 'Info B';
            }
        }
    }
}
