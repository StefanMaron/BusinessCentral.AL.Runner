codeunit 50201 IsolationTest
{
    Subtype = Test;

    [Test]
    procedure TestA()
    var
        G: Codeunit Greeter;
    begin
        Message('from A');
        if G.Hello() <> 'hello' then Error('expected hello');
    end;

    [Test]
    procedure TestB()
    var
        G: Codeunit Greeter;
    begin
        Message('from B');
        if G.Hello() <> 'hello' then Error('expected hello');
    end;
}
