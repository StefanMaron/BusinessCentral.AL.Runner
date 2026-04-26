/// Helper codeunit exercising HttpClient configuration methods — issue #732.
codeunit 94000 "HCC Src"
{
    // SetBaseAddress + GetBaseAddress — round-trip
    procedure SetAndGetBaseAddress(url: Text): Text
    var
        client: HttpClient;
    begin
        client.SetBaseAddress(url);
        exit(client.GetBaseAddress());
    end;

    // Clear resets the base address to empty (instance-method syntax)
    procedure ClearResetsBaseAddress(url: Text): Text
    var
        client: HttpClient;
    begin
        client.SetBaseAddress(url);
        client.Clear();
        exit(client.GetBaseAddress());
    end;

    // GlobalClear resets the base address to empty (global Clear(x) syntax — issue #1334)
    procedure GlobalClearResetsBaseAddress(url: Text): Text
    var
        client: HttpClient;
    begin
        client.SetBaseAddress(url);
        Clear(client);
        exit(client.GetBaseAddress());
    end;

    // UseResponseCookies — write-only setter, must not throw
    procedure UseResponseCookiesDoesNotThrow(val: Boolean): Boolean
    var
        client: HttpClient;
    begin
        client.UseResponseCookies(val);
        exit(true);
    end;

    // UseServerCertificateValidation — write-only setter, must not throw
    procedure UseServerCertificateValidationDoesNotThrow(val: Boolean): Boolean
    var
        client: HttpClient;
    begin
        client.UseServerCertificateValidation(val);
        exit(true);
    end;

    // UseWindowsAuthentication (Text, Text overload) — must not throw
    procedure UseWindowsAuthenticationText(username: Text; password: Text): Boolean
    var
        client: HttpClient;
    begin
        client.UseWindowsAuthentication(username, password);
        exit(true);
    end;

    // UseWindowsAuthentication (Text, Text, Text overload) — must not throw
    procedure UseWindowsAuthenticationTextDomain(username: Text; password: Text; domain: Text): Boolean
    var
        client: HttpClient;
    begin
        client.UseWindowsAuthentication(username, password, domain);
        exit(true);
    end;

    // AddCertificate (Text overload) — must not throw
    procedure AddCertificateByThumbprint(thumbprint: Text): Boolean
    var
        client: HttpClient;
    begin
        client.AddCertificate(thumbprint);
        exit(true);
    end;

    // AddCertificate (Text, Text overload) — must not throw
    procedure AddCertificateByThumbprintAndPassword(thumbprint: Text; pwd: Text): Boolean
    var
        client: HttpClient;
    begin
        client.AddCertificate(thumbprint, pwd);
        exit(true);
    end;
}
