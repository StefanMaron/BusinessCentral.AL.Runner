/// Tests for Dictionary of [Guid, Codeunit X] — issues #1239, #1240, #1241.
/// Verifies that NavObjectDictionary with a codeunit value type compiles and runs
/// in al-runner without ITreeObject constraint errors.
codeunit 1239003 "Dict Cu Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Manager: Codeunit "Dict Cu Manager";

    // ------------------------------------------------------------------
    // Positive tests
    // ------------------------------------------------------------------

    [Test]
    procedure Register_ThenCount_ReturnsOne()
    var
        ID: Guid;
        Handle: Codeunit "Dict Cu Task Handle";
    begin
        // Prove Add + Count work for a codeunit-value dictionary.
        ID := CreateGuid();
        Handle.SetName('alpha');
        Manager.Register(ID, Handle);
        Assert.AreEqual(1, Manager.Count(), 'Count must be 1 after one Register');
    end;

    [Test]
    procedure HasTask_PresentKey_ReturnsTrue()
    var
        ID: Guid;
        Handle: Codeunit "Dict Cu Task Handle";
    begin
        // Prove ContainsKey works.
        ID := CreateGuid();
        Handle.SetName('beta');
        Manager.Register(ID, Handle);
        Assert.IsTrue(Manager.HasTask(ID), 'HasTask must return true for a registered ID');
    end;

    [Test]
    procedure HasTask_AbsentKey_ReturnsFalse()
    var
        AbsentID: Guid;
    begin
        // Prove ContainsKey returns false for an unregistered key — issue #1239.
        AbsentID := CreateGuid();
        Assert.IsFalse(Manager.HasTask(AbsentID), 'HasTask must return false for an unknown ID');
    end;

    [Test]
    procedure TryGet_PresentKey_ReturnsTrueAndHandle()
    var
        ID: Guid;
        Handle: Codeunit "Dict Cu Task Handle";
        Retrieved: Codeunit "Dict Cu Task Handle";
    begin
        // Prove ALGet(key, ByRef<TValue>) overload works — issue #1240.
        ID := CreateGuid();
        Handle.SetName('gamma');
        Manager.Register(ID, Handle);
        Assert.IsTrue(Manager.TryGet(ID, Retrieved), 'TryGet must return true for a registered ID');
        Assert.AreEqual('gamma', Retrieved.GetName(), 'Retrieved handle must carry the same name');
    end;

    [Test]
    procedure TryGet_AbsentKey_ReturnsFalse()
    var
        AbsentID: Guid;
        Retrieved: Codeunit "Dict Cu Task Handle";
    begin
        // Prove the out-param overload returns false when key is missing.
        AbsentID := CreateGuid();
        Assert.IsFalse(Manager.TryGet(AbsentID, Retrieved), 'TryGet must return false for an unknown ID');
    end;

    [Test]
    procedure GetKeys_ReturnsRegisteredIDs()
    var
        ID1: Guid;
        ID2: Guid;
        Handle: Codeunit "Dict Cu Task Handle";
        Keys: List of [Guid];
    begin
        // Prove Keys() compiles and returns all registered IDs — issues #1239 / #1241.
        ID1 := CreateGuid();
        ID2 := CreateGuid();
        Handle.SetName('delta');
        Manager.Register(ID1, Handle);
        Handle.SetName('epsilon');
        Manager.Register(ID2, Handle);
        Keys := Manager.GetKeys();
        Assert.AreEqual(2, Keys.Count(), 'Keys must contain both registered IDs');
        Assert.IsTrue(Keys.Contains(ID1), 'Keys must contain ID1');
        Assert.IsTrue(Keys.Contains(ID2), 'Keys must contain ID2');
    end;

    [Test]
    procedure Deregister_PresentKey_ReturnsTrueAndReducesCount()
    var
        ID: Guid;
        Handle: Codeunit "Dict Cu Task Handle";
    begin
        // Prove Remove works.
        ID := CreateGuid();
        Handle.SetName('zeta');
        Manager.Register(ID, Handle);
        Assert.IsTrue(Manager.Deregister(ID), 'Deregister must return true for a present key');
        Assert.AreEqual(0, Manager.Count(), 'Count must drop to 0 after removal');
        Assert.IsFalse(Manager.HasTask(ID), 'HasTask must return false after removal');
    end;

    [Test]
    procedure Deregister_AbsentKey_ReturnsFalse()
    var
        AbsentID: Guid;
    begin
        // Prove Remove returns false for a missing key (not present in dictionary).
        AbsentID := CreateGuid();
        Assert.IsFalse(Manager.Deregister(AbsentID), 'Deregister must return false for an absent key');
    end;

    // ------------------------------------------------------------------
    // Negative test
    // ------------------------------------------------------------------

    [Test]
    procedure TryGet_AfterDeregister_ReturnsFalse()
    var
        ID: Guid;
        Handle: Codeunit "Dict Cu Task Handle";
        Retrieved: Codeunit "Dict Cu Task Handle";
    begin
        // Prove that once deregistered a key is truly gone.
        ID := CreateGuid();
        Handle.SetName('eta');
        Manager.Register(ID, Handle);
        Manager.Deregister(ID);
        Assert.IsFalse(Manager.TryGet(ID, Retrieved),
            'TryGet after Deregister must return false');
    end;
}
