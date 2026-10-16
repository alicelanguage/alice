using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

/// <summary>
/// Ruby/Crystal-flavoured standard library for Alice programs. Generated code does
/// <c>using static Alice.Runtime.Prelude;</c>, so these read as builtins / methods.
/// Names ending in <c>_q</c> are Alice <c>name?</c> predicates.
/// </summary>
public static partial class Prelude
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Command-line arguments, visible everywhere like Ruby's ARGV (Main sets it).</summary>
    public static string[] ARGV { get; set; } = [];

    /// <summary>Alice programs are culture-invariant (like Crystal/Ruby).</summary>
    public static void init()
    {
        CultureInfo.DefaultThreadCurrentCulture = Inv;
        CultureInfo.CurrentCulture = Inv;
    }
}
