using System.Text;

namespace Alice.Cli.Repl;

/// <summary>
/// Julia-style line editor: live syntax highlighting, bracket matching, grey history hints (→ accepts),
/// prefix history search on ↑/↓, Ctrl-R reverse search, word motions (Alt/Ctrl ←/→, Alt-B/F), Ctrl-W/K/U kills,
/// Tab completion with a column menu. Ctrl-C clears, Ctrl-D on an empty line exits, Ctrl-L clears the screen.
/// Backspace on an empty line leaves a mode; mode keys on an empty line enter one.
/// </summary>
public sealed class LineEditor(Func<string, List<string>> complete)
{
    public readonly List<string> History = new();

    /// <summary>Highlight the buffer as Alice code (off for shell/pkg/help modes).</summary>
    public bool Highlight { get; set; } = true;

    public enum Signal { Line, Eof, LeaveMode, ModeKey }

    // ---- pure helpers (unit-tested) ----

    /// <summary>Rest of the newest history entry starting with `buf` (fish-style ghost text).</summary>
    public static string? HistoryHint(IReadOnlyList<string> history, string buf)
    {
        if (buf.Length == 0) return null;
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Length > buf.Length && history[i].StartsWith(buf, StringComparison.Ordinal) && !history[i].Contains('\n'))
                return history[i][buf.Length..];
        return null;
    }

    /// <summary>Index of the previous entry before `from` that starts with (or contains) `query`; -1 if none.</summary>
    public static int SearchBack(IReadOnlyList<string> history, string query, int from, bool contains = false)
    {
        for (int i = Math.Min(from, history.Count) - 1; i >= 0; i--)
            if (contains ? history[i].Contains(query, StringComparison.Ordinal) : history[i].StartsWith(query, StringComparison.Ordinal)) return i;
        return -1;
    }

    static int SearchForward(IReadOnlyList<string> history, string query, int from)
    {
        for (int i = from + 1; i < history.Count; i++)
            if (history[i].StartsWith(query, StringComparison.Ordinal)) return i;
        return history.Count;
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static int WordLeft(string s, int cur)
    {
        while (cur > 0 && !IsWord(s[cur - 1])) cur--;
        while (cur > 0 && IsWord(s[cur - 1])) cur--;
        return cur;
    }

    public static int WordRight(string s, int cur)
    {
        while (cur < s.Length && !IsWord(s[cur])) cur++;
        while (cur < s.Length && IsWord(s[cur])) cur++;
        return cur;
    }

    /// <summary>Lays completion candidates out in columns no wider than `width`.</summary>
    public static List<string> Columns(IReadOnlyList<string> items, int width)
    {
        if (items.Count == 0) return new();
        int colW = Math.Min(Math.Max(1, width), items.Max(i => i.Length) + 2);
        int cols = Math.Max(1, width / colW);
        int rows = (items.Count + cols - 1) / cols;
        var lines = new List<string>();
        for (int r = 0; r < rows; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < cols; c++)
            {
                int idx = c * rows + r;
                if (idx >= items.Count) break;
                string cell = items[idx].Length > width ? items[idx][..width] : items[idx];
                sb.Append(c + 1 < cols && (c + 1) * rows + r < items.Count ? cell.PadRight(colW) : cell);
            }
            lines.Add(sb.ToString().TrimEnd());
        }
        return lines;
    }

    // ---- interactive editing ----

    int rowsAbove;   // terminal rows between the prompt's first row and the cursor, for redraws of wrapped lines

    static int Width => Console.IsOutputRedirected ? 80 : Math.Max(20, Console.WindowWidth);

    /// <summary>Reads one line. modeKeys: characters that switch mode when typed on an empty line.</summary>
    public (Signal Signal, string Text) Read(string prompt, int promptWidth, string modeKeys, string initial = "")
    {
        var buf = new StringBuilder(initial);
        int cur = buf.Length, hist = History.Count;
        string saved = "", prefix = "";
        rowsAbove = 0;
        Console.Write(prompt);
        if (initial.Length > 0) Draw(prompt, promptWidth, initial, cur, hint: false);
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            bool ctrl = k.Modifiers.HasFlag(ConsoleModifiers.Control), alt = k.Modifiers.HasFlag(ConsoleModifiers.Alt);
            if (k.Key != ConsoleKey.UpArrow && k.Key != ConsoleKey.DownArrow) prefix = buf.ToString();
            if (k.Key == ConsoleKey.Enter)
            {
                Draw(prompt, promptWidth, buf.ToString(), buf.Length, hint: false);
                Console.WriteLine();
                return (Signal.Line, buf.ToString());
            }
            if (ctrl && k.Key == ConsoleKey.D)
            {
                if (buf.Length == 0) { Console.WriteLine(); return (Signal.Eof, ""); }
                if (cur < buf.Length) buf.Remove(cur, 1);
            }
            else if (ctrl && k.Key == ConsoleKey.C)
            {
                Draw(prompt, promptWidth, buf.ToString(), buf.Length, hint: false);
                Console.WriteLine("\u001b[90m^C\u001b[0m");
                buf.Clear(); cur = 0; hist = History.Count; rowsAbove = 0;
                Console.Write(prompt);
                continue;
            }
            else if (ctrl && k.Key == ConsoleKey.L) { Console.Write("\u001b[2J\u001b[H"); rowsAbove = 0; Console.Write(prompt); }
            else if (ctrl && k.Key == ConsoleKey.R)
            {
                var found = ReverseSearch(prompt, promptWidth);
                if (found != null) { buf.Clear().Append(found); cur = buf.Length; }
                rowsAbove = 0;
                Console.Write("\r\u001b[J" + prompt);
            }
            else if (buf.Length == 0 && modeKeys.Contains(k.KeyChar) && k.KeyChar != '\0') return (Signal.ModeKey, k.KeyChar.ToString());
            else switch (k.Key)
            {
                case ConsoleKey.Backspace:
                    if (buf.Length == 0 || initial.Length > 0 && buf.ToString().Trim().Length == 0 && cur == 0) return (Signal.LeaveMode, "");
                    if (ctrl || alt) { int w = WordLeft(buf.ToString(), cur); buf.Remove(w, cur - w); cur = w; }
                    else if (cur > 0)
                    {
                        // remove a whole indent step when only spaces precede the cursor
                        int n = cur >= 2 && buf.ToString(0, cur).Trim().Length == 0 && buf[cur - 1] == ' ' && buf[cur - 2] == ' ' ? 2 : 1;
                        buf.Remove(cur - n, n); cur -= n;
                    }
                    break;
                case ConsoleKey.Delete: if (cur < buf.Length) buf.Remove(cur, 1); break;
                case ConsoleKey.LeftArrow: cur = ctrl || alt ? WordLeft(buf.ToString(), cur) : Math.Max(0, cur - 1); break;
                case ConsoleKey.RightArrow:
                    if (cur == buf.Length && HistoryHint(History, buf.ToString()) is { } hint)
                    {
                        string take = ctrl || alt ? hint[..Math.Max(1, WordRight(hint, 0))] : hint;
                        buf.Append(take); cur = buf.Length;
                    }
                    else cur = ctrl || alt ? WordRight(buf.ToString(), cur) : Math.Min(buf.Length, cur + 1);
                    break;
                case ConsoleKey.Home: cur = 0; break;
                case ConsoleKey.End:
                    if (cur == buf.Length && HistoryHint(History, buf.ToString()) is { } h2) buf.Append(h2);
                    cur = buf.Length;
                    break;
                case ConsoleKey.A when ctrl: cur = 0; break;
                case ConsoleKey.E when ctrl:
                    if (cur == buf.Length && HistoryHint(History, buf.ToString()) is { } h3) buf.Append(h3);
                    cur = buf.Length;
                    break;
                case ConsoleKey.B when ctrl: cur = Math.Max(0, cur - 1); break;
                case ConsoleKey.F when ctrl: cur = Math.Min(buf.Length, cur + 1); break;
                case ConsoleKey.B when alt: cur = WordLeft(buf.ToString(), cur); break;
                case ConsoleKey.F when alt: cur = WordRight(buf.ToString(), cur); break;
                case ConsoleKey.U when ctrl: buf.Remove(0, cur); cur = 0; break;
                case ConsoleKey.K when ctrl: buf.Remove(cur, buf.Length - cur); break;
                case ConsoleKey.W when ctrl: { int w = WordLeft(buf.ToString(), cur); buf.Remove(w, cur - w); cur = w; break; }
                case ConsoleKey.D when alt: { int w = WordRight(buf.ToString(), cur); buf.Remove(cur, w - cur); break; }
                case ConsoleKey.UpArrow:
                    {
                        // Julia: ↑ searches history entries that start with what is already typed
                        int i = SearchBack(History, prefix, hist);
                        if (i >= 0)
                        {
                            if (hist == History.Count) saved = buf.ToString();
                            hist = i;
                            buf.Clear().Append(History[i]); cur = buf.Length;
                        }
                        break;
                    }
                case ConsoleKey.DownArrow:
                    if (hist < History.Count)
                    {
                        hist = SearchForward(History, prefix, hist);
                        buf.Clear().Append(hist == History.Count ? saved : History[hist]); cur = buf.Length;
                    }
                    break;
                case ConsoleKey.Tab:
                    {
                        int start = cur;
                        while (start > 0 && (IsWord(buf[start - 1]) || buf[start - 1] is '.' or '?' or '!' or ':' or '@')) start--;
                        string word = buf.ToString(start, cur - start);
                        if (word.Trim().Length == 0) { buf.Insert(cur, "  "); cur += 2; break; }
                        var opts = complete(word);
                        if (opts.Count == 1) { buf.Remove(start, cur - start).Insert(start, opts[0]); cur = start + opts[0].Length; }
                        else if (opts.Count > 1)
                        {
                            string common = opts.Aggregate((a, b) => new string(a.TakeWhile((c, i) => i < b.Length && b[i] == c).ToArray()));
                            if (common.Length > word.Length) { buf.Remove(start, cur - start).Insert(start, common); cur = start + common.Length; }
                            else Menu(prompt, promptWidth, buf.ToString(), opts);
                        }
                        break;
                    }
                default:
                    if (!char.IsControl(k.KeyChar))
                    {
                        buf.Insert(cur, k.KeyChar); cur++;
                        // dedent a closing keyword typed on an indented continuation line
                        if (k.KeyChar is 'd' or 'e' or 'f' or 'n' && buf.ToString() is var line && line.TrimStart() is "end" or "else" or "elsif" or "rescue" or "ensure" or "when" or "in" && line.StartsWith("  "))
                        { buf.Remove(0, 2); cur -= 2; }
                    }
                    break;
            }
            Draw(prompt, promptWidth, buf.ToString(), cur, hint: true);
        }
    }

    /// <summary>Redraws prompt + buffer (wrapped across rows), with the cursor at `cur`.</summary>
    void Draw(string prompt, int promptWidth, string text, int cur, bool hint)
    {
        int width = Width;
        var sb = new StringBuilder();
        if (rowsAbove > 0) sb.Append($"\u001b[{rowsAbove}A");
        sb.Append('\r').Append("\u001b[J").Append(prompt);
        int mark = -1;
        if (Highlight)
        {
            int at = cur < text.Length && "()[]{}".Contains(text[cur]) ? cur : cur > 0 && cur - 1 < text.Length && "()[]{}".Contains(text[cur - 1]) ? cur - 1 : -1;
            if (at >= 0) mark = Highlighter.MatchingBracket(text, at);
        }
        sb.Append(Highlight ? Highlighter.Highlight(text, mark) : text);
        string ghost = hint && cur == text.Length ? HistoryHint(History, text) ?? "" : "";
        if (ghost.Length > 0) sb.Append("\u001b[90m").Append(ghost).Append("\u001b[0m");
        int total = promptWidth + text.Length + ghost.Length;
        if (total > 0 && total % width == 0) sb.Append(" \r");          // force the deferred wrap so row math holds
        int endRow = total / width;
        int curRow = (promptWidth + cur) / width, curCol = (promptWidth + cur) % width;
        if (endRow > curRow) sb.Append($"\u001b[{endRow - curRow}A");
        sb.Append('\r');
        if (curCol > 0) sb.Append($"\u001b[{curCol}C");
        rowsAbove = curRow;
        Console.Write(sb.ToString());
    }

    void Menu(string prompt, int promptWidth, string text, List<string> opts)
    {
        Draw(prompt, promptWidth, text, text.Length, hint: false);
        Console.WriteLine();
        var shown = opts.Take(120).ToList();
        foreach (var row in Columns(shown, Width - 1)) Console.WriteLine("\u001b[36m" + row + "\u001b[0m");
        if (opts.Count > shown.Count) Console.WriteLine($"\u001b[90m… {opts.Count - shown.Count} more\u001b[0m");
        rowsAbove = 0;
        Console.Write(prompt);
    }

    /// <summary>Ctrl-R incremental reverse search; Enter/→ accepts, Esc/Ctrl-G cancels, Ctrl-R again finds older.</summary>
    string? ReverseSearch(string prompt, int promptWidth)
    {
        var q = new StringBuilder();
        int at = History.Count;
        string? match = null;
        while (true)
        {
            int idx = q.Length == 0 ? -1 : SearchBack(History, q.ToString(), at, contains: true);
            if (idx >= 0) match = History[idx];
            Console.Write("\r\u001b[J\u001b[90m(reverse-search)`\u001b[0m" + q + "\u001b[90m': \u001b[0m" + (match != null ? Highlighter.Highlight(match.Replace('\n', ' ')) : ""));
            var k = Console.ReadKey(intercept: true);
            if (k.Key is ConsoleKey.Enter or ConsoleKey.RightArrow or ConsoleKey.Tab) return match;
            if (k.Key == ConsoleKey.Escape || k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key is ConsoleKey.G or ConsoleKey.C) return null;
            if (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key == ConsoleKey.R) { if (idx >= 0) at = idx; continue; }
            if (k.Key == ConsoleKey.Backspace) { if (q.Length > 0) q.Length--; at = History.Count; continue; }
            if (!char.IsControl(k.KeyChar)) { q.Append(k.KeyChar); at = History.Count; }
        }
    }
}
