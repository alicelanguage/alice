namespace Alice.Runtime;

/// <summary>
/// Alice member names in .NET convention (`[package] naming = "dotnet"`): what C# and F# consumers of an Alice
/// library see. The compiler and the reflection fallbacks (`send`, `respond_to?`) share this one rule.
/// </summary>
public static class Names
{
    /// <summary>`total_area` -> TotalArea, `x` -> X, `empty?` -> IsEmpty, `has_key?` -> HasKey, `save!` -> Save.
    /// Names that are not lower snake case (`Dispose`, `GetEnumerator`) are returned unchanged.</summary>
    public static string Dotnet(string name)
    {
        bool predicate = name.EndsWith('?');
        string core = name.TrimEnd('?', '!');
        if (core.Length == 0 || !char.IsLower(core[0]) || core.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))) return name;
        var words = core.Split('_', StringSplitOptions.RemoveEmptyEntries);
        string pascal = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        // a predicate reads as a property-like question: empty? -> IsEmpty; already-phrased ones keep their verb
        if (predicate && words[0] is not ("is" or "has" or "can" or "should" or "contains" or "starts" or "ends")) pascal = "Is" + pascal;
        return pascal;
    }
}
