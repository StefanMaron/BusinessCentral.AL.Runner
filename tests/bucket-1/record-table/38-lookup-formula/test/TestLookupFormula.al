codeunit 54100 "Test Lookup Formula"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure LookupReturnsTextFromRelatedRow()
    var
        Parent: Record "LF Parent";
        Child: Record "LF Child";
    begin
        // Positive: lookup FlowField returns the Name of the matching child row.
        Parent.Init();
        Parent."No." := 'P1';
        Parent.Name := 'Parent One';
        Parent.Insert(true);

        Child.Init();
        Child."Entry No." := 1;
        Child."Parent No." := 'P1';
        Child.Name := 'ChildAlpha';
        Child.Amount := 42.5;
        Child.Insert(true);

        Parent.Get('P1');
        Parent.CalcFields("Child Name");
        Assert.AreEqual('ChildAlpha', Parent."Child Name",
            'Lookup should resolve Child.Name for matching row');
    end;

    [Test]
    procedure LookupReturnsDecimalFromRelatedRow()
    var
        Parent: Record "LF Parent";
        Child: Record "LF Child";
    begin
        // Positive: lookup FlowField returns the numeric Amount of the matching child row.
        Parent.Init();
        Parent."No." := 'P2';
        Parent.Name := 'Parent Two';
        Parent.Insert(true);

        Child.Init();
        Child."Entry No." := 2;
        Child."Parent No." := 'P2';
        Child.Name := 'ChildBeta';
        Child.Amount := 99.99;
        Child.Insert(true);

        Parent.Get('P2');
        Parent.CalcFields("Child Amount");
        Assert.AreEqual(99.99, Parent."Child Amount",
            'Lookup should resolve Child.Amount for matching row');
    end;

    [Test]
    procedure LookupReturnsFirstMatchingRow()
    var
        Parent: Record "LF Parent";
        Child: Record "LF Child";
    begin
        // Positive/disambiguation: multiple children — lookup returns the first match.
        Parent.Init();
        Parent."No." := 'P3';
        Parent.Insert(true);

        Child.Init();
        Child."Entry No." := 10;
        Child."Parent No." := 'P3';
        Child.Name := 'FirstChild';
        Child.Insert(true);

        Child.Init();
        Child."Entry No." := 11;
        Child."Parent No." := 'P3';
        Child.Name := 'SecondChild';
        Child.Insert(true);

        Parent.Get('P3');
        Parent.CalcFields("Child Name");
        Assert.AreEqual('FirstChild', Parent."Child Name",
            'Lookup should return the first matching child row');
    end;

    [Test]
    procedure LookupWithNoMatchClearsField()
    var
        Parent: Record "LF Parent";
    begin
        // Negative: no matching child — lookup should clear the field to default.
        Parent.Init();
        Parent."No." := 'P4';
        Parent.Insert(true);

        Parent.Get('P4');
        Parent.CalcFields("Child Name");
        Assert.AreEqual('', Parent."Child Name",
            'Lookup with no matching row should produce empty/default text');

        Parent.CalcFields("Child Amount");
        Assert.AreEqual(0, Parent."Child Amount",
            'Lookup with no matching row should produce 0 for decimal');
    end;
}
