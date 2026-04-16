/// Helper codeunit that exercises CreateSequentialGuid().
codeunit 82100 "Guid Seq Src"
{
    procedure GetSequentialGuid(): Guid
    var
        g: Guid;
    begin
        g := CreateSequentialGuid();
        exit(g);
    end;

    procedure TwoSequentialGuidsAreDistinct(): Boolean
    var
        g1: Guid;
        g2: Guid;
    begin
        g1 := CreateSequentialGuid();
        g2 := CreateSequentialGuid();
        exit(g1 <> g2);
    end;

    procedure SequentialGuidIsNotNull(): Boolean
    var
        g: Guid;
    begin
        g := CreateSequentialGuid();
        exit(not IsNullGuid(g));
    end;
}
