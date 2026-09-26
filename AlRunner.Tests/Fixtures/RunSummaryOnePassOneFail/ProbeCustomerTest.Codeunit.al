codeunit 50150 "Probe Customer Test"
{
    Subtype = Test;

    [Test]
    procedure CustomerInsertPasses()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic is broken');
    end;

    [Test]
    procedure CustomerNameFails()
    begin
        Error('customer name: expected Expected, got Actual');
    end;
}
