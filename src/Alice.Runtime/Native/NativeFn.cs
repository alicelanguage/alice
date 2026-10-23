namespace Alice.Runtime;

/// <summary>
/// The address of a C-callable function (an Alice callback lifted by the compiler, or one returned by C), as a safe
/// value: calling a `fun` that takes a `Callback(...)` needs no `unsafe`; only the generated wrapper casts it back.
/// </summary>
public readonly record struct NativeFn(nint Address);
