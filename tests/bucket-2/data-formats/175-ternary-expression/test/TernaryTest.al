codeunit 61221 "Ternary Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Classify_Above3_ReturnsBig()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual('big', S.Classify(5), 'Classify(5) must return ''big''');
    end;

    [Test]
    procedure Classify_At3_ReturnsSmall()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual('small', S.Classify(3), 'Classify(3) must return ''small''');
    end;

    [Test]
    procedure Classify_Below3_ReturnsSmall()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual('small', S.Classify(0), 'Classify(0) must return ''small''');
    end;

    [Test]
    procedure Classify_NotSameForBothBranches()
    var
        S: Codeunit "Ternary Src";
    begin
        // Negative: the two branches must return different values (no constant stub).
        Assert.AreNotEqual(S.Classify(5), S.Classify(1), 'Classify must return different values for different branches');
    end;

    [Test]
    procedure Max_FirstIsLarger()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual(10, S.Max(10, 3), 'Max(10,3) must return 10');
    end;

    [Test]
    procedure Max_SecondIsLarger()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual(7, S.Max(2, 7), 'Max(2,7) must return 7');
    end;

    [Test]
    procedure Max_Equal()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual(5, S.Max(5, 5), 'Max(5,5) must return 5');
    end;

    [Test]
    procedure Bucket_Low()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual('low', S.Bucket(5), 'Bucket(5) must return ''low''');
    end;

    [Test]
    procedure Bucket_Mid()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual('mid', S.Bucket(50), 'Bucket(50) must return ''mid''');
    end;

    [Test]
    procedure Bucket_High()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual('high', S.Bucket(200), 'Bucket(200) must return ''high''');
    end;

    [Test]
    procedure FlipBool_True_ReturnsFalse()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual(false, S.FlipBool(true), 'FlipBool(true) must return false');
    end;

    [Test]
    procedure FlipBool_False_ReturnsTrue()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual(true, S.FlipBool(false), 'FlipBool(false) must return true');
    end;

    [Test]
    procedure AddWithBonus_Proving()
    var
        S: Codeunit "Ternary Src";
    begin
        Assert.AreEqual(8, S.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
    end;
}
