codeunit 72620 "TP Tests"
{
    Subtype = Test;

    // --- What opens a new record starts an insert.

    [Test]
    procedure OpenNew_StartsAnInsert()
    var P: TestPage "TP Rec Card";
    begin
        P.OpenNew();
        P.Close();
    end;

    [Test]
    procedure New_OnAnOpenPage_StartsAnInsert()
    var R: Record "TP Rec"; P: TestPage "TP Rec Card";
    begin
        R.PK := 'N';
        R.Insert(false);
        P.OpenEdit();
        P.New();
        P.Close();
    end;

    // --- What a typed value starts: the field's OnValidate, and the insert, modify or rename it makes.

    [Test]
    procedure SetValue_StartsTheValidateInsertModifyAndRenameOfTheTable()
    var P: TestPage "TP Rec Card";
    begin
        P.OpenNew();
        P.PK.SetValue('A');
        P.Qty.SetValue(5);
        P.Close();
    end;

    [Test]
    procedure SetValue_OnAControlAPageExtensionAddsForATableField_StartsTheTablesTriggers()
    var R: Record "TP Rec"; P: TestPage "TP Rec Card";
    begin
        R.PK := 'EXTRA';
        R.Insert(false);
        P.OpenEdit();
        P.GoToKey('EXTRA');
        P.Extra.SetValue('x');
        P.Close();
    end;

    [Test]
    procedure SetValue_OnAControlBoundToATableExtensionField_StartsTheBaseTablesTriggers()
    var R: Record "TP Rec"; P: TestPage "TP Rec Card";
    begin
        R.PK := 'EXTNOTE';
        R.Insert(false);
        P.OpenEdit();
        P.GoToKey('EXTNOTE');
        P.ExtNote.SetValue('x');
        P.Close();
    end;

    [Test]
    procedure SetValue_OnAControlBoundToAVariable_StartsTheTriggersOfThePagesTable()
    var R: Record "TP Rec"; P: TestPage "TP Rec Card";
    begin
        R.PK := 'TYPED';
        R.Insert(false);
        P.OpenEdit();
        P.GoToKey('TYPED');
        P.Typed.SetValue('x');
        P.Close();
    end;

    [Test]
    procedure SetValue_InAHelperTheTestCalls_StartsTheTablesTriggers()
    var P: TestPage "TP Rec Card";
    begin
        P.OpenEdit();
        TypeIntoThePage(P);
        P.Close();
    end;

    local procedure TypeIntoThePage(var P: TestPage "TP Rec Card")
    begin
        P.Note.SetValue('typed');
    end;

    // --- The table is the page's: another page's table is not touched.

    [Test]
    procedure SetValue_OnAnotherTablesPage_StartsOnlyThatTablesTriggers()
    var P: TestPage "TP Quiet Card";
    begin
        P.OpenNew();
        P.K.SetValue('Q');
        P.Close();
    end;

    [Test]
    procedure SetValue_OnATableWithSubscribersOnly_RaisesItsDatabaseEvents()
    var P: TestPage "TP Evt Card";
    begin
        P.OpenNew();
        P.K.SetValue('E');
        P.V.SetValue(3);
        P.Close();
    end;

    [Test]
    procedure SetValue_OnAPageWithNoSourceTable_CountsForEveryTable()
    var P: TestPage "TP Dialog";
    begin
        P.OpenEdit();
        P.Answer.SetValue('yes');
        P.Close();
    end;

    // --- The controls: nothing here writes a record.

    [Test]
    procedure OpenEditGoToAndRead_StartNothing_IsNotAnnotated()
    var R: Record "TP Rec"; P: TestPage "TP Rec Card"; Seen: Text;
    begin
        R.PK := 'R';
        R.Insert(false);
        P.OpenEdit();
        P.GoToKey('R');
        P.GoToRecord(R);
        Seen := P.Note.Value;
        P.Note.AssertEquals('');
        P.Close();
    end;

    [Test]
    procedure OpenViewAndMoves_StartNothing_IsNotAnnotated()
    var R: Record "TP Rec"; P: TestPage "TP Rec Card";
    begin
        R.PK := 'V';
        R.Insert(false);
        P.OpenView();
        P.First();
        P.Last();
        P.Next();
        P.Previous();
        P.Close();
    end;

    [Test]
    procedure OpenNew_OnAnotherTablesPage_StartsOnlyThatTablesInsert()
    var P: TestPage "TP Quiet Card";
    begin
        P.OpenNew();
        P.Close();
    end;

    [Test]
    procedure Quiet_IsNotAnnotated()
    begin
    end;
}
