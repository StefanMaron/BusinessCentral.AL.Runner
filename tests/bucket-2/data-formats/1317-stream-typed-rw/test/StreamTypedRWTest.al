/// Proving tests for OutStream.Write and InStream.Read typed overloads (issue #1400).
///
/// Covers (status: not-tested → covered):
///   OutStream.Write (Integer)            — positive + negative
///   OutStream.Write (Boolean)            — positive + negative
///   OutStream.Write (Decimal)            — positive + negative
///   OutStream.Write (Integer, Integer)   — 2-arg length form
///   OutStream.Write (Boolean, Integer)   — 2-arg length form
///   InStream.Read (Integer)              — positive + negative
///   InStream.Read (Boolean)              — positive + negative
///   InStream.Read (Decimal)              — positive + negative
///   InStream.Read (Integer, Integer)     — 2-arg length form
///   InStream.Read (Boolean, Integer)     — 2-arg length form
///   InStream.Read (Decimal, Integer)     — 2-arg length form
codeunit 1317001 "Stream Typed RW Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Stream Typed RW Src";

    // ── OutStream.Write (Integer) + InStream.Read (Integer) ──────────────────

    [Test]
    procedure WriteInt_ReadInt_RoundTrips()
    begin
        Assert.AreEqual(42, Src.WriteInt_ReadInt(42),
            'OutStream.Write(Integer) + InStream.Read(Integer) must round-trip a non-zero integer');
    end;

    [Test]
    procedure WriteInt_ReadInt_DifferentValues_DifferentResults()
    begin
        Assert.AreNotEqual(
            Src.WriteInt_ReadInt(1),
            Src.WriteInt_ReadInt(2),
            'Different integers must produce different read-back values');
    end;

    // ── OutStream.Write (Boolean) + InStream.Read (Boolean) ──────────────────

    [Test]
    procedure WriteBool_ReadBool_True_RoundTrips()
    begin
        Assert.AreEqual(true, Src.WriteBool_ReadBool(true),
            'OutStream.Write(Boolean true) + InStream.Read(Boolean) must round-trip true');
    end;

    [Test]
    procedure WriteBool_ReadBool_False_RoundTrips()
    begin
        Assert.AreEqual(false, Src.WriteBool_ReadBool(false),
            'OutStream.Write(Boolean false) + InStream.Read(Boolean) must round-trip false');
    end;

    [Test]
    procedure WriteBool_ReadBool_TrueAndFalse_Differ()
    begin
        Assert.AreNotEqual(
            Src.WriteBool_ReadBool(true),
            Src.WriteBool_ReadBool(false),
            'true and false must produce different read-back boolean values');
    end;

    // ── OutStream.Write (Decimal) + InStream.Read (Decimal) ──────────────────

    [Test]
    procedure WriteDecimal_ReadDecimal_RoundTrips()
    begin
        Assert.AreEqual(3.14, Src.WriteDecimal_ReadDecimal(3.14),
            'OutStream.Write(Decimal) + InStream.Read(Decimal) must round-trip a decimal value');
    end;

    [Test]
    procedure WriteDecimal_ReadDecimal_DifferentValues_Differ()
    begin
        Assert.AreNotEqual(
            Src.WriteDecimal_ReadDecimal(1.5),
            Src.WriteDecimal_ReadDecimal(2.5),
            'Different decimal values must produce different read-back results');
    end;

    // ── OutStream.Write (Integer, Integer) — 2-arg form ──────────────────────

    [Test]
    procedure WriteInt_WithLength_RoundTrips()
    begin
        Assert.AreEqual(255, Src.WriteInt_WithLength(255, 4),
            'OutStream.Write(Integer, Length) + InStream.Read(Integer) must round-trip');
    end;

    // ── OutStream.Write (Boolean, Integer) — 2-arg form ──────────────────────

    [Test]
    procedure WriteBool_WithLength_RoundTrips()
    begin
        Assert.AreEqual(true, Src.WriteBool_WithLength(true, 1),
            'OutStream.Write(Boolean, Length) + InStream.Read(Boolean) must round-trip');
    end;

    // ── InStream.Read (Integer, Integer) — 2-arg form ────────────────────────

    [Test]
    procedure ReadInt_WithLength_RoundTrips()
    begin
        Assert.AreEqual(100, Src.ReadInt_WithLength(100, 4),
            'InStream.Read(Integer, MaxLength) must read the stored integer');
    end;

    // ── InStream.Read (Boolean, Integer) — 2-arg form ────────────────────────

    [Test]
    procedure ReadBool_WithLength_RoundTrips()
    begin
        Assert.AreEqual(false, Src.ReadBool_WithLength(false, 1),
            'InStream.Read(Boolean, MaxLength) must read the stored boolean');
    end;

    // ── InStream.Read (Decimal, Integer) — 2-arg form ────────────────────────

    [Test]
    procedure ReadDecimal_WithLength_RoundTrips()
    begin
        Assert.AreEqual(7.77, Src.ReadDecimal_WithLength(7.77, 16),
            'InStream.Read(Decimal, MaxLength) must read the stored decimal');
    end;
}
