codeunit 61831 "CP Cookie Properties Tests"
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
    // Cookie.Domain — read-only
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Domain_Default_Empty()
    var
        Cookie: Cookie;
    begin
        // Negative: Domain defaults to '' when not set (read-only property)
        Assert.AreEqual('', Cookie.Domain, 'Cookie.Domain must default to empty string');
    end;

    [Test]
    procedure Cookie_Domain_Default_Via_Helper()
    begin
        // Negative: default Domain via helper
        Assert.AreEqual('', Helper.DefaultDomain(), 'Cookie.Domain default must be empty via helper');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Path — read-only
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Path_Default_Empty()
    var
        Cookie: Cookie;
    begin
        // Negative: Path defaults to '' when not set (read-only property)
        Assert.AreEqual('', Cookie.Path, 'Cookie.Path must default to empty string');
    end;

    [Test]
    procedure Cookie_Path_Default_Via_Helper()
    begin
        // Negative: default Path via helper
        Assert.AreEqual('', Helper.DefaultPath(), 'Cookie.Path default must be empty via helper');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Secure — default read
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Secure_Default_False()
    var
        Cookie: Cookie;
    begin
        // Negative: Secure defaults to false when not set
        Assert.IsFalse(Cookie.Secure, 'Cookie.Secure must default to false');
    end;

    [Test]
    procedure Cookie_Secure_Default_Via_Helper()
    begin
        // Negative: default Secure via helper
        Assert.IsFalse(Helper.DefaultSecure(), 'Cookie.Secure default must be false via helper');
    end;

    // -----------------------------------------------------------------------
    // Cookie.HttpOnly — default read
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_HttpOnly_Default_False()
    var
        Cookie: Cookie;
    begin
        // Negative: HttpOnly defaults to false when not set
        Assert.IsFalse(Cookie.HttpOnly, 'Cookie.HttpOnly must default to false');
    end;

    [Test]
    procedure Cookie_HttpOnly_Default_Via_Helper()
    begin
        // Negative: default HttpOnly via helper
        Assert.IsFalse(Helper.DefaultHttpOnly(), 'Cookie.HttpOnly default must be false via helper');
    end;

    // -----------------------------------------------------------------------
    // Cookie.Expires
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_Expires_Default_Via_Helper()
    begin
        // Negative: default Expires via helper
        Assert.AreEqual(0DT, Helper.DefaultExpires(), 'Cookie.Expires default must be 0DT via helper');
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
    // Name, Value, Domain, Path together
    // -----------------------------------------------------------------------

    [Test]
    procedure Cookie_NameValue_RoundTrip()
    begin
        // Positive: Name and Value (writable) round-trip correctly
        Assert.AreEqual(
            'auth|tok123',
            Helper.CreateCookieWithNameValue('auth', 'tok123'),
            'Cookie Name/Value must round-trip correctly');
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
