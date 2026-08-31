/// <summary>
/// Proves that a codeunit compiled ALONGSIDE a crashing `profile` object (see
/// PocBadProfile.Profile.al) still compiles and its [Test] procedures still run.
///
/// RED (before the #2238 fix): Compilation.Emit throws inside BC's own
/// ProfileMetadataEmitter for the broken profile, and because Emit is atomic per
/// module, the WHOLE app — including this codeunit — comes back with 0 emitted
/// sources (EMIT-ZERO). Neither [Test] procedure below ever runs.
///
/// GREEN (after the fix): BcCompiler's emit-retry loop correctly identifies AND maps
/// the crashing profile back to its own source file (previously it could name the
/// object from BC's exception text but never map an ID-less `profile` header back to
/// a file to exclude), drops ONLY that one file, and Program.cs's EMIT-EXCLUDED guard
/// recognises that an all-profile exclusion set can never hide a missing [Test]
/// procedure (a profile has none), so it keeps the recovered module instead of
/// failing the whole bundle.
/// </summary>
codeunit 60801 "POC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "POC Assert";

    /// <summary>Positive: proves the codeunit's own logic actually ran, not a stub
    /// returning a hardcoded value — Add(3, 4) must be the real sum, not 42 or 0.</summary>
    [Test]
    procedure Add_ThreeAndFour_ReturnsSeven()
    var
        Result: Integer;
    begin
        Result := Add(3, 4);
        Assert.AreEqual(7, Result, 'Add(3, 4) must return 7');
    end;

    /// <summary>Negative: dividing by zero must raise a specific, expected error —
    /// not silently return 0 (which a no-op/stub implementation would do).</summary>
    [Test]
    procedure Divide_ByZero_RaisesExpectedError()
    var
        Result: Integer;
    begin
        asserterror Result := Divide(10, 0);
        Assert.AreEqual('Cannot divide by zero.', GetLastErrorText(),
            'Divide(10, 0) must raise the exact expected error');
    end;

    local procedure Add(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;

    local procedure Divide(A: Integer; B: Integer): Integer
    begin
        if B = 0 then
            Error('Cannot divide by zero.');
        exit(A div B);
    end;
}
