codeunit 500001 "Repro SetValue Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestPage_SetValue_PersistsToRecord()
    var
        ReproRec: Record "Repro SetValue Tab";
        ReproPage: TestPage "Repro SetValue Card";
    begin
        ReproRec.Init();
        ReproRec."No." := 1;
        ReproRec.Description := 'initial';
        ReproRec.Insert();

        ReproPage.OpenEdit();
        ReproPage.GoToRecord(ReproRec);
        ReproPage.Description.SetValue('written');
        ReproPage.Close();

        ReproRec.Get(1);
        Assert.AreEqual('written', ReproRec.Description, 'TestPage SetValue must persist to underlying record');
    end;

    [Test]
    procedure TestPage_SetValue_View_Errors()
    var
        ReproRec: Record "Repro SetValue Tab";
        ReproPage: TestPage "Repro SetValue Card";
    begin
        ReproRec.Init();
        ReproRec."No." := 2;
        ReproRec.Description := 'initial';
        ReproRec.Insert();

        ReproPage.OpenView();
        ReproPage.GoToRecord(ReproRec);
        asserterror ReproPage.Description.SetValue('blocked');
    end;
}
