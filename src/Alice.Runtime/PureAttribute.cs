namespace Alice.Runtime;

/// <summary>
/// The runtime vouches that a static method has no observable side effects (no shared state, no I/O), so the
/// compiler's purity gate may parallelize loops and forks that call it. Only Alice.Runtime methods are trusted.
/// (System.Diagnostics.Contracts.PureAttribute is [Conditional("CONTRACTS_FULL")]: compilers strip it.)
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PureAttribute : Attribute;
