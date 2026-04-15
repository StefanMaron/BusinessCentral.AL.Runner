codeunit 56280 "Missing CU Probe"
{
    procedure CallMissingUserCodeunit()
    begin
        // Call a user-range codeunit (50xxx) that doesn't exist in the assembly
        Codeunit.Run(59999);
    end;

    procedure CallMissingTestToolkitCodeunit()
    begin
        // Call a test-toolkit-range codeunit (130xxx) that doesn't exist
        Codeunit.Run(130512);
    end;

    procedure CallMissingSystemCodeunit()
    begin
        // Call a system-range codeunit (1-9999) that doesn't exist
        Codeunit.Run(9999);
    end;

    procedure CallExistingCodeunit()
    begin
        // Call a codeunit that does exist in the assembly (positive path)
        Codeunit.Run(56282);
    end;
}
