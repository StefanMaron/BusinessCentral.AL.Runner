codeunit 56701 "PK Fallback Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // Positive: two records with distinct PKs must both land
    [Test]
    procedure DistinctInsertsSucceed()
    var
        Rec: Record "No Key Table";
    begin
        Rec."Entry No." := 1;
        Rec.Description := 'First';
        Rec.Insert();

        Rec.Init();
        Rec."Entry No." := 2;
        Rec.Description := 'Second';
        Rec.Insert();

        Assert.AreEqual(2, Rec.Count(), 'Two distinct inserts should produce two rows');
    end;

    // Negative: inserting a record whose field-1 value already exists must error
    [Test]
    procedure DuplicateInsertErrors()
    var
        Rec: Record "No Key Table";
    begin
        Rec."Entry No." := 42;
        Rec.Description := 'Original';
        Rec.Insert();

        asserterror begin
            Rec.Init();
            Rec."Entry No." := 42;
            Rec.Description := 'Duplicate';
            Rec.Insert();
        end;

        // The table must still have exactly one row
        Assert.AreEqual(1, Rec.Count(), 'Duplicate Insert must not create a second row');
    end;
}
