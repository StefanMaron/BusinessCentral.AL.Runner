namespace AlRunner.Runtime;

using System.Reflection;

/// <summary>
/// Thin wrapper around BC's <c>Microsoft.Dynamics.Nav.CodeAnalysis.IdSpace.GetMemberId(int, string)</c>.
/// BC uses this function to compute the stable hash for page actions, fields, and parts.
/// The hash is encoded in generated C# as the argument to <c>tP.GetAction(hash)</c>,
/// <c>tP.GetField(hash)</c>, and <c>tP.GetPart(hash)</c>.
/// </summary>
internal static class IdSpaceHelper
{
    private static Func<int, string, int>? _getMemberId;
    private static bool _attempted;

    /// <summary>
    /// Computes BC's stable member hash for a given page object ID and member name.
    /// Returns 0 if the BC CodeAnalysis DLL is not loaded.
    /// </summary>
    public static int GetMemberId(int pageId, string memberName)
    {
        if (!_attempted)
        {
            _getMemberId = Load();
            _attempted = true;
        }
        return _getMemberId?.Invoke(pageId, memberName) ?? 0;
    }

    private static Func<int, string, int>? Load()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.IdSpace");
            if (t == null) continue;
            var method = t.GetMethod(
                "GetMemberId",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(int), typeof(string)],
                modifiers: null);
            if (method == null) continue;
            return (id, name) => (int)method.Invoke(null, [id, name])!;
        }
        return null;
    }
}
