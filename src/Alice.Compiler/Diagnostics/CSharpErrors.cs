using System.Globalization;
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

    public static (string Code, string Message) Translate(Microsoft.CodeAnalysis.Diagnostic d) =>
        Translate(d.Id, d.GetMessage(CultureInfo.InvariantCulture), () => Callee(d));

    /// <summary>A csc diagnostic from build output (code and English message): package builds report errors through
    /// MSBuild's compile, not a second in-process one. The callee name is not known there.</summary>
    public static (string Code, string Message) Translate(string id, string message) => Translate(id, message, () => "");

    static Alice.Core.Text.MatchData? Rx(string pattern, string text) => Alice.Core.Text.Regex.Cached(pattern).Match(text);

    static (string Code, string Message) Translate(string id, string en, Func<string> callee)
    {
        Alice.Core.Text.MatchData? m;
        switch (id)
        {
            case "CS1061" or "CS0117" when (m = Rx(@"\A'(?<t>.+?)' does not contain a definition for '(?<n>[^']+)'", en)) != null:
                return ("AL0302", $"undefined method '{M(m!["n"]!)}' for {T(m!["t"]!)}");
            case "CS0019" when (m = Rx(@"Operator '(?<o>[^']+)' cannot be applied to operands of type '(?<a>.+?)' and '(?<b>.+?)'", en)) != null:
                return ("AL0301", $"'{m!["o"]!}' is not defined for {T(m!["a"]!)} and {T(m!["b"]!)}");
            case "CS0023" when (m = Rx(@"Operator '(?<o>[^']+)' cannot be applied to operand of type '(?<a>.+?)'", en)) != null:
                return ("AL0301", $"'{m!["o"]!}' is not defined for {T(m!["a"]!)}");
            case var _ when en.Contains("unsafe context"):   // CS0214, CS0227, CS9123...: every pointer-in-safe-code error
                return ("AL0216", "pointer operation outside `unsafe`: wrap it in `unsafe ... end` or declare the method `unsafe def`");
            case "CS0103" when (m = Rx(@"The name '(?<n>[^']+)' does not exist", en)) != null:
                return ("AL0303", $"undefined name '{M(m!["n"]!)}'");
            case "CS0029" or "CS0266" when (m = Rx(@"convert type '(?<a>.+?)' to '(?<b>.+?)'", en)) != null:
                return ("AL0304", $"type mismatch: expected {T(m!["b"]!)}, got {T(m!["a"]!)}" +
                    (id == "CS0266" ? $" (convert explicitly, e.g. .to_i / .to_f / .as({T(m!["b"]!)}))" : ""));
            case "CS1503" when (m = Rx(@"Argument (?<i>\d+): cannot convert from '(?<a>.+?)' to '(?<b>.+?)'", en)) != null:
                return ("AL0305", $"argument {m!["i"]!}{callee()}: expected {T(m!["b"]!)}, got {T(m!["a"]!)}");
            case "CS1501" when (m = Rx(@"No overload for method '(?<n>[^']+)' takes (?<k>\d+) arguments", en)) != null:
                return ("AL0306", $"wrong number of arguments for {M(m!["n"]!)} (given {m!["k"]!})");
            case "CS7036":
                return ("AL0306", $"wrong number of arguments{callee()} (missing a required argument)");
            case "CS0246" when (m = Rx(@"name '(?<n>[^']+)' could not be found", en)) != null:
                return ("AL0307", $"unknown type '{T(m!["n"]!)}' (missing `require`, `using` or a package?)");
            case "CS0161":
                return ("AL0308", "not every path returns a value; add a final value or `return`");
            case "CS0165" when (m = Rx(@"variable '(?<n>[^']+)'", en)) != null:
                return ("AL0309", $"'{M(m!["n"]!)}' may be used before it is assigned");
            case "CS8602" or "CS8600" or "CS8601" or "CS8603" or "CS8604" or "CS8625" or "CS8618" or "CS8629":
                return ("AL0310", "value may be nil here; check it (`if x`, `x.nil?`, `x || default`) or assert with `x.not_nil!`");
            case "CS0121":
                return ("AL0311", "ambiguous call: " + AliceNames.Quoted(en[(en.IndexOf(':') + 1)..].Trim()));
            case "CS0411" when (m = Rx(@"method '(?<n>[^'(<]+)", en)) != null:
                return ("AL0312", $"cannot infer type arguments for '{M(m!["n"]!.Split('.')[^1])}'; give them explicitly: name<T>(...)");
            case "CS0122" when (m = Rx(@"\A'(?<n>[^']+)' is inaccessible", en)) != null:
                return ("AL0313", $"'{M(m!["n"]!.Split('.')[^1])}' is private here");
            case "CS0200" or "CS0191" or "CS0198" when (m = Rx(@"'(?<n>[^']+)'", en)) != null:
                return ("AL0314", $"'{M(m!["n"]!.Split('.')[^1])}' is read-only (declare it with `property` to allow assignment)");
            case "CS0120" when (m = Rx(@"'(?<n>[^']+)'", en)) != null:
                return ("AL0315", $"'{M(m!["n"]!.Split('.')[^1].Split('(')[0])}' needs an instance (it is not a class method)");
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
