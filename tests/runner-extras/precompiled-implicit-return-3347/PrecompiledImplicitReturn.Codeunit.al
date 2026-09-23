/// #3710 — calling Microsoft's PRECOMPILED AL and getting the value BC's own AL says.
///
/// Two shapes that look identical in AL source and return OPPOSITE values:
///
///   * a `[TryFunction]`, which declares no return type. The attribute synthesizes a
///     Boolean meaning "the body completed without raising", so falling off the end
///     answers TRUE.
///   * a plain method declared with a return type, whose body falls off the end.
///     AL's implicit default return governs, so a Boolean answers FALSE.
///
/// Neither declares a return type in source, neither contains an `exit`, and the only
/// difference is the attribute line above the signature. What makes the pair worth a
/// runner test is that the runner reaches them through Microsoft's PRECOMPILED
/// assemblies rather than through its own emit, so both answers have to survive that
/// route.
///
/// WHAT #3710 ORIGINALLY CLAIMED, AND WHY IT WAS WRONG. The issue recorded Codeunit
/// 2000 "Time Series Management".GetMLForecastCredentials answering `true` as a defect,
/// reasoning that a Boolean method with no `exit` must answer `false`. Its shipped AL
/// (Base Application 28.1.49838.53910, src/System/AI/TimeSeriesManagement.Codeunit.al)
/// reads:
///
///     [NonDebuggable]
///     [TryFunction]
///     [Scope('OnPrem')]
///     procedure GetMLForecastCredentials(var LocalApiUri: Text[250]; var "Key": SecretText;
///                                        var LimitType: Option; var Limit: Decimal)
///
/// It is a [TryFunction]. Its body completes without raising, so `true` is correct and
/// the runner was right all along. The trap is in the symbol: SymbolReference.json
/// carries `ReturnTypeDefinition: {"Name":"Boolean"}` NEXT TO an `Attributes` array
/// naming `TryFunction`, so the Boolean is the attribute's synthesized return rather
/// than a declared one — and reading the return type without the attributes gives
/// exactly the wrong conclusion with nothing to flag it.
///
/// That also explains the "control" the issue relied on. Codeunit 7046 "Price Asset -
/// G/L Account".ValidateUnitOfMeasure has `Attributes: null` — an ordinary Boolean
/// method — which is why it correctly answers `false`. The two were never the same
/// shape, so the differential they appeared to establish did not exist.
///
/// WHERE THE BC CLAIM IS ADJUDICATED. The claim itself — what a [TryFunction] with no
/// `exit` returns, and how it differs from a plain method — is plain BC behaviour and is
/// pinned upstream by corpus codeunit 60349 "Test CU TryFunc NoExit Return", against a
/// real service tier. This bundle is the runner-specific half: it asserts that calling
/// MICROSOFT'S PRECOMPILED copies of both shapes yields those same two answers, which a
/// corpus test cannot express because naming a precompiled method requires calling one.
codeunit 65900 "Precompiled Implicit Return"
{
    Subtype = Test;

    var
        Assert: Codeunit "Pir Assert";

    /// A locally-compiled mirror of Codeunit 2000's body — same signature shape, same
    /// discarded cross-codeunit call, same byref write — but WITHOUT [TryFunction] and
    /// with an explicit `: Boolean`. It is the plain-method arm of the pair, and it is
    /// what the precompiled [TryFunction] must NOT agree with.
    procedure LocalPlainMirrorOfCodeunit2000(var Url: Text[250]): Boolean
    var
        MLKeyVaultMgmt: Codeunit "Machine Learning KeyVaultMgmt.";
        Secret: SecretText;
        LimitType: Option;
        Limit: Decimal;
    begin
        MLKeyVaultMgmt.GetMachineLearningCredentials('MachineLearningForecast', Url, Secret, LimitType, Limit);
        Url := CopyStr(Url + '/execute?api-version=2.0&details=true', 1, 250);
    end;

    [Test]
    procedure PrecompiledTryFunctionThatCompletes_ReturnsTrue()
    var
        TimeSeriesManagement: Codeunit "Time Series Management";
        ApiUrl: Text[250];
        ApiKey: SecretText;
        LimitType: Option;
        Limit: Decimal;
        Result: Boolean;
    begin
        Result := TimeSeriesManagement.GetMLForecastCredentials(ApiUrl, ApiKey, LimitType, Limit);

        // The byref output proves the body ran, so the return value cannot be mistaken
        // for the call having been skipped: BC appends this suffix as its last statement,
        // and the key vault supplies no prefix here.
        Assert.AreEqualText('/execute?api-version=2.0&details=true', ApiUrl,
          'Codeunit 2000.GetMLForecastCredentials must still append its URL suffix.');
        Assert.IsTrue(ApiKey.IsEmpty(),
          'No Azure Key Vault is reachable here, so the returned key must be empty.');

        Assert.IsTrue(Result,
          'Codeunit 2000.GetMLForecastCredentials is a [TryFunction] whose body completed without raising, so it must return true.');
    end;

    [Test]
    procedure LocallyCompiledPlainMethodWithNoExit_ReturnsFalse()
    var
        Url: Text[250];
        Result: Boolean;
    begin
        Result := LocalPlainMirrorOfCodeunit2000(Url);

        Assert.AreEqualText('/execute?api-version=2.0&details=true', Url,
          'The mirror must produce the same byref output as the precompiled original.');
        Assert.IsFalse(Result,
          'Without [TryFunction], a Boolean method whose body falls off the end returns AL''s default false.');
    end;

    [Test]
    procedure PrecompiledTryFunctionAndPlainCopy_DisagreeBecauseOfTheAttribute()
    var
        TimeSeriesManagement: Codeunit "Time Series Management";
        PrecompiledUrl: Text[250];
        LocalUrl: Text[250];
        ApiKey: SecretText;
        LimitType: Option;
        Limit: Decimal;
        Precompiled: Boolean;
        Mirrored: Boolean;
    begin
        Precompiled := TimeSeriesManagement.GetMLForecastCredentials(PrecompiledUrl, ApiKey, LimitType, Limit);
        Mirrored := LocalPlainMirrorOfCodeunit2000(LocalUrl);

        // Identical bodies and identical side effects, called in one run. The return
        // values still differ, and [TryFunction] is the only difference between them.
        // This is the assertion #3710 had inverted: it read the disagreement as a defect
        // in how the runner executes precompiled AL, when it is what AL specifies.
        Assert.AreEqualText(LocalUrl, PrecompiledUrl,
          'Both copies must write the same byref output.');
        Assert.IsTrue(Precompiled,
          'The precompiled copy is a [TryFunction] that completed, so it must answer true.');
        Assert.IsFalse(Mirrored,
          'The locally-compiled copy has no [TryFunction], so it must answer false.');
    end;

    [Test]
    procedure PrecompiledPlainBooleanMethodWithNoExit_ReturnsFalse()
    var
        PriceAssetGLAccount: Codeunit "Price Asset - G/L Account";
        PriceAsset: Record "Price Asset";
        UnitOfMeasure: Record "Unit of Measure";
    begin
        // Codeunit 7046.ValidateUnitOfMeasure is the PRECOMPILED plain-method arm: a
        // Boolean return, no exit, and `Attributes: null` in SymbolReference.json — no
        // [TryFunction]. So AL's implicit default return governs and it answers false,
        // through the same precompiled route that answers true for Codeunit 2000.
        //
        // Keeping both precompiled arms here is what makes the pair discriminate: a
        // runner that answered true for every precompiled method, or false for every
        // one, would fail exactly one of them.
        //
        // Its body is `UnitofMeasure.Get(PriceAsset."Unit of Measure Code");` with the
        // result discarded, so the row has to exist or the Get raises instead of
        // returning -- this suite runs without --test-data in CI, so insert it here
        // rather than depending on a company being loaded.
        if not UnitOfMeasure.Get('PCS') then begin
            UnitOfMeasure.Init();
            UnitOfMeasure.Validate(Code, 'PCS');
            UnitOfMeasure.Insert(true);
        end;

        PriceAsset."Unit of Measure Code" := 'PCS';

        Assert.IsFalse(PriceAssetGLAccount.ValidateUnitOfMeasure(PriceAsset),
          'Codeunit 7046.ValidateUnitOfMeasure has no [TryFunction] and no exit, so it must return false.');
    end;
}
