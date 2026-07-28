/// <summary>
/// A stream opened with <c>TextEncoding::Windows</c> must write ONE BYTE per representable
/// character.
///
/// BC resolves Windows (and MSDos) to the tenant's default encoding, which on a real service
/// tier is the host's ANSI code page — cp1252 for every western-European deployment, and the
/// encoding the whole WinAnsi convention is built on.
///
/// The consequence of getting this wrong is not a mangled string, it is a wrong LENGTH. AL
/// that assembles a binary format — a PDF content stream, a fixed-width export, anything that
/// computes its own byte offsets — relies on one character costing one byte. Answering UTF-8
/// makes every non-ASCII character three bytes and silently invalidates every offset derived
/// from the text, far away from where the encoding was chosen.
/// </summary>
codeunit 62101 "TEW Tests"
{
    Subtype = Test;

    // One character from each band that separates cp1252 from both ASCII and Latin-1:
    //   —  U+2014 EM DASH        cp1252 0x97, in the 0x80..0x9F block Latin-1 leaves undefined
    //   €  U+20AC EURO SIGN      cp1252 0x80, and not in Latin-1 at all
    //   é  U+00E9 SMALL E ACUTE  cp1252 0xE9, shared with Latin-1
    //   NBSP U+00A0              cp1252 0xA0
    // Only a real cp1252 codec round-trips all four AND keeps them one byte each.
    local procedure SampleText(): Text
    begin
        exit('A' + SpecialChars() + 'Z');
    end;

    local procedure SpecialChars(): Text
    var
        Nbsp: Char;
        EmDash: Char;
        Euro: Char;
        EAcute: Char;
    begin
        EmDash := 8212;   // U+2014
        Euro := 8364;     // U+20AC
        EAcute := 233;    // U+00E9
        Nbsp := 160;      // U+00A0
        exit(Format(EmDash) + Format(Euro) + Format(EAcute) + Format(Nbsp));
    end;

    local procedure WriteWith(Encoding: TextEncoding; Value: Text) ByteCount: Integer
    var
        Row: Record "TEW Blob";
        Out: OutStream;
    begin
        Row.DeleteAll();
        Row.Init();
        Row."No." := 'ROW';
        Row.Insert();

        Row.Data.CreateOutStream(Out, Encoding);
        Out.WriteText(Value);
        Row.Modify();

        Row.Get('ROW');
        Row.CalcFields(Data);
        exit(Row.Data.Length());
    end;

    local procedure ReadBack(Encoding: TextEncoding) Value: Text
    var
        Row: Record "TEW Blob";
        Ins: InStream;
    begin
        Row.Get('ROW');
        Row.CalcFields(Data);
        Row.Data.CreateInStream(Ins, Encoding);
        Ins.ReadText(Value);
    end;

    [Test]
    procedure WindowsEncoding_WritesOneBytePerCharacter()
    var
        Written: Integer;
    begin
        Written := WriteWith(TextEncoding::Windows, SampleText());

        // 'A' + four non-ASCII characters + 'Z' = 6 characters, so 6 bytes. Under UTF-8 the
        // em dash and the euro sign cost 3 bytes each and the other two cost 2, giving 12.
        if Written <> StrLen(SampleText()) then
            Error('TextEncoding::Windows wrote %1 bytes for %2 characters — one character must ' +
                  'cost one byte.', Written, StrLen(SampleText()));
    end;

    [Test]
    procedure WindowsEncoding_RoundTripsTheCharactersItEncoded()
    var
        Written: Integer;
        RoundTripped: Text;
    begin
        Written := WriteWith(TextEncoding::Windows, SampleText());
        RoundTripped := ReadBack(TextEncoding::Windows);

        // Length alone would also be satisfied by a codec that mapped everything it did not
        // know to '?'. Latin-1 does exactly that for the em dash and the euro sign, so this
        // is what separates cp1252 from the nearest wrong answer.
        if RoundTripped <> SampleText() then
            Error('TextEncoding::Windows did not round-trip: wrote <%1> (%2 bytes), read <%3>.',
                SampleText(), Written, RoundTripped);
    end;

    [Test]
    procedure Utf8Encoding_StillWritesMultipleBytesForTheSameCharacters()
    var
        Written: Integer;
    begin
        Written := WriteWith(TextEncoding::UTF8, SampleText());

        // The negative that matters: this fix must change what Windows means, not force every
        // encoding to be single-byte. UTF-8 asked for explicitly must stay UTF-8.
        if Written <= StrLen(SampleText()) then
            Error('TextEncoding::UTF8 wrote %1 bytes for %2 characters — the non-ASCII ones ' +
                  'must take more than one byte each.', Written, StrLen(SampleText()));
    end;

    [Test]
    procedure Utf8Encoding_RoundTripsTheSameText()
    var
        RoundTripped: Text;
    begin
        WriteWith(TextEncoding::UTF8, SampleText());
        RoundTripped := ReadBack(TextEncoding::UTF8);

        if RoundTripped <> SampleText() then
            Error('TextEncoding::UTF8 did not round-trip: wrote <%1>, read <%2>.',
                SampleText(), RoundTripped);
    end;

    [Test]
    procedure AsciiOnlyText_IsUnaffectedByTheEncodingChoice()
    var
        Plain: Text;
    begin
        Plain := 'PLAIN-ASCII-123';

        // Pure ASCII must cost one byte either way. If this ever differs, something is
        // writing a byte-order mark or a line terminator and every length above is suspect.
        if WriteWith(TextEncoding::Windows, Plain) <> StrLen(Plain) then
            Error('TextEncoding::Windows wrote %1 bytes for %2 ASCII characters.',
                WriteWith(TextEncoding::Windows, Plain), StrLen(Plain));
        if WriteWith(TextEncoding::UTF8, Plain) <> StrLen(Plain) then
            Error('TextEncoding::UTF8 wrote %1 bytes for %2 ASCII characters.',
                WriteWith(TextEncoding::UTF8, Plain), StrLen(Plain));
    end;
}
