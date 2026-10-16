using System.Text;

namespace Alice.Cli.Repl;

/// <summary>
/// Minimal readline: insert/delete, ←/→, Home/End (Ctrl-A/E), ↑/↓ history, Tab completion, Ctrl-C clears,
/// Ctrl-D on an empty line exits, Ctrl-L clears the screen. Backspace on an empty line leaves a mode (Julia).
/// </summary>
public sealed class LineEditor(Func<string, List<string>> complete)
{
    public readonly List<string> History = new();

    public enum Signal { Line, Eof, LeaveMode, ModeKey }

    /// <summary>Reads one line. modeKeys: characters that switch mode when typed on an empty line.</summary>
    public (Signal Signal, string Text) Read(string prompt, int promptWidth, string modeKeys)
    {
        var buf = new StringBuilder();
        int cur = 0, hist = History.Count;
        string saved = "";
        Console.Write(prompt);
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return (Signal.Line, buf.ToString()); }
            if (k.Key == ConsoleKey.D && k.Modifiers.HasFlag(ConsoleModifiers.Control) && buf.Length == 0) { Console.WriteLine(); return (Signal.Eof, ""); }
            if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)) { Console.WriteLine("^C"); buf.Clear(); cur = 0; Console.Write(prompt); continue; }
            if (k.Key == ConsoleKey.L && k.Modifiers.HasFlag(ConsoleModifiers.Control)) { Console.Write("\x1b[2J\x1b[H"); Redraw(); continue; }
            if (buf.Length == 0 && modeKeys.Contains(k.KeyChar)) return (Signal.ModeKey, k.KeyChar.ToString());
            switch (k.Key)
            {
                case ConsoleKey.Backspace:
                    if (buf.Length == 0) return (Signal.LeaveMode, "");
                    if (cur > 0) { buf.Remove(cur - 1, 1); cur--; }
                    break;
                case ConsoleKey.Delete: if (cur < buf.Length) buf.Remove(cur, 1); break;
                case ConsoleKey.LeftArrow: if (cur > 0) cur--; break;
                case ConsoleKey.RightArrow: if (cur < buf.Length) cur++; break;
                case ConsoleKey.Home: cur = 0; break;
                case ConsoleKey.End: cur = buf.Length; break;
                case ConsoleKey.A when k.Modifiers.HasFlag(ConsoleModifiers.Control): cur = 0; break;
                case ConsoleKey.E when k.Modifiers.HasFlag(ConsoleModifiers.Control): cur = buf.Length; break;
                case ConsoleKey.U when k.Modifiers.HasFlag(ConsoleModifiers.Control): buf.Remove(0, cur); cur = 0; break;
                case ConsoleKey.UpArrow:
                    if (hist > 0)
                    {
                        if (hist == History.Count) saved = buf.ToString();
                        hist--;
                        Set(History[hist]);
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (hist < History.Count) { hist++; Set(hist == History.Count ? saved : History[hist]); }
                    break;
                case ConsoleKey.Tab:
                    {
                        int start = cur;
                        while (start > 0 && (char.IsLetterOrDigit(buf[start - 1]) || buf[start - 1] is '_' or '.' or '?' or '!')) start--;
                        string word = buf.ToString(start, cur - start);
                        if (word.Length == 0) { buf.Insert(cur, "  "); cur += 2; break; }
                        var opts = complete(word);
                        if (opts.Count == 1) { buf.Remove(start, cur - start).Insert(start, opts[0]); cur = start + opts[0].Length; }
                        else if (opts.Count > 1)
                        {
                            string common = opts.Aggregate((a, b) => new string(a.TakeWhile((c, i) => i < b.Length && b[i] == c).ToArray()));
                            if (common.Length > word.Length) { buf.Remove(start, cur - start).Insert(start, common); cur = start + common.Length; }
                            else
                            {
                                Console.WriteLine();
                                Console.WriteLine(string.Join("  ", opts.Take(60)) + (opts.Count > 60 ? $"  … (+{opts.Count - 60})" : ""));
                            }
                        }
                        break;
                    }
                default:
                    if (!char.IsControl(k.KeyChar)) { buf.Insert(cur, k.KeyChar); cur++; }
                    break;
            }
            Redraw();
        }

        void Set(string s) { buf.Clear().Append(s); cur = buf.Length; }

        void Redraw()
        {
            Console.Write("\r" + prompt + buf + "\x1b[K");
            int back = buf.Length - cur;
            if (back > 0) Console.Write($"\x1b[{back}D");
        }
    }
}
