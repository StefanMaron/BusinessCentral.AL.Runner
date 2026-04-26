codeunit 60431 "VC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VC Src";

    [Test]
    procedure Greater_TrueForNewer()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(2, 0, 0, 0);
        v2 := Version.Create(1, 0, 0, 0);
        Assert.IsTrue(Src.IsGreater(v1, v2),
            'Version 2.0.0.0 > 1.0.0.0 must be true');
    end;

    [Test]
    procedure Less_TrueForOlder()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(1, 0, 0, 0);
        v2 := Version.Create(2, 0, 0, 0);
        Assert.IsTrue(Src.IsLess(v1, v2),
            'Version 1.0.0.0 < 2.0.0.0 must be true');
    end;

    [Test]
    procedure Equal_TrueForSame()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(1, 2, 3, 4);
        v2 := Version.Create(1, 2, 3, 4);
        Assert.IsTrue(Src.IsEqual(v1, v2),
            'Version 1.2.3.4 = 1.2.3.4 must be true');
    end;

    [Test]
    procedure Equal_FalseForDifferent()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(1, 0, 0, 0);
        v2 := Version.Create(2, 0, 0, 0);
        Assert.IsFalse(Src.IsEqual(v1, v2),
            'Version 1.0.0.0 = 2.0.0.0 must be false');
    end;

    [Test]
    procedure GreaterOrEqual_TrueForEqual()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(1, 0, 0, 0);
        v2 := Version.Create(1, 0, 0, 0);
        Assert.IsTrue(Src.IsGreaterOrEqual(v1, v2),
            'Version 1.0.0.0 >= 1.0.0.0 must be true');
    end;

    [Test]
    procedure LessOrEqual_TrueForEqual()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(1, 0, 0, 0);
        v2 := Version.Create(1, 0, 0, 0);
        Assert.IsTrue(Src.IsLessOrEqual(v1, v2),
            'Version 1.0.0.0 <= 1.0.0.0 must be true');
    end;

    [Test]
    procedure NotEqual_TrueForDifferent()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(1, 0, 0, 0);
        v2 := Version.Create(2, 0, 0, 0);
        Assert.IsTrue(Src.IsNotEqual(v1, v2),
            'Version 1.0.0.0 <> 2.0.0.0 must be true');
    end;

    [Test]
    procedure MinorVersionMatters()
    var
        v1, v2 : Version;
    begin
        v1 := Version.Create(1, 1, 0, 0);
        v2 := Version.Create(1, 0, 0, 0);
        Assert.IsTrue(Src.IsGreater(v1, v2),
            'Version 1.1.0.0 must be greater than 1.0.0.0');
    end;
}
