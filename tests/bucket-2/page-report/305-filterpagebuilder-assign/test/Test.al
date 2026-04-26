codeunit 305002 "FPB Assign Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Assign_CopiesCount()
    var
        Src: Codeunit "FPB Assign Src";
    begin
        Assert.AreEqual(2, Src.AssignAndGetCount(), 'Assigned FPB must preserve count');
    end;

    [Test]
    procedure Assign_CopiesView()
    var
        Src: Codeunit "FPB Assign Src";
    begin
        Assert.AreEqual(
            'WHERE(No.=FILTER(1000..2000))',
            Src.AssignAndGetView(),
            'Assigned FPB must preserve view data');
    end;

    [Test]
    procedure Assign_IsDeepCopy()
    var
        Src: Codeunit "FPB Assign Src";
    begin
        // Deep copy: modifying original after assignment should not affect the copy
        // If ALAssign does a deep copy, count = 1; if shallow, count = 2
        Assert.AreEqual(1, Src.AssignIsDeepCopy(), 'Assignment must be a deep copy — original changes must not affect copy');
    end;
}
