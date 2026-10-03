/// <summary>Reaches the base: a dependent of this folder runs code two folders down. One file, with
/// its own test, so the folder weighs less than the base under --jobs.</summary>
codeunit 65710 "Jobs Dep Mid"
{
    procedure Value(): Integer
    var
        Base: Codeunit "Jobs Dep Base";
    begin
        exit(Base.Value() + 1);
    end;
}

codeunit 65711 "Jobs Dep Mid Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MidValue_Is11()
    var
        Mid: Codeunit "Jobs Dep Mid";
    begin
        if Mid.Value() <> 11 then
            Error('Mid returns the base value plus one');
    end;
}
