/// Table whose OnModify trigger calls Modify on the same record.
/// Without a recursion guard, this would cause a StackOverflowException.
/// In real BC, triggers do NOT re-fire recursively on the same record.
table 108001 "Recursive Trigger Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Counter; Integer) { }
    }

    keys
    {
        key(PK; PK) { Clustered = true; }
    }

    trigger OnModify()
    begin
        // Increment counter and re-modify — would infinitely recurse without guard
        Rec.Counter += 1;
        Rec.Modify(true);  // true = run trigger again → recursion!
    end;
}
