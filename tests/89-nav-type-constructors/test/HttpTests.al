codeunit 56901 "HTTP Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // --- Positive tests ---

    [Test]
    procedure ValidHttpUrlAccepted()
    var
        Probe: Codeunit "HTTP Probe";
    begin
        // [GIVEN] A well-formed http URL
        // [WHEN]  IsValidUrl is called
        // [THEN]  Returns true
        Assert.IsTrue(Probe.IsValidUrl('http://example.com'), 'http URL must be valid');
    end;

    [Test]
    procedure ValidHttpsUrlAccepted()
    var
        Probe: Codeunit "HTTP Probe";
    begin
        // [GIVEN] A well-formed https URL
        // [WHEN]  IsValidUrl is called
        // [THEN]  Returns true
        Assert.IsTrue(Probe.IsValidUrl('https://api.example.com/v1/data'), 'https URL must be valid');
    end;

    // --- Negative tests ---

    [Test]
    procedure EmptyUrlRejected()
    var
        Probe: Codeunit "HTTP Probe";
    begin
        // [GIVEN] An empty string
        // [WHEN]  IsValidUrl is called
        // [THEN]  Returns false
        Assert.IsFalse(Probe.IsValidUrl(''), 'Empty string must not be a valid URL');
    end;

    [Test]
    procedure RelativeUrlRejected()
    var
        Probe: Codeunit "HTTP Probe";
    begin
        // [GIVEN] A relative path (no scheme)
        // [WHEN]  IsValidUrl is called
        // [THEN]  Returns false
        Assert.IsFalse(Probe.IsValidUrl('/api/resource'), 'Relative URL must not be valid');
    end;

    [Test]
    procedure FtpSchemeRejected()
    var
        Probe: Codeunit "HTTP Probe";
    begin
        // [GIVEN] An ftp:// URL
        // [WHEN]  IsValidUrl is called
        // [THEN]  Returns false
        Assert.IsFalse(Probe.IsValidUrl('ftp://files.example.com'), 'ftp URL must not be valid');
    end;
}
