codeunit 55101 "Test InitValue"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure InitAppliesIntegerInitValue()
    var
        Rec: Record "Init Value Table";
    begin
        // InitValue = 1 on Status field — Init() must set it to 1, not 0
        Rec.Init();
        Assert.AreEqual(1, Rec.Status, 'Status must equal InitValue = 1 after Init()');
    end;

    [Test]
    procedure InitAppliesTextInitValue()
    var
        Rec: Record "Init Value Table";
    begin
        // InitValue = 'Default' on Name field
        Rec.Init();
        Assert.AreEqual('Default', Rec.Name, 'Name must equal InitValue = ''Default'' after Init()');
    end;

    [Test]
    procedure InitAppliesBooleanInitValue()
    var
        Rec: Record "Init Value Table";
    begin
        // InitValue = true on Active field
        Rec.Init();
        Assert.AreEqual(true, Rec.Active, 'Active must equal InitValue = true after Init()');
    end;

    [Test]
    procedure InitAppliesDecimalInitValue()
    var
        Rec: Record "Init Value Table";
    begin
        // InitValue = 9.99 on Amount field
        Rec.Init();
        Assert.AreEqual(9.99, Rec.Amount, 'Amount must equal InitValue = 9.99 after Init()');
    end;

    [Test]
    procedure FieldWithoutInitValueStaysAtTypeDefault()
    var
        Rec: Record "Init Value Table";
    begin
        // NoInit has no InitValue — must remain 0 after Init()
        Rec.Init();
        Assert.AreEqual(0, Rec.NoInit, 'NoInit field with no InitValue must remain 0 after Init()');
    end;

    [Test]
    procedure InitOverwritesPreviousFieldValue()
    var
        Rec: Record "Init Value Table";
    begin
        // Manually set a value, then Init() — InitValue must overwrite it
        Rec.Status := 99;
        Rec.Name := 'Something';
        Rec.Init();
        Assert.AreEqual(1, Rec.Status, 'Init() must overwrite Status with InitValue = 1');
        Assert.AreEqual('Default', Rec.Name, 'Init() must overwrite Name with InitValue = ''Default''');
    end;
}
