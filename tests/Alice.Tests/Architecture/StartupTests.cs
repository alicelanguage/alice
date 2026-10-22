using System.Reflection;
using Alice.Runtime;

namespace Alice.Tests.Architecture;

/// <summary>
/// Every Alice program touches Prelude on its first line (Prelude.init), which runs all of Prelude's static
/// initializers. Anything expensive there (caches, regexes) is paid by every program at startup: it must live
/// in a nested holder class that is initialized on first use.
/// </summary>
public class StartupTests
{
    static readonly Type[] Cheap = [typeof(System.Globalization.CultureInfo), typeof(string), typeof(string[])];

    [Fact]
    public void Prelude_has_no_eager_static_caches()
    {
        var eager = typeof(Prelude).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsLiteral && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && !Cheap.Contains(f.FieldType) && !f.FieldType.IsValueType)
            .Select(f => $"{f.Name} : {f.FieldType.Name}")
            .ToList();
        Assert.Empty(eager);
    }
}
