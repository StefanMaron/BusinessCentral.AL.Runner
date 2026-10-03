/// <summary>
/// #5264: a subscriber in the test bundle itself (a test codeunit may only subscribe with manual
/// binding, so it is a codeunit of its own). The publisher is in the app, a bundle of its own.
/// </summary>
codeunit 71961 "Lib Sub Test Side"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Lib Sub Publisher", 'OnTestSide', '', false, false)]
    local procedure HandleTestSide()
    var
        Publisher: Codeunit "Lib Sub Publisher";
        Result: Integer;
    begin
        Result := Publisher.MissingFromTest(1);
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Lib Sub Publisher", 'OnOther', '', false, false)]
    local procedure HandleOther()
    var
        Publisher: Codeunit "Lib Sub Publisher";
        Result: Integer;
    begin
        Result := Publisher.MissingFromChain(1);
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Lib Sub Publisher", 'OnViaLib', '', false, false)]
    local procedure HandleViaLib()
    var
        Helper: Codeunit "Lib Sub Helper";
        Result: Integer;
    begin
        Result := Helper.Calc();
    end;
}
