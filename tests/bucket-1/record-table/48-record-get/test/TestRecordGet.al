codeunit 55201 "Test Record Get"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetRetrievesRecordByPrimaryKey()
    var
        Rec: Record "Record Get Table";
    begin
        // Insert a record, then Get it by PK — must load the correct row
        Rec.Init();
        Rec.Code := 'ABC';
        Rec.Description := 'Test Record';
        Rec.Quantity := 42;
        Rec.Insert();

        Clear(Rec);
        Rec.Get('ABC');
        Assert.AreEqual('ABC', Rec.Code, 'Code must match inserted value');
        Assert.AreEqual('Test Record', Rec.Description, 'Description must match');
        Assert.AreEqual(42, Rec.Quantity, 'Quantity must match');
    end;

    [Test]
    procedure GetReturnsTrueWhenRecordExists()
    var
        Rec: Record "Record Get Table";
        Found: Boolean;
    begin
        Rec.Init();
        Rec.Code := 'EXIST';
        Rec.Insert();

        Found := Rec.Get('EXIST');
        Assert.AreEqual(true, Found, 'Get must return true when record exists');
    end;

    [Test]
    procedure GetWithNonExistentKeyErrors()
    var
        Rec: Record "Record Get Table";
    begin
        // Get on a key that was never inserted must throw
        asserterror Rec.Get('NONEXIST');
        Assert.ExpectedError('does not exist');
    end;

    [Test]
    procedure GetDistinguishesBetweenDifferentKeys()
    var
        Rec: Record "Record Get Table";
    begin
        // Insert two records, Get should load the one matching the key
        Rec.Init();
        Rec.Code := 'A';
        Rec.Description := 'Alpha';
        Rec.Insert();

        Rec.Init();
        Rec.Code := 'B';
        Rec.Description := 'Beta';
        Rec.Insert();

        Rec.Get('A');
        Assert.AreEqual('Alpha', Rec.Description, 'Get(A) must return Alpha record');

        Rec.Get('B');
        Assert.AreEqual('Beta', Rec.Description, 'Get(B) must return Beta record');
    end;

    [Test]
    procedure GetWithCompositePrimaryKey()
    var
        Rec: Record "Record Get Composite";
    begin
        // Composite PK: (CompanyNo, EntryNo)
        Rec.Init();
        Rec."CompanyNo" := 'COMP1';
        Rec."EntryNo" := 10;
        Rec.Amount := 100.0;
        Rec.Insert();

        Rec.Init();
        Rec."CompanyNo" := 'COMP1';
        Rec."EntryNo" := 20;
        Rec.Amount := 200.0;
        Rec.Insert();

        Clear(Rec);
        Rec.Get('COMP1', 10);
        Assert.AreEqual(100.0, Rec.Amount, 'Get(COMP1,10) must return Amount=100');

        Rec.Get('COMP1', 20);
        Assert.AreEqual(200.0, Rec.Amount, 'Get(COMP1,20) must return Amount=200');
    end;

    [Test]
    procedure GetWithCompositeKeyNotFoundErrors()
    var
        Rec: Record "Record Get Composite";
    begin
        asserterror Rec.Get('NOCOMP', 999);
        Assert.ExpectedError('does not exist');
    end;
}
