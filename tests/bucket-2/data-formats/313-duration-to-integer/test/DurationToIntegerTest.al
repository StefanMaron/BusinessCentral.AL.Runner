codeunit 1313001 "Duration To Integer Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";

    // Positive: Duration → Integer implicit conversion preserves millisecond value
    [Test]
    procedure DurationToInteger_AssignsMilliseconds()
    var
        d: Duration;
        i: Integer;
    begin
        d := 5000; // 5 seconds in ms
        i := d;
        Assert.AreEqual(5000, i, 'Duration→Integer must preserve millisecond value');
    end;

    // Positive: HttpClient.Timeout := Duration compiles and round-trips
    [Test]
    procedure HttpClientTimeout_SetFromDuration()
    var
        Client: HttpClient;
        Timeout: Duration;
        Retrieved: Integer;
    begin
        Timeout := 30000; // 30 seconds in ms
        Client.Timeout := Timeout;
        Retrieved := Client.Timeout;
        Assert.AreEqual(30000, Retrieved, 'HttpClient.Timeout must store Duration as Integer milliseconds');
    end;

    // Positive: Integer → Duration assignment works too (round-trip)
    [Test]
    procedure IntegerToDuration_Compiles()
    var
        d: Duration;
        i: Integer;
    begin
        i := 7500;
        d := i;
        i := d;
        Assert.AreEqual(7500, i, 'Integer→Duration→Integer round-trip must preserve value');
    end;
}
