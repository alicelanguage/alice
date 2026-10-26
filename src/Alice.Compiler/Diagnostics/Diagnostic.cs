using System.Text.RegularExpressions;

namespace Alice.Compiler;

/// <summary>A positioned compiler message. Alice checks use AL01xx/AL02xx; errors from the generated C# are translated to AL03xx.</summary>
public sealed record Diagnostic(string Severity, string Code, string File, int Line, int Col, string Message, int Length = 1)
{
    public override string ToString() => Code.StartsWith("CS") || Code.StartsWith("AL03")
        ? $"{File}({Line},{Col}): {Severity} {Code}: {Message}"
        : $"{File}:{Line}:{Col}: {Message}";

    static readonly Regex Positioned = new(@"^(?<file>.*?):(?<line>\d+)(?::(?<col>\d+))?: (?<msg>.*)$", RegexOptions.Singleline);

    /// <summary>Stable codes for Alice's own checks (documented in docs/errors.md).</summary>
    static readonly (string Needle, string Code)[] Codes =
    [
        ("conflicting borrow", "AL0217"),
        ("stack value", "AL0218"),
        ("sent to another thread", "AL0219"),
        ("non-exhaustive", "AL0201"),
        ("data race", "AL0202"),
        ("immutable", "AL0203"),
        ("cannot infer", "AL0204"),
        ("type required", "AL0205"),
        ("needs a type", "AL0205"),
        ("yield needs", "AL0206"),
        ("owned value", "AL0207"),
        ("use after move", "AL0208"),
        ("cannot parallelize", "AL0209"),
        ("is not pure", "AL0210"),
        ("cannot fork", "AL0211"),
        ("is not safe", "AL0212"),
        ("borrowed value", "AL0213"),
        ("rule domain", "AL0214"),
        ("expected", "AL0101"),
        ("unexpected", "AL0102"),
        ("unterminated", "AL0103"),
    ];

    public static Diagnostic FromMessage(string message)
    {
        var m = Positioned.Match(message);
        string code = Codes.FirstOrDefault(c => message.Contains(c.Needle)).Code ?? "AL0001";
        if (!m.Success) return new Diagnostic("error", code, "", 0, 0, message);
        int col = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 1;
        return new Diagnostic("error", code, m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), col, m.Groups["msg"].Value);
    }
}

public sealed class CompileException : Exception
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public CompileException(string message) : base(message) => Diagnostics = [Diagnostic.FromMessage(message)];

    public CompileException(IReadOnlyList<Diagnostic> diagnostics)
        : base(string.Join("\n", diagnostics.Select(d => d.ToString()))) => Diagnostics = diagnostics;
}
