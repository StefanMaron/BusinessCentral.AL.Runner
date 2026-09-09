/// #3347 — AL's implicit default return, as executed for MICROSOFT'S PRECOMPILED AL.
///
/// A method declared with a return type whose body completes without `exit(<value>)`
/// returns that type's default. 23 Boolean-returning methods in the shipped Base
/// Application (28.1.49838.53910) have no `exit` anywhere in their bodies and rely on it.
///
/// Codeunit 2000 "Time Series Management".GetMLForecastCredentials is one of them:
///
///     procedure GetMLForecastCredentials(var LocalApiUri: Text[250]; var Key: SecretText;
///                                        var LimitType: Option; var Limit: Decimal): Boolean
///     begin
///         MachineLearningKeyVaultMgmt.GetMachineLearningCredentials(
///             ForecastSecretNameTxt, LocalApiUri, Key, LimitType, Limit);
///         LocalApiUri += '/execute?api-version=2.0&details=true';
///     end;
///
/// No `exit`, so it must answer FALSE. That false is load-bearing: Codeunit 850
/// "Cash Flow Forecast Handler".Initialize logs "You must specify an API URL and an API
/// Key for the Cash Flow Setup" and returns early precisely because GetMLCredentials ->
/// RetrieveSaaSMLCredentials -> GetMLForecastCredentials came back false. When it answers
/// true instead, Initialize walks past that check to the Azure ML quota test and logs
/// "The Microsoft Azure Machine Learning limit has been reached" — which is exactly how
/// four of MS's Tests-Cash Flow Codeunit135203 tests failed (APIKeyNotDefinedError,
/// APIURLNotDefinedError, PrepareDataNotEnoughHistoricalData, AzureAINotEnabledError).
///
/// WHY HERE AND NOT IN THE UPSTREAM CORPUS. The claim is plain BC behaviour, so it was
/// written upstream too — corpus PR #316 pins eight variants of this shape, including a
/// cross-codeunit call whose returned `true` is discarded by the caller. All eight PASS on
/// the runner. The corpus compiles its objects from source, and the runner's own emit of
/// this shape is correct in every variant AL can express; what diverges is the runner's
/// execution of Microsoft's PRECOMPILED copy. Naming a precompiled method requires calling
/// one, which is what this bundle does and what the corpus structurally cannot do.
///
/// The isolation, measured in one run: a locally-compiled mirror of Codeunit 2000's exact
/// body returned false while BC's precompiled original returned true, with both writing the
/// identical byref output — so both bodies ran, and only the return value differs.
codeunit 65900 "Precompiled Implicit Return"
{
    Subtype = Test;

    var
        Assert: Codeunit "Pir Assert";

    /// A locally-compiled mirror of Codeunit 2000's body: Boolean return, no `exit`
    /// anywhere, one cross-codeunit call whose result is discarded, then a byref write.
    /// This is the control — it isolates "precompiled" as the only difference.
    procedure LocalMirrorOfCodeunit2000(var Url: Text[250]): Boolean
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
    procedure PrecompiledBooleanMethodWithNoExit_ReturnsFalse()
    var
        TimeSeriesManagement: Codeunit "Time Series Management";
        ApiUrl: Text[250];
        ApiKey: SecretText;
        LimitType: Option;
        Limit: Decimal;
        Result: Boolean;
    begin
        Result := TimeSeriesManagement.GetMLForecastCredentials(ApiUrl, ApiKey, LimitType, Limit);

        // The byref output proves the body ran, so a false return cannot be mistaken for
        // the call having been skipped: BC appends this suffix as its last statement, and
        // the key vault supplies no prefix here.
        Assert.AreEqualText('/execute?api-version=2.0&details=true', ApiUrl,
          'Codeunit 2000.GetMLForecastCredentials must still append its URL suffix.');
        Assert.IsTrue(ApiKey.IsEmpty(),
          'No Azure Key Vault is reachable here, so the returned key must be empty.');

        Assert.IsFalse(Result,
          'Codeunit 2000.GetMLForecastCredentials has no exit(<value>) in its body, so AL''s implicit default return requires false.');
    end;

    [Test]
    procedure LocallyCompiledMirrorOfTheSameShape_AlsoReturnsFalse()
    var
        Url: Text[250];
        Result: Boolean;
    begin
        Result := LocalMirrorOfCodeunit2000(Url);

        Assert.AreEqualText('/execute?api-version=2.0&details=true', Url,
          'The mirror must produce the same byref output as the precompiled original.');
        Assert.IsFalse(Result,
          'A locally-compiled method of the same shape must return false — this is the control that isolates "precompiled" as the difference.');
    end;

    [Test]
    procedure PrecompiledAndLocallyCompiledCopies_AgreeWithEachOther()
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
        Mirrored := LocalMirrorOfCodeunit2000(LocalUrl);

        // Same body, same run, same byref result. The return values must agree too;
        // whether the AL was precompiled by Microsoft or emitted here is not an
        // observable an AL caller may depend on.
        Assert.AreEqualText(LocalUrl, PrecompiledUrl,
          'Both copies must write the same byref output.');
        Assert.AreEqualBool(Mirrored, Precompiled,
          'A precompiled AL method and a locally-compiled copy of the same body must return the same value.');
    end;

    [Test]
    procedure PrecompiledNoExitMethod_IsNotUniversallyBroken()
    var
        PriceAssetGLAccount: Codeunit "Price Asset - G/L Account";
        PriceAsset: Record "Price Asset";
        UnitOfMeasure: Record "Unit of Measure";
    begin
        // Codeunit 7046.ValidateUnitOfMeasure has the same no-exit Boolean shape and is
        // also precompiled, and it answers correctly. Pinned so the fix is not written as
        // a blanket rule about precompiled Boolean methods, and so a regression here is
        // distinguishable from a regression in the case above.
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
          'Codeunit 7046.ValidateUnitOfMeasure has no exit(<value>) either, so it must also return false.');
    end;
}
