namespace AlRunner.Infrastructure;

/// <summary>
/// A call into a codeunit the runner could not load (#3516), carrying the codeunit id and — when
/// the run found the app declaring it with no implementation — that app, so the console can
/// point at its Action needed entry instead of repeating the remedy (#4600). The message keeps
/// the full text for --output-json and JUnit.
/// </summary>
public sealed class MissingDependencyCodeunitException : InvalidOperationException
{
    public int CodeunitId { get; }

    /// <summary><c>Publisher/Name</c> of the app with no implementation, or null.</summary>
    public string? App { get; }

    public MissingDependencyCodeunitException(int codeunitId, string? app, string message)
        : base(message)
    {
        CodeunitId = codeunitId;
        App = app;
    }

    /// <summary>The exception for codeunit <paramref name="id"/>, attributed when it can be.</summary>
    internal static MissingDependencyCodeunitException For(int id, string prefix, string remedy)
    {
        var app = ProvisionGapLog.UnservableAppDeclaringCodeunit(id);
        var attribution = app == null
            ? ""
            : $"Codeunit {id} is declared by {app}, which this run resolved to a package with no "
              + "implementation (see Action needed). ";
        return new MissingDependencyCodeunitException(id, app, prefix + attribution + remedy);
    }

    /// <summary>
    /// The console line for a failure whose cause is an app listed in Action needed, or null
    /// when <paramref name="ex"/> is not one or its app has no entry in <paramref name="actionNeededApps"/>.
    /// </summary>
    internal static string? ConsolePointer(Exception? ex, Func<string, bool> actionNeededApps)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is MissingDependencyCodeunitException { App: { } app } m && actionNeededApps(app))
                return $"Codeunit {m.CodeunitId} is in {app}, which has no implementation in this run"
                    + $" — see Action needed: {app}";
        return null;
    }
}
