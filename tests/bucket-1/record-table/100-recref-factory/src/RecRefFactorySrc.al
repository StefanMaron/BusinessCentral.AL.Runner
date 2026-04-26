codeunit 304000 "RecRef Factory Src"
{
    /// Uses an array of RecordRef variables, which causes the BC compiler
    /// to emit NavRecordRef.Factory (rewritten to MockRecordRef.Factory).
    procedure GetRecRefFromArray(): Integer
    var
        RecRefs: array[3] of RecordRef;
        i: Integer;
    begin
        // Open each RecordRef to a different "table"
        for i := 1 to 3 do
            RecRefs[i].Open(i);

        // Return the table number of the second element to prove array works
        exit(RecRefs[2].Number);
    end;
}
