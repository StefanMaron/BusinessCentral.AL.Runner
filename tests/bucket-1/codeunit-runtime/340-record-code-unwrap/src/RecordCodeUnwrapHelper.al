codeunit 1320611 "Record Code Unwrap Helper"
{
    procedure TakeCode(InputCode: Code[20]): Code[20]
    begin
        if InputCode = '' then
            Error('Code must be provided');
        exit(InputCode);
    end;

    procedure TakeFromRecord(Rec: Record "Record Code Unwrap Table"): Code[20]
    begin
        exit(TakeCode(Rec.Code));
    end;

    procedure AppendSuffix(var InputCode: Code[20])
    begin
        if InputCode = '' then
            Error('Code must be provided');
        InputCode := InputCode + 'X';
    end;

    procedure AppendSuffixFromRecord(var Rec: Record "Record Code Unwrap Table")
    begin
        AppendSuffix(Rec.Code);
    end;
}
