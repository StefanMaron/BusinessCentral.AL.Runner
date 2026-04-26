/// Helper codeunit exercising HttpClient ALAssign (`:=` operator) — issue #1447.
codeunit 311000 "HCA Src"
{
    // ConfigureAndAssign — sets base address on source, assigns to target via `:=`, returns target base address.
    procedure ConfigureAndAssign(url: Text): Text
    var
        source: HttpClient;
        target: HttpClient;
    begin
        source.SetBaseAddress(url);
        target := source;
        exit(target.GetBaseAddress());
    end;

    // AssignCopiesHeaders — adds a header to source, assigns, checks target has it.
    procedure AssignCopiesHeaders(headerName: Text; headerValue: Text): Boolean
    var
        source: HttpClient;
        target: HttpClient;
        headers: HttpHeaders;
    begin
        source.DefaultRequestHeaders().Add(headerName, headerValue);
        target := source;
        headers := target.DefaultRequestHeaders();
        exit(headers.Contains(headerName));
    end;

    // AssignedClientIsUsable — assign an empty client, verify GetBaseAddress still works.
    procedure AssignedEmptyClientBaseAddress(): Text
    var
        source: HttpClient;
        target: HttpClient;
    begin
        target := source;
        exit(target.GetBaseAddress());
    end;

    // SelfAssign — assigning a var to itself must not throw and state is preserved.
    procedure SelfAssign(url: Text): Text
    var
        client: HttpClient;
    begin
        client.SetBaseAddress(url);
        client := client;
        exit(client.GetBaseAddress());
    end;

    // ConfigureServerCertificateValidation — exact trigger pattern from issue #1447:
    //   procedure ConfigureServerCertificateValidation(var Client: HttpClient)
    procedure ConfigureServerCertificateValidation(var Client: HttpClient)
    begin
        Client.UseServerCertificateValidation(true);
    end;

    // AssignAndCallByVar — calls ConfigureServerCertificateValidation with an assigned client.
    procedure AssignAndCallByVar(url: Text): Boolean
    var
        source: HttpClient;
        target: HttpClient;
    begin
        source.SetBaseAddress(url);
        target := source;
        ConfigureServerCertificateValidation(target);
        exit(true);
    end;
}
