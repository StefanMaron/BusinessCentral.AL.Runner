codeunit 59531 "Dict Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Dict Helper";

    [Test]
    procedure Count_ReturnsEntryCount()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        Assert.AreEqual(3, Helper.CountOf(d), 'Count must be 3 after Add("one"),("two"),("three")');
    end;

    [Test]
    procedure Get_ReturnsValueByKey()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        Assert.AreEqual(1, Helper.GetByKey(d, 'one'), 'Get("one") must return 1');
        Assert.AreEqual(2, Helper.GetByKey(d, 'two'), 'Get("two") must return 2');
        Assert.AreEqual(3, Helper.GetByKey(d, 'three'), 'Get("three") must return 3');
    end;

    [Test]
    procedure Get_MissingKey_Errors()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        asserterror Helper.GetByKey(d, 'missing');
    end;

    [Test]
    procedure ContainsKey_TrueForPresent_FalseForAbsent()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        Assert.IsTrue(Helper.ContainsKey(d, 'one'), 'ContainsKey("one") must be true');
        Assert.IsTrue(Helper.ContainsKey(d, 'two'), 'ContainsKey("two") must be true');
        Assert.IsFalse(Helper.ContainsKey(d, 'missing'), 'ContainsKey("missing") must be false');
    end;

    [Test]
    procedure AddDuplicate_Errors()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        asserterror d.Add('one', 99);
    end;

    [Test]
    procedure Set_OverwritesExistingValue()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        Helper.SetValue(d, 'two', 20);
        Assert.AreEqual(20, Helper.GetByKey(d, 'two'), 'Set must overwrite existing value');
        Assert.AreEqual(3, Helper.CountOf(d), 'Set on existing key must not change count');
    end;

    [Test]
    procedure Set_AddsWhenKeyAbsent()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        Helper.SetValue(d, 'four', 4);
        Assert.AreEqual(4, Helper.GetByKey(d, 'four'), 'Set must add when key absent');
        Assert.AreEqual(4, Helper.CountOf(d), 'Set must increase count when adding');
    end;

    [Test]
    procedure Remove_PresentKey_ReturnsTrueAndDrops()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        Assert.IsTrue(Helper.RemoveKey(d, 'one'), 'Remove of present key must return true');
        Assert.AreEqual(2, Helper.CountOf(d), 'Count must drop to 2');
        Assert.IsFalse(Helper.ContainsKey(d, 'one'), 'Key one must be gone');
    end;

    [Test]
    procedure Remove_MissingKey_ReturnsFalse()
    var
        d: Dictionary of [Text, Integer];
    begin
        d := Helper.Build();
        Assert.IsFalse(Helper.RemoveKey(d, 'missing'), 'Remove of missing key must return false');
        Assert.AreEqual(3, Helper.CountOf(d), 'Count must be unchanged');
    end;

    [Test]
    procedure Values_ReturnsAllValues()
    var
        d: Dictionary of [Text, Integer];
    begin
        // Sum of 1+2+3 = 6 regardless of iteration order, so this is order-insensitive.
        d := Helper.Build();
        Assert.AreEqual(6, Helper.SumValues(d), 'SumValues must be 1+2+3=6');
    end;

    [Test]
    procedure Keys_ReturnsAllKeys()
    var
        d: Dictionary of [Text, Integer];
    begin
        // Set-membership check — Keys() iteration order is not guaranteed, but
        // the set of keys must be exactly the three build keys.
        d := Helper.Build();
        Assert.IsTrue(Helper.AllBuildKeysPresent(d),
            'Keys must contain one, two, and three');
        Assert.AreEqual(3, Helper.KeysCount(d), 'Keys count must equal dictionary count');
    end;

    [Test]
    procedure EmptyDictionary_CountZero_NoKeys()
    var
        d: Dictionary of [Text, Integer];
    begin
        // Guard against a no-op Build() — a constructor returning an empty
        // dict would still pass CountOf<=3 but must fail CountOf=0.
        Assert.AreEqual(0, Helper.CountOf(d), 'Fresh dictionary Count must be 0');
        Assert.IsFalse(Helper.ContainsKey(d, 'anything'), 'Fresh dictionary ContainsKey must be false');
    end;

    [Test]
    procedure Build_NotEmpty_NegativeTrap()
    var
        d: Dictionary of [Text, Integer];
    begin
        // Negative: guard against Build() silently returning an empty dict.
        d := Helper.Build();
        Assert.AreNotEqual(0, Helper.CountOf(d), 'Build must not return an empty dictionary');
    end;
}
