codeunit 61811 "AL Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ArrayLen_1D_NoArg_Returns10()
    var
        H: Codeunit "AL Helper";
    begin
        Assert.AreEqual(10, H.GetLen1D(), 'ArrayLen on array[10] must return 10');
    end;

    [Test]
    procedure ArrayLen_1D_Dim1_Returns10()
    var
        H: Codeunit "AL Helper";
    begin
        Assert.AreEqual(10, H.GetLen1D_Dim1(), 'ArrayLen(arr, 1) on array[10] must return 10');
    end;

    [Test]
    procedure ArrayLen_2D_Dim1_Returns3()
    var
        H: Codeunit "AL Helper";
    begin
        Assert.AreEqual(3, H.GetLen2D_Dim1(), 'ArrayLen(arr, 1) on array[3,4] must return 3');
    end;

    [Test]
    procedure ArrayLen_2D_Dim2_Returns4()
    var
        H: Codeunit "AL Helper";
    begin
        Assert.AreEqual(4, H.GetLen2D_Dim2(), 'ArrayLen(arr, 2) on array[3,4] must return 4');
    end;

    [Test]
    procedure ArrayLen_IsNotZero()
    var
        H: Codeunit "AL Helper";
    begin
        Assert.AreNotEqual(0, H.GetLen1D(), 'ArrayLen must not return zero for non-empty array');
    end;
}
