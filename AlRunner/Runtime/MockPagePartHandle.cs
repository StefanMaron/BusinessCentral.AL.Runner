using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Returned by <c>GetPart(partHash)</c> on generated page classes.
///
/// BC emits <c>CurrPage.SubPart.Page.SomeProcedure()</c> as:
/// <code>
///   CurrPage.GetPart(partHash).CreateNavFormHandle(scope).Invoke(methodHash, args)
/// </code>
///
/// This handle bridges <c>GetPart</c> to the <c>CreateNavFormHandle → Invoke</c> chain
/// without needing to know the subpage's page ID at the time GetPart is called.
/// </summary>
public class MockPagePartHandle
{
    private readonly int _partHash;
    private MockPartFormHandle? _cachedHandle;

    public MockPagePartHandle(int partHash)
    {
        _partHash = partHash;
    }

    /// <summary>
    /// BC calls this to obtain a form handle from the part.
    /// Returns a cached <see cref="MockPartFormHandle"/> so that state written
    /// in one BC statement (e.g. <c>CurrPage.SubPart.Page.Caption := X</c>)
    /// is visible when the same part handle is read in the next statement
    /// (e.g. <c>exit(CurrPage.SubPart.Page.Caption)</c>).
    /// The <paramref name="scope"/> argument (the calling scope object) is unused
    /// in standalone mode — no real page lifecycle runs.
    /// Issue #1440.
    /// </summary>
    public MockPartFormHandle CreateNavFormHandle(object? scope)
        => _cachedHandle ??= new MockPartFormHandle(_partHash);
}

/// <summary>
/// Form handle returned by <see cref="MockPagePartHandle.CreateNavFormHandle"/>.
/// Dispatches method calls to the subpage class by searching all Page* types
/// in the compiled assembly for a scope class whose name encodes the method hash.
///
/// This mirrors <see cref="MockFormHandle.Invoke"/> and
/// <see cref="MockCodeunitHandle.Invoke"/> dispatch strategy.
/// </summary>
public class MockPartFormHandle
{
    private readonly int _partHash;

    public MockPartFormHandle(int partHash)
    {
        _partHash = partHash;
    }

    /// <summary>
    /// Close — no-op in standalone mode.
    /// BC generates <c>CurrPage.SubPart.Page.Close()</c> as
    /// <c>CurrPage.GetPart(hash).CreateNavFormHandle(scope).Close()</c>.
    /// Issue #1325.
    /// </summary>
    public void Close() { }

    /// <summary>
    /// GetRecord — no-op in standalone mode.
    /// BC generates <c>CurrPage.SubPart.Page.GetRecord(rec)</c> as
    /// <c>CurrPage.GetPart(hash).CreateNavFormHandle(scope).GetRecord(rec.Target)</c>.
    /// Issue #1325.
    /// </summary>
    public void GetRecord(MockRecordHandle rec) { }

    /// <summary>
    /// SetTableView — no-op in standalone mode.
    /// BC generates <c>CurrPage.SubPart.Page.SetTableView(rec)</c> as
    /// <c>CurrPage.GetPart(hash).CreateNavFormHandle(scope).SetTableView(rec.Target)</c>.
    /// Issue #1186.
    /// </summary>
    public void SetTableView(MockRecordHandle rec) { }

    /// <summary>
    /// Update — no-op in standalone mode.
    /// BC generates <c>CurrPage.SubPart.Page.Update()</c> or <c>Update(bool)</c> as
    /// <c>CurrPage.GetPart(hash).CreateNavFormHandle(scope).Update(...)</c>.
    /// Issue #1186.
    /// </summary>
    public void Update(bool saveRecord = true) { }

    /// <summary>
    /// Stub property. BC emits <c>CurrPage.SubPart.Page.Caption</c> get/set as
    /// <c>CurrPage.GetPart(hash).CreateNavFormHandle(scope).PageCaption</c>.
    /// Issue #1440.
    /// </summary>
    public string PageCaption { get; set; } = "";

    /// <summary>
    /// Invokes a procedure on the subpage identified by its method hash.
    /// Searches all <c>Page{N}</c> types in the compiled assembly for a
    /// nested scope class whose name contains <paramref name="memberId"/>,
    /// creates an instance of the page class, and invokes the corresponding
    /// public method.
    ///
    /// Returns <c>null</c> (default return) when the page type or method
    /// cannot be found — making subpage procedure calls no-ops in
    /// scenarios where the subpage is not compiled into the test bucket.
    /// </summary>
    public object? Invoke(int memberId, object[] args)
    {
        var absMemberId = Math.Abs(memberId).ToString();
        var memberIdStr = memberId.ToString();

        // Search all Page* classes for the method whose scope class name encodes memberId.
        foreach (var t in MockRecordHandle.GetAllAssemblies().SelectMany(a => a.GetTypes()))
        {
            if (!t.Name.StartsWith("Page", StringComparison.Ordinal)) continue;

            foreach (var nested in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!nested.Name.Contains($"_Scope_{memberIdStr}") &&
                    !nested.Name.Contains($"_Scope__{absMemberId}"))
                    continue;

                // Found the page type that owns this method — dispatch to it.
                var scopeIdx = nested.Name.IndexOf("_Scope_");
                if (scopeIdx < 0) continue;
                var methodName = nested.Name.Substring(0, scopeIdx);

                // Create a page instance using the default constructor so that
                // auto-property initialisers (e.g. Rec = new MockRecordHandle(N))
                // are correctly set. GetUninitializedObject skips these initialisers
                // and leaves Rec null, causing NullReferenceException on first field access.
                var pageInstance = Activator.CreateInstance(t);

                // Find the method by base name (handles overloads the same way
                // MockCodeunitHandle and MockFormHandle do).
                var suffixedName = $"{methodName}_{absMemberId}";
                var method = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == suffixedName);
                if (method == null)
                {
                    var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == methodName)
                        .ToArray();
                    method = candidates.Length == 1
                        ? candidates[0]
                        : candidates.FirstOrDefault(m => m.GetParameters().Length == args.Length);
                }
                if (method == null) continue;

                var parameters = method.GetParameters();
                var convertedArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length && i < args.Length; i++)
                    convertedArgs[i] = args[i];

                return method.Invoke(pageInstance, convertedArgs);
            }
        }

        // Method not found in any page class — return null (integer default, etc.).
        return null;
    }
}
