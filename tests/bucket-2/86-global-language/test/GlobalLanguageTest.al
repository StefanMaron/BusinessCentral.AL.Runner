codeunit 50861 "Global Language Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GlobalLanguageReturnsPositiveInteger()
    var
        Api: Codeunit "Global Language Api";
        Lang: Integer;
    begin
        // Positive: GlobalLanguage() must return a positive integer without crashing.
        Lang := Api.GetCurrentLanguage();
        Assert.IsTrue(Lang > 0, 'GlobalLanguage() must return a positive integer');
    end;

    [Test]
    procedure GlobalLanguageDefaultIsENU()
    var
        Api: Codeunit "Global Language Api";
        Lang: Integer;
    begin
        // Positive: Default language should be 1033 (ENU).
        Lang := Api.GetCurrentLanguage();
        Assert.AreEqual(1033, Lang, 'Default GlobalLanguage should be 1033 (ENU)');
    end;

    [Test]
    procedure GlobalLanguageSaveSetRestore()
    var
        Api: Codeunit "Global Language Api";
        Result: Integer;
    begin
        // Positive: Save/set/restore round-trip must return the original value.
        Result := Api.SetAndRestoreLanguage();
        Assert.AreEqual(1033, Result, 'After save/set/restore, GlobalLanguage must equal the original');
    end;

    [Test]
    procedure GlobalLanguageSetAndGetRoundTrip()
    var
        Api: Codeunit "Global Language Api";
        Result: Integer;
    begin
        // Positive: Set to a specific language ID, then get it back.
        Result := Api.GetLanguageAfterSet(1031);
        Assert.AreEqual(1031, Result, 'GlobalLanguage should return 1031 after being set to 1031');
    end;

    [Test]
    procedure GlobalLanguageMustNotBeZero()
    var
        Api: Codeunit "Global Language Api";
        Lang: Integer;
    begin
        // Negative: GlobalLanguage() must NOT return zero — a zero value would indicate
        // the getter is broken (e.g., uninitialized field).
        Lang := Api.GetCurrentLanguage();
        asserterror Assert.AreEqual(0, Lang, 'GlobalLanguage should not be zero');
        Assert.ExpectedError('GlobalLanguage should not be zero');
    end;
}
