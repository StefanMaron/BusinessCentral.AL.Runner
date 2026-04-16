codeunit 61801 "CP Cookie Properties Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "CP Helper";

    // -----------------------------------------------------------------------
    // Cookie.Name
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Name_GetSet()
    var
        Cookie: Cookie;
    begin
        // Positive: Name property round-trips correctly
        Cookie.Name := 'session_id';
        Assert.AreEqual('session_id', Cookie.Name, 'Cookie.Name must return the value that was set');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Value
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Value_GetSet()
    var
        Cookie: Cookie;
    begin
        // Positive: Value property round-trips correctly
        Cookie.Value := 'abc123xyz';
        Assert.AreEqual('abc123xyz', Cookie.Value, 'Cookie.Value must return the value that was set');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Domain
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Domain_GetSet()
    var
        Cookie: Cookie;
    begin
        // Positive: Domain property round-trips correctly
        Cookie.Domain := 'example.com';
        Assert.AreEqual('example.com', Cookie.Domain, 'Cookie.Domain must return the value that was set');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Path
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Path_GetSet()
    var
        Cookie: Cookie;
    begin
        // Positive: Path property round-trips correctly
        Cookie.Path := '/api/v1';
        Assert.AreEqual('/api/v1', Cookie.Path, 'Cookie.Path must return the value that was set');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Secure
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Secure_GetSet_True()
    var
        Cookie: Cookie;
    begin
        // Positive: Secure=true round-trips correctly
        Cookie.Secure := true;
        Assert.IsTrue(Cookie.Secure, 'Cookie.Secure must return true when set to true');
    end;

    [Test]
    procedure Cookie_Secure_Default_False()
    var
        Cookie: Cookie;
    begin
        // Negative: Secure defaults to false
        Assert.IsFalse(Cookie.Secure, 'Cookie.Secure must default to false');
    end;

    // -----------------------------------------------------------------------
    // Cookie.HttpOnly
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_HttpOnly_GetSet_True()
    var
        Cookie: Cookie;
    begin
        // Positive: HttpOnly=true round-trips correctly
        Cookie.HttpOnly := true;
        Assert.IsTrue(Cookie.HttpOnly, 'Cookie.HttpOnly must return true when set to true');
    end;

    [Test]
    procedure Cookie_HttpOnly_Default_False()
    var
        Cookie: Cookie;
    begin
        // Negative: HttpOnly defaults to false
        Assert.IsFalse(Cookie.HttpOnly, 'Cookie.HttpOnly must default to false');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Expires
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Expires_GetSet()
    var
        ExpectedDT: DateTime;
    begin
        // Positive: Expires property round-trips correctly
        ExpectedDT := CreateDateTime(20260101D, 120000T);
        Assert.AreEqual(ExpectedDT, Helper.SetAndGetExpires(ExpectedDT),
            'Cookie.Expires must return the DateTime that was set');
    end;

    [Test]
    procedure Cookie_Expires_Default_Zero()
    var
        Cookie: Cookie;
    begin
        // Negative: Expires defaults to 0DT (unset)
        Assert.AreEqual(0DT, Cookie.Expires, 'Cookie.Expires must default to 0DT when not set');
    end;

    // -----------------------------------------------------------------------
    // All properties together
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_AllProperties_RoundTrip()
    begin
        // Positive: all string/bool properties set and read back correctly
        Assert.AreEqual(
            'auth|tok123|auth.example.com|/|true|true',
            Helper.CreateCookieWithProperties('auth', 'tok123', 'auth.example.com', '/', true, true),
            'All Cookie properties must round-trip correctly');
    end;

    // -----------------------------------------------------------------------
    // Error mechanism
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Error()
    begin
        // Negative: error mechanism works correctly
        asserterror Error('expected error');
        Assert.ExpectedError('expected error');
    end;
}
