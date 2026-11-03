namespace Alice.Fuzz;

/// <summary>
/// Mutations of real programs (corpus files under examples/, benchmarks/, samples/) for the parser fuzzer: line edits
/// (delete, duplicate, swap, truncate the file), token splices (keywords, brackets, quotes, interpolation openers,
/// heredoc and percent-literal openers) and character edits. Most results are invalid programs: the oracle is that the
/// parser rejects them with an AL diagnostic, never with an internal exception or a hang.
/// </summary>
public static class Mutator
{
    static readonly string[] Tokens =
    [
        "end", "do", "then", "else", "elsif", "rescue", "ensure", "begin", "def", "class", "case", "when", "in", "if",
        "(", ")", "[", "]", "{", "}", "|", "\"", "'", "#{", "}", "<<~EOS", "<<-X", "<<", "%w[", "%q(", "%Q{", "%(", "/",
        "\\", ",", ".", "::", "->", "&", "*", "**", "?", ":", "=>", "=", "==", "..", "...", "@", "@@", "$", "_1", "it",
        "yield", "return", "break", "next", "retry", "raise", "nil", "self", "super", "own", "borrow", "par", "pure",
        "affine", "\n", " ", "\t", "0x", "1_", "1e", "'\\u{", "\"\\u{", "\\x", ";", "#",
    ];

    public static string Mutate(string src, Rng r)
    {
        int rounds = r.Range(1, 4);
        for (int i = 0; i < rounds; i++) src = Once(src, r);
        return src;
    }

    static string Once(string src, Rng r)
    {
        var lines = src.Split('\n').ToList();
        if (lines.Count == 0) return src;
        int li = r.Next(lines.Count);
        switch (r.Next(10))
        {
            case 0: lines.RemoveAt(li); break;
            case 1: lines.Insert(li, lines[r.Next(lines.Count)]); break;
            case 2: { int lj = r.Next(lines.Count); (lines[li], lines[lj]) = (lines[lj], lines[li]); break; }
            case 3: return string.Join('\n', lines.Take(li));
            case 4: return src[..r.Next(src.Length + 1)];
            case 5 or 6:
                {
                    var l = lines[li];
                    int at = r.Next(l.Length + 1);
                    lines[li] = l[..at] + r.Pick(Tokens) + l[at..];
                    break;
                }
            case 7:
                {
                    var l = lines[li];
                    if (l.Length == 0) break;
                    int at = r.Next(l.Length);
                    int len = Math.Min(l.Length - at, r.Range(1, 4));
                    lines[li] = l[..at] + l[(at + len)..];
                    break;
                }
            case 8:
                {
                    // replace an identifier-ish word with a token
                    var words = lines[li].Split(' ');
                    if (words.Length == 0) break;
                    words[r.Next(words.Length)] = r.Pick(Tokens);
                    lines[li] = string.Join(' ', words);
                    break;
                }
            default:
                {
                    // re-indent a line (the formatter must not care)
                    lines[li] = new string(' ', r.Next(9)) + lines[li].TrimStart();
                    break;
                }
        }
        return string.Join('\n', lines);
    }

    /// <summary>Corpus: every .al file under the given roots, sorted (deterministic).</summary>
    public static List<(string Path, string Text)> Corpus(string root, params string[] dirs) =>
        dirs.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.al", SearchOption.AllDirectories))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Order(StringComparer.Ordinal)
            .Select(p => (p, File.ReadAllText(p))).ToList();
}
