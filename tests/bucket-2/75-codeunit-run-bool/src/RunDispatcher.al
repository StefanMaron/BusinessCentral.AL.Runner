codeunit 50752 "Run Dispatcher"
{
    /// <summary>
    /// Runs a codeunit by ID and returns true on success, false on error.
    /// This reproduces the pattern: if Codeunit.Run(CU_ID) then ...
    /// which the BC compiler emits as NavCodeunit.RunCodeunit(DataError.TrapError, id)
    /// and requires the call to return bool.
    /// </summary>
    procedure TryRunCodeunit(CUId: Integer): Boolean
    begin
        if Codeunit.Run(CUId) then
            exit(true)
        else
            exit(false);
    end;
}
