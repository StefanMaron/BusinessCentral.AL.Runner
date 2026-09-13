using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static class NavAppPackageDataPatches
{
    // Cecil patch target: static void ALNavApp.ALNavAppLoadPackageData(int tableId).
    // Outside an install or upgrade this is BC's own first line (return), so it is observably
    // equivalent. During one, BC imports the table's data from the app package; the runner has
    // no package retriever for that, and BC's body fails there with a bare format error, so
    // this refuses by name instead (#4061). Trap: the guard must stay BC's ALNavAppIsInstalling,
    // which reads the install context InstallExecutionContext sets (#4049).
    public static void ALNavApp_LoadPackageData(int tableId)
    {
        if (!Microsoft.Dynamics.Nav.Runtime.ALNavApp.ALNavAppIsInstalling())
            return;
        throw new RunnerOutOfScopeException(
            "NavApp.LoadPackageData",
            $"not-yet-implemented — importing the app package's table data for table {tableId} "
            + "during install is not supported yet (#4061)");
    }
}
