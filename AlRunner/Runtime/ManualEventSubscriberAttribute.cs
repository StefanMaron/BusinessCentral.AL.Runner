namespace AlRunner.Runtime;

/// <summary>
/// Marker attribute emitted by the rewriter on codeunit classes that have
/// <c>EventSubscriberInstance = Manual</c>. The original <c>NavCodeunitOptions</c>
/// attribute is stripped; this lightweight replacement lets the
/// <see cref="EventSubscriberRegistry"/> detect manual subscribers at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ManualEventSubscriberAttribute : Attribute { }
