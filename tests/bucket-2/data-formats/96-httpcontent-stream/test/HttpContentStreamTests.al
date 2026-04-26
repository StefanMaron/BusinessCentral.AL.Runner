codeunit 56981 "HTTP Content Stream Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Probe: Codeunit "HTTP Content Stream Probe";

    /// <summary>
    /// Positive: GetProbeVersion returns 96 — confirms the codeunit was loaded into
    /// the in-memory assembly, meaning the CS1503 fix worked and WriteBodyFromStream /
    /// ReadBodyIntoStream compiled without errors.
    /// </summary>
    [Test]
    procedure TestProbeCodeunitLoaded()
    begin
        Assert.AreEqual(96, Probe.GetProbeVersion(), 'Probe codeunit must be loaded (stream methods compiled OK)');
    end;

    /// <summary>
    /// Negative: GetProbeVersion must not return a wrong value.
    /// </summary>
    [Test]
    procedure TestProbeVersionIsNot0()
    begin
        Assert.AreNotEqual(0, Probe.GetProbeVersion(), 'Version must not be 0');
    end;

    /// <summary>
    /// Positive: FormatRequestLine builds a correct request line.
    /// Pure-logic test, no HTTP objects created.
    /// </summary>
    [Test]
    procedure TestFormatRequestLine_Post()
    begin
        Assert.AreEqual(
            'POST https://api.example.com/orders',
            Probe.FormatRequestLine('POST', 'https://api.example.com/orders'),
            'FormatRequestLine must return METHOD SPACE URL');
    end;

    /// <summary>
    /// Positive: FormatRequestLine with GET and simple URL.
    /// </summary>
    [Test]
    procedure TestFormatRequestLine_Get()
    begin
        Assert.AreEqual(
            'GET /items',
            Probe.FormatRequestLine('GET', '/items'),
            'GET request line must format correctly');
    end;

    /// <summary>
    /// Negative: FormatRequestLine with empty method and URL returns " ".
    /// </summary>
    [Test]
    procedure TestFormatRequestLine_Empty()
    begin
        Assert.AreEqual(' ', Probe.FormatRequestLine('', ''), 'Empty inputs must produce a single space');
    end;
}
