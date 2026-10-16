using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Alice.Compiler.Diagnostics;

/// <summary>
/// Roslyn errors on generated code -> Alice diagnostics (AL03xx, Alice type names, Alice wording). The C# id is
/// kept out of the message; AL0300 covers anything not specifically mapped.
/// </summary>
public static class CSharpErrors
{
    static string T(string cs) => AliceNames.Type(cs);
    static string M(string cs) => AliceNames.Member(cs);

    public static (string Code, string Message) Translate(Microsoft.CodeAnalysis.Diagnostic d)
    {
        string en = d.GetMessage(CultureInfo.InvariantCulture);
        Match m;
        switch (d.Id)
        {
            case "CS1061" or "CS0117" when (m = Regex.Match(en, @"^'(?<t>.+?)' does not contain a definition for '(?<n>[^']+)'")).Success:
                return ("AL0302", $"undefined method '{M(m.Groups["n"].Value)}' for {T(m.Groups["t"].Value)}");
            case "CS0019" when (m = Regex.Match(en, @"Operator '(?<o>[^']+)' cannot be applied to operands of type '(?<a>.+?)' and '(?<b>.+?)'")).Success:
                return ("AL0301", $"'{m.Groups["o"].Value}' is not defined for {T(m.Groups["a"].Value)} and {T(m.Groups["b"].Value)}");
            case "CS0023" when (m = Regex.Match(en, @"Operator '(?<o>[^']+)' cannot be applied to operand of type '(?<a>.+?)'")).Success:
                return ("AL0301", $"'{m.Groups["o"].Value}' is not defined for {T(m.Groups["a"].Value)}");
            case "CS0103" when (m = Regex.Match(en, @"The name '(?<n>[^']+)' does not exist")).Success:
                return ("AL0303", $"undefined name '{M(m.Groups["n"].Value)}'");
            case "CS0029" or "CS0266" when (m = Regex.Match(en, @"convert type '(?<a>.+?)' to '(?<b>.+?)'")).Success:
                return ("AL0304", $"type mismatch: expected {T(m.Groups["b"].Value)}, got {T(m.Groups["a"].Value)}" +
                    (d.Id == "CS0266" ? $" (convert explicitly, e.g. .to_i / .to_f / .as({T(m.Groups["b"].Value)}))" : ""));
            case "CS1503" when (m = Regex.Match(en, @"Argument (?<i>\d+): cannot convert from '(?<a>.+?)' to '(?<b>.+?)'")).Success:
                return ("AL0305", $"argument {m.Groups["i"].Value}{Callee(d)}: expected {T(m.Groups["b"].Value)}, got {T(m.Groups["a"].Value)}");
            case "CS1501" when (m = Regex.Match(en, @"No overload for method '(?<n>[^']+)' takes (?<k>\d+) arguments")).Success:
                return ("AL0306", $"wrong number of arguments for {M(m.Groups["n"].Value)} (given {m.Groups["k"].Value})");
            case "CS7036":
                return ("AL0306", $"wrong number of arguments{Callee(d)} (missing a required argument)");
            case "CS0246" when (m = Regex.Match(en, @"name '(?<n>[^']+)' could not be found")).Success:
                return ("AL0307", $"unknown type '{T(m.Groups["n"].Value)}' (missing `require`, `using` or a package?)");
            case "CS0161":
                return ("AL0308", "not every path returns a value; add a final value or `return`");
            case "CS0165" when (m = Regex.Match(en, @"variable '(?<n>[^']+)'")).Success:
                return ("AL0309", $"'{M(m.Groups["n"].Value)}' may be used before it is assigned");
            case "CS8602" or "CS8600" or "CS8601" or "CS8603" or "CS8604" or "CS8625" or "CS8618" or "CS8629":
                return ("AL0310", "value may be nil here; check it (`if x`, `x.nil?`, `x || default`) or assert with `x.not_nil!`");
            case "CS0121":
                return ("AL0311", "ambiguous call: " + AliceNames.Quoted(en[(en.IndexOf(':') + 1)..].Trim()));
            case "CS0411" when (m = Regex.Match(en, @"method '(?<n>[^'(<]+)")).Success:
                return ("AL0312", $"cannot infer type arguments for '{M(m.Groups["n"].Value.Split('.')[^1])}'; give them explicitly: name<T>(...)");
            case "CS0122" when (m = Regex.Match(en, @"^'(?<n>[^']+)' is inaccessible")).Success:
                return ("AL0313", $"'{M(m.Groups["n"].Value.Split('.')[^1])}' is private here");
            case "CS0200" or "CS0191" or "CS0198" when (m = Regex.Match(en, @"'(?<n>[^']+)'")).Success:
                return ("AL0314", $"'{M(m.Groups["n"].Value.Split('.')[^1])}' is read-only (declare it with `property` to allow assignment)");
            case "CS0120" when (m = Regex.Match(en, @"'(?<n>[^']+)'")).Success:
                return ("AL0315", $"'{M(m.Groups["n"].Value.Split('.')[^1].Split('(')[0])}' needs an instance (it is not a class method)");
        }
        return ("AL0300", AliceNames.Quoted(en));
    }

    /// <summary>" of name" for the invocation enclosing the diagnostic, when there is one.</summary>
    static string Callee(Microsoft.CodeAnalysis.Diagnostic d)
    {
        if (d.Location.SourceTree is not { } tree) return "";
        var node = tree.GetRoot().FindNode(d.Location.SourceSpan);
        var call = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        string? name = call?.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            _ => null,
        };
        if (name == null && node.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault() is { } oc) name = oc.Type.ToString() + ".new";
        return name != null ? " of " + M(name) : "";
    }
}
