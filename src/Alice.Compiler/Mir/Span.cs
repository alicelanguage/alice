namespace Alice.Compiler.Mir;

/// <summary>A source range in an Alice file: 1-based line and column, length in characters (0: a point).</summary>
public readonly record struct Span(string File, int Line, int Col, int Len);
