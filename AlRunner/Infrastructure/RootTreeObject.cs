// RootTreeObject — concrete ITreeObject used as the parent of the skeleton root scope.
// Its TreeHandler.hostObject must be non-null so TreeHandler.IsDisposed returns false.
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Infrastructure;

internal sealed class RootTreeObject : Microsoft.Dynamics.Nav.Runtime.ITreeObject
{
    private readonly RootHandler _h;
    public RootTreeObject() { _h = new RootHandler(this); }
    Microsoft.Dynamics.Nav.Runtime.TreeHandler Microsoft.Dynamics.Nav.Runtime.ITreeObject.Tree => _h;
    Microsoft.Dynamics.Nav.Runtime.TreeObjectType Microsoft.Dynamics.Nav.Runtime.ITreeObject.Type => default;
    bool Microsoft.Dynamics.Nav.Runtime.ITreeObject.SingleThreaded => false;
}

internal sealed class RootHandler : Microsoft.Dynamics.Nav.Runtime.TreeHandler
{
    private static readonly FieldInfo _fHost =
        BcShape.Field(
            typeof(Microsoft.Dynamics.Nav.Runtime.TreeHandler), "hostObject",
            BindingFlags.NonPublic | BindingFlags.Instance, "tree-object host binding");
    public RootHandler(Microsoft.Dynamics.Nav.Runtime.ITreeObject host) : base()
    {
        // IsDisposed = (hostObject == null) — flip it.
        _fHost.SetValue(this, host);
    }
}
