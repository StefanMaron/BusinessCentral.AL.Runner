codeunit 1320400 "HC UseDefaultNetwork Bool Src"
{
    /// <summary>
    /// Exercises UseDefaultNetworkWindowsAuthentication() in a boolean context.
    /// </summary>
    procedure UseDefaultNetworkInIf(): Boolean
    var
        Client: HttpClient;
    begin
        if not Client.UseDefaultNetworkWindowsAuthentication() then
            exit(false);
        exit(true);
    end;
}
