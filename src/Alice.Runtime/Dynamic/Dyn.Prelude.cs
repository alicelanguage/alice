namespace Alice.Runtime;

public static partial class Prelude
{
    /// <summary>The runtime `define_method` body for <paramref name="name"/> on <paramref name="t"/> or a base (for <see cref="Dyn"/>).</summary>
    internal static Delegate? DefinedMethodFor(Type t, string name) => DefinedMethod(t, name);
}
