using System.Globalization;

namespace Alice.Core.Time;

/// <summary>
/// Ruby `strftime` interpreted while writing into a caller span: 0 bytes allocated (month and day names are literals).
/// </summary>
/// <remarks>
/// Directives: <c>%Y %C %y %m %B %b %h %d %e %j %H %k %I %l %P %p %M %S %L %N %z %:z %::z %Z %A %a %u %w %G %g %V
/// %U %W %s %Q %n %t %% %c %D %F %T %R %r %x %X %v %+</c>. Flags <c>-</c> (no padding), <c>_</c> (spaces), <c>0</c>
/// (zeros), <c>^</c> (upper case), <c>#</c> (swap case) and a width; for <c>%L</c>/<c>%N</c> the width is the number
/// of fraction digits. English names, invariant digits; an unknown directive is copied as written.
/// </remarks>
public static class Strftime
{
    /// <summary>Formats <paramref name="t"/> into <paramref name="destination"/>; false when it does not fit.</summary>
    public static bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, Time t)
    {
        var w = new W(destination);
        Run(ref w, format, t.ToDateTimeOffset(), t.IsUtc);
        charsWritten = w.Overflow ? 0 : w.Pos;
        return !w.Overflow;
    }

    /// <summary>Formats a <see cref="DateTimeOffset"/> (UTC flag: offset 0 prints <c>%Z</c> as "UTC").</summary>
    public static bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, DateTimeOffset value)
    {
        var w = new W(destination);
        Run(ref w, format, value, value.Offset == TimeSpan.Zero);
        charsWritten = w.Overflow ? 0 : w.Pos;
        return !w.Overflow;
    }

    ref struct W(Span<char> d)
    {
        readonly Span<char> d = d;
        public int Pos;
        public bool Overflow;
        public void Put(char c) { if (Pos < d.Length) d[Pos++] = c; else Overflow = true; }
        public void Fill(char c, int n) { for (int i = 0; i < n; i++) Put(c); }
        public void Put(scoped ReadOnlySpan<char> s) { if (s.TryCopyTo(d[Pos..])) Pos += s.Length; else Overflow = true; }
        public Span<char> Written(int from) => d[from..Pos];
    }

    static ReadOnlySpan<char> MonthName(int m) => m switch
    {
        1 => "January", 2 => "February", 3 => "March", 4 => "April", 5 => "May", 6 => "June", 7 => "July",
        8 => "August", 9 => "September", 10 => "October", 11 => "November", _ => "December",
    };

    static ReadOnlySpan<char> DayName(int d) => d switch
    {
        0 => "Sunday", 1 => "Monday", 2 => "Tuesday", 3 => "Wednesday", 4 => "Thursday", 5 => "Friday", _ => "Saturday",
    };

    static void Run(ref W w, ReadOnlySpan<char> f, DateTimeOffset v, bool utc)
    {
        for (int i = 0; i < f.Length; i++)
        {
            char c = f[i];
            if (c != '%' || i + 1 >= f.Length) { w.Put(c); continue; }
            int j = i + 1;
            char pad = '\0';
            bool upper = false, swap = false;
            int colons = 0;
            for (; j < f.Length; j++)
            {
                char g = f[j];
                if (g == '-') pad = '-';
                else if (g == '_') pad = ' ';
                else if (g == '0') pad = '0';
                else if (g == '^') upper = true;
                else if (g == '#') swap = true;
                else break;
            }
            int width = -1;
            while (j < f.Length && char.IsAsciiDigit(f[j])) width = Math.Max(0, width) * 10 + (f[j++] - '0');
            while (j < f.Length && f[j] == ':') { colons++; j++; }
            if (j >= f.Length) { w.Put(f[i..]); break; }
            char d = f[j];
            int start = w.Pos;
            switch (d)
            {
                case 'Y': Num(ref w, v.Year, 4, '0', pad, width); break;
                case 'C': Num(ref w, v.Year / 100, 2, '0', pad, width); break;
                case 'y': Num(ref w, v.Year % 100, 2, '0', pad, width); break;
                case 'm': Num(ref w, v.Month, 2, '0', pad, width); break;
                case 'd': Num(ref w, v.Day, 2, '0', pad, width); break;
                case 'e': Num(ref w, v.Day, 2, ' ', pad, width); break;
                case 'j': Num(ref w, v.DayOfYear, 3, '0', pad, width); break;
                case 'H': Num(ref w, v.Hour, 2, '0', pad, width); break;
                case 'k': Num(ref w, v.Hour, 2, ' ', pad, width); break;
                case 'I': Num(ref w, Hour12(v.Hour), 2, '0', pad, width); break;
                case 'l': Num(ref w, Hour12(v.Hour), 2, ' ', pad, width); break;
                case 'M': Num(ref w, v.Minute, 2, '0', pad, width); break;
                case 'S': Num(ref w, v.Second, 2, '0', pad, width); break;
                case 'u': Num(ref w, v.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)v.DayOfWeek, 1, '0', pad, width); break;
                case 'w': Num(ref w, (int)v.DayOfWeek, 1, '0', pad, width); break;
                case 'G': Num(ref w, ISOWeek.GetYear(v.DateTime), 4, '0', pad, width); break;
                case 'g': Num(ref w, ISOWeek.GetYear(v.DateTime) % 100, 2, '0', pad, width); break;
                case 'V': Num(ref w, ISOWeek.GetWeekOfYear(v.DateTime), 2, '0', pad, width); break;
                case 'U': Num(ref w, (v.DayOfYear + 6 - (int)v.DayOfWeek) / 7, 2, '0', pad, width); break;
                case 'W': Num(ref w, (v.DayOfYear + 6 - ((int)v.DayOfWeek + 6) % 7) / 7, 2, '0', pad, width); break;
                case 's': Num(ref w, v.ToUnixTimeSeconds(), 1, '0', pad, width); break;
                case 'Q': Num(ref w, v.ToUnixTimeMilliseconds(), 1, '0', pad, width); break;
                case 'L': Fraction(ref w, v, width < 0 ? 3 : width); break;
                case 'N': Fraction(ref w, v, width < 0 ? 9 : width); break;
                case 'B': Name(ref w, MonthName(v.Month), pad, width); break;
                case 'b' or 'h': Name(ref w, MonthName(v.Month)[..3], pad, width); break;
                case 'A': Name(ref w, DayName((int)v.DayOfWeek), pad, width); break;
                case 'a': Name(ref w, DayName((int)v.DayOfWeek)[..3], pad, width); break;
                case 'p': Name(ref w, v.Hour < 12 ? "AM" : "PM", pad, width); break;
                case 'P': Name(ref w, v.Hour < 12 ? "am" : "pm", pad, width); break;
                case 'z': Offset(ref w, v.Offset, colons, pad, width); break;
                case 'Z': Name(ref w, utc ? "UTC" : "", pad, width); if (!utc) Offset(ref w, v.Offset, 1, '\0', -1); break;
                case 'n': w.Put('\n'); break;
                case 't': w.Put('\t'); break;
                case '%': w.Put('%'); break;
                case 'c': Run(ref w, "%a %b %e %H:%M:%S %Y", v, utc); break;
                case 'D' or 'x': Run(ref w, "%m/%d/%y", v, utc); break;
                case 'F': Run(ref w, "%Y-%m-%d", v, utc); break;
                case 'T' or 'X': Run(ref w, "%H:%M:%S", v, utc); break;
                case 'R': Run(ref w, "%H:%M", v, utc); break;
                case 'r': Run(ref w, "%I:%M:%S %p", v, utc); break;
                case 'v': Run(ref w, "%e-%^b-%4Y", v, utc); break;
                case '+': Run(ref w, "%a %b %e %H:%M:%S %Z %Y", v, utc); break;
                default: w.Put(f[i..(j + 1)]); break;
            }
            if ((upper || swap) && !w.Overflow)
            {
                var s = w.Written(start);
                // Ruby: `#` swaps case of names (upper-cases %a %b %A %B, lower-cases %p)
                bool toLower = swap && d is 'p';
                for (int k = 0; k < s.Length; k++) s[k] = toLower ? char.ToLowerInvariant(s[k]) : char.ToUpperInvariant(s[k]);
            }
            i = j;
        }
    }

    static int Hour12(int h) => h % 12 == 0 ? 12 : h % 12;

    static void Num(ref W w, long v, int defWidth, char defPad, char pad, int width)
    {
        Span<char> buf = stackalloc char[24];
        bool neg = v < 0;
        ulong mag = neg ? (ulong)(-(v + 1)) + 1 : (ulong)v;
        mag.TryFormat(buf, out int n, default, CultureInfo.InvariantCulture);
        int wd = width >= 0 ? width : defWidth;
        char p = pad == '\0' ? defPad : pad;
        int fill = pad == '-' ? 0 : wd - n - (neg ? 1 : 0);
        if (p == ' ') w.Fill(' ', fill);
        if (neg) w.Put('-');
        if (p == '0') w.Fill('0', fill);
        w.Put(buf[..n]);
    }

    static void Name(ref W w, scoped ReadOnlySpan<char> s, char pad, int width)
    {
        if (width > s.Length && pad != '-') w.Fill(pad == '0' ? '0' : ' ', width - s.Length);
        w.Put(s);
    }

    static void Fraction(ref W w, DateTimeOffset v, int digits)
    {
        long ns = v.Ticks % TimeSpan.TicksPerSecond * 100;
        Span<char> buf = stackalloc char[9];
        for (int k = 8; k >= 0; k--) { buf[k] = (char)('0' + ns % 10); ns /= 10; }
        if (digits <= 9) w.Put(buf[..digits]);
        else { w.Put(buf); w.Fill('0', digits - 9); }
    }

    static void Offset(ref W w, TimeSpan off, int colons, char pad, int width)
    {
        long secs = (long)off.TotalSeconds;
        char sign = secs < 0 ? '-' : '+';
        secs = Math.Abs(secs);
        int body = colons switch { 0 => 4, 1 => 5, _ => 8 };
        int fill = width > body + 1 && pad != '-' ? width - body - 1 : 0;
        if (pad == ' ' || pad == '_') w.Fill(' ', fill);
        w.Put(sign);
        if (pad is '\0' or '0') w.Fill('0', fill);
        Two(ref w, secs / 3600);
        if (colons >= 1) w.Put(':');
        Two(ref w, secs / 60 % 60);
        if (colons >= 2) { w.Put(':'); Two(ref w, secs % 60); }
    }

    static void Two(ref W w, long v) { w.Put((char)('0' + v / 10 % 10)); w.Put((char)('0' + v % 10)); }
}
