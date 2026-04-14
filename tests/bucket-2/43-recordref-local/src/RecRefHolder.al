codeunit 56440 "RecRef Holder"
{
    procedure DeclareOnly(): Integer
    var
        // Just declaring RecordRef in a local used to cascade-exclude the whole codeunit.
        RecRef: RecordRef;
    begin
        exit(1);
    end;

    procedure DeclareAndClear(): Integer
    var
        RecRef: RecordRef;
    begin
        // Clear on a RecordRef is a no-op — must still leave the codeunit runnable.
        Clear(RecRef);
        exit(2);
    end;
}
