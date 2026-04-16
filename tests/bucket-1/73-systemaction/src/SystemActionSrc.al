/// Helper codeunit in the same compilation unit as a page that uses
/// systemaction() declarations. Used to prove the unit compiles correctly.
codeunit 73000 "SA Helper"
{
    procedure GetValue(): Text
    begin
        exit('ok');
    end;

    procedure Multiply(a: Integer; b: Integer): Integer
    begin
        exit(a * b);
    end;
}

/// A page that declares systemaction entries (built-in BC actions such as
/// Print and SendMail). These have no runtime effect in unit tests but the
/// BC compiler emits C# for them; the runner must accept the output.
page 73000 "SA Test Page"
{
    PageType = Card;

    actions
    {
        area(Promoted)
        {
            systemaction(Print) { }
            systemaction(SendMail) { }
        }
    }
}
