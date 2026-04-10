codeunit 50915 "Codeunit Assign Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestAssignCodeunitVariable()
    var
        Original: Codeunit "Adder";
        Assigned: Codeunit "Adder";
    begin
        // [GIVEN] Two codeunit variables of the same type
        // [WHEN] Assigning one to the other
        Assigned := Original;

        // [THEN] The assigned variable should work the same
        Assert.AreEqual(7, Assigned.Add(3, 4), 'Assigned codeunit Add(3,4) should return 7');
    end;

    [Test]
    procedure TestAssignedCodeunitCallsMultipleMethods()
    var
        Source: Codeunit "Adder";
        Target: Codeunit "Adder";
    begin
        // [GIVEN] A codeunit assigned to another variable
        Target := Source;

        // [WHEN/THEN] Both methods work on the assigned variable
        Assert.AreEqual(5, Target.Add(2, 3), 'Target.Add(2,3) should return 5');
        Assert.AreEqual(10, Target.AddThree(2, 3, 5), 'Target.AddThree(2,3,5) should return 10');
    end;
}
