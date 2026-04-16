/// Helper codeunit exercising Guid.CreateSequentialGuid().
codeunit 60040 "GCSG Src"
{
    procedure GetSequentialGuid(): Guid
    var
        g: Guid;
    begin
        g := CreateGuid();
        g := Guid.CreateSequentialGuid();
        exit(g);
    end;

    procedure GetTwoSequentialGuids(var g1: Guid; var g2: Guid)
    begin
        g1 := Guid.CreateSequentialGuid();
        g2 := Guid.CreateSequentialGuid();
    end;

    procedure SequentialGuidIsNullGuid(): Boolean
    var
        g: Guid;
    begin
        g := Guid.CreateSequentialGuid();
        exit(IsNullGuid(g));
    end;
}
