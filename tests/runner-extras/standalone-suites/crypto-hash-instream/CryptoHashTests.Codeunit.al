codeunit 64101 "Crypto Hash Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "CH Assert";

    [Test]
    procedure GenerateHash_InStream_Sha256_KnownVector()
    // Regression for the Pageworks Cluster #1 gap: CU 1266 "Cryptography Management" →
    // CU 1279 GenerateHash(InStream, HashAlgorithmType) wraps a .NET MemoryStream in a
    // NavDotNet and calls ALCompiler.DotNetToNavOutStream, whose real body dereferences
    //     parentOfResult.Tree.Session.Company.SharedObjects
    // — null on the headless skeleton → NullReferenceException / ArgumentNullException.
    // Expected value computed independently: printf 'test' | sha256sum (BC formats
    // hash bytes with ToString("X2") → uppercase hex).
    var
        TempBlob: Codeunit "Temp Blob";
        CryptographyManagement: Codeunit "Cryptography Management";
        HashAlgorithmType: Option MD5,SHA1,SHA256,SHA384,SHA512;
        OutStr: OutStream;
        InStr: InStream;
        Hash: Text;
    begin
        TempBlob.CreateOutStream(OutStr);
        OutStr.WriteText('test');
        TempBlob.CreateInStream(InStr);

        Hash := CryptographyManagement.GenerateHash(InStr, HashAlgorithmType::SHA256);

        Assert.AreEqualText(
            '9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08', Hash,
            'SHA256 of ''test'' via GenerateHash(InStream) must match the independently computed digest.');
    end;

    [Test]
    procedure GenerateHash_InStream_Md5_KnownVector()
    var
        TempBlob: Codeunit "Temp Blob";
        CryptographyManagement: Codeunit "Cryptography Management";
        HashAlgorithmType: Option MD5,SHA1,SHA256,SHA384,SHA512;
        OutStr: OutStream;
        InStr: InStream;
        Hash: Text;
    begin
        TempBlob.CreateOutStream(OutStr);
        OutStr.WriteText('test');
        TempBlob.CreateInStream(InStr);

        Hash := CryptographyManagement.GenerateHash(InStr, HashAlgorithmType::MD5);

        // printf 'test' | md5sum → 098f6bcd4621d373cade4e832627b4f6 (uppercased by BC's "X2")
        Assert.AreEqualText(
            '098F6BCD4621D373CADE4E832627B4F6', Hash,
            'MD5 of ''test'' via GenerateHash(InStream) must match the independently computed digest.');
    end;

    [Test]
    procedure GenerateHash_EmptyInStream_ReturnsEmptyText()
    // Negative direction: CU 1279 GenerateHash(InStream) exits with '' when the
    // input stream is at EOS before any bytes are read (the EOS guard branch).
    var
        TempBlob: Codeunit "Temp Blob";
        CryptographyManagement: Codeunit "Cryptography Management";
        HashAlgorithmType: Option MD5,SHA1,SHA256,SHA384,SHA512;
        OutStr: OutStream;
        InStr: InStream;
        Hash: Text;
    begin
        // Create an empty blob stream — EOS immediately.
        TempBlob.CreateOutStream(OutStr);
        TempBlob.CreateInStream(InStr);

        Hash := CryptographyManagement.GenerateHash(InStr, HashAlgorithmType::SHA256);

        Assert.AreEqualText('', Hash,
            'GenerateHash over an empty (EOS) InStream must return empty text, not a digest.');
    end;
}
