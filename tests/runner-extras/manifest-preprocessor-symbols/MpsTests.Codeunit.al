codeunit 65963 "MPS Tests"
{
    Subtype = Test;

    local procedure AssertEqual(Expected: Text; Actual: Text; What: Text)
    begin
        if Expected <> Actual then
            Error('%1: expected <%2>, got <%3>', What, Expected, Actual);
    end;

    [Test]
    procedure PageCaptionFollowsManifestSymbol()
    var
        AllObj: Record AllObjWithCaption;
    begin
        AllObj.Get(AllObj."Object Type"::Page, 65960);
        AssertEqual('MPS Defined Caption', AllObj."Object Caption", 'AllObjWithCaption."Object Caption" of page 65960');
    end;

    [Test]
    procedure CodeunitUnderDefinedBranchIsListed()
    var
        AllObj: Record AllObj;
        OnlyDefined: Codeunit "MPS Only Defined";
    begin
        AssertEqual('65961', Format(OnlyDefined.Touch()), 'MPS Only Defined.Touch()');
        AllObj.SetRange("Object Type", AllObj."Object Type"::Codeunit);
        AllObj.SetRange("Object ID", 65961);
        AssertEqual('1', Format(AllObj.Count()), 'AllObj rows for codeunit 65961');
    end;

    [Test]
    procedure CodeunitUnderInactiveBranchIsNotListed()
    var
        AllObj: Record AllObj;
    begin
        AllObj.SetRange("Object Type", AllObj."Object Type"::Codeunit);
        AllObj.SetRange("Object ID", 65962);
        AssertEqual('0', Format(AllObj.Count()), 'AllObj rows for codeunit 65962');
    end;
}
