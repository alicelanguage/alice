// THROWAWAY SPIKE (backend plan D1.1): see README.md in this folder. Not production code.
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Alice.Compiler.Backend.IlSpike;

/// <summary>
/// Hidden CLI: `alice emit --il-spike ...`
///   &lt;file.al&gt; [-o dir] [--bounds m1,m2] [--tail] [--stack] [--stack-dirty]  writes dir/&lt;name&gt;.il.dll (+ .cs.dll reference)
///   --census &lt;file-or-dir&gt;...                                              node-kind / blocker tables
///   --bench &lt;dll&gt; &lt;reps&gt; [args...]                                        in-process steady state (entry point reps times)
/// </summary>
public static class SpikeCli
{
    public static int Run(List<string> a)
    {
        if (a.Remove("--census")) return Census(a);
        if (a.Remove("--bench")) return Bench(a);
        if (a.Remove("--il-dump")) return Dump(a[0], a[1]);
        var opt = new SpikeOptions();
        string? outDir = null;
        bool noHot = false;
        var forceInline = new List<string>();
        var files = new List<string>();
        for (int i = 0; i < a.Count; i++)
            switch (a[i])
            {
                case "-o": outDir = a[++i]; break;
                case "--bounds": opt.NoBounds = a[++i].Split(',').ToHashSet(); break;
                case "--tail": opt.TailCalls = true; break;
                case "--tail-all": opt.TailAll = true; break;
                case "--no-hot": noHot = true; break;
                case "--no-stack-schedule": opt.NoStackSchedule = true; break;
                case "--force-inline": forceInline.AddRange(a[++i].Split(',')); break;   // experiment: AggressiveInlining on these (IL side only)
                case "--stack": opt.StackArrays = true; break;
                case "--stack-dirty": opt.StackArrays = true; opt.StackZero = false; break;
                default: files.Add(a[i]); break;
            }
        if (files.Count != 1) { Console.Error.WriteLine("usage: alice emit --il-spike <file.al> [-o dir] [--bounds m,..] [--tail] [--stack]"); return 2; }
        var srcs = AliceCompiler.LoadWithRequires(files);
        string name = Path.GetFileNameWithoutExtension(files[0]);
        outDir ??= Path.GetDirectoryName(Path.GetFullPath(files[0]))!;
        Directory.CreateDirectory(outDir);

        // the C# backend in the same mode (sequential), as oracle and timing reference
        var sw = Stopwatch.StartNew();
        var cs = AliceCompiler.TranspileFull(srcs, null, "alice_native", sequential: true).CSharp;
        var csImage = AliceCompiler.Emit(cs, name + ".cs", exe: true);
        long csMs = sw.ElapsedMilliseconds;
        // the C# backend's [MethodImpl] flags (`inline def`, kernels, loop leaves...) are decided in Emitter text: copy them
        foreach (Match m in Regex.Matches(string.Join("\n", cs), @"MethodImplOptions\.(\w+)\)\]\n(?:#line[^\n]*\n)*(?:public|private|internal)[^\n(]*?\s(\w+)\("))
            (m.Groups[1].Value == "AggressiveInlining" ? opt.Inline : m.Groups[1].Value == "NoInlining" ? opt.NoInline : opt.Hot).Add(m.Groups[2].Value);
        opt.Inline.UnionWith(forceInline);
        if (noHot) opt.Hot.Clear();   // experiment: no AggressiveOptimization on the IL side
        Write(outDir, name + ".cs", csImage);

        sw.Restart();
        var res = IlSpikeCompiler.Compile(srcs, name + ".il", opt);
        long ilMs = sw.ElapsedMilliseconds;
        if (res.Image == null)
        {
            Console.Error.WriteLine("il-spike: unsupported\n" + res.Error);
            return 1;
        }
        Write(outDir, name + ".il", res.Image);
        Console.WriteLine($"il-spike: {Path.Combine(outDir, name + ".il.dll")} ({res.Image.Length} B, {ilMs} ms) and {name}.cs.dll ({csImage.Length} B, {csMs} ms); hot: {string.Join(",", opt.Hot.Order())}; inline: {string.Join(",", opt.Inline.Order())}"
            + (res.Corrupted.Count > 0 ? $"; long branches after corrupt short-branch IL in: {string.Join(",", res.Corrupted.Order())}" : ""));
        return 0;
    }

    static void Write(string dir, string asm, byte[] image)
    {
        File.WriteAllBytes(Path.Combine(dir, asm + ".dll"), image);
        File.WriteAllText(Path.Combine(dir, asm + ".runtimeconfig.json"),
            $"{{\"runtimeOptions\":{{\"tfm\":\"net{Environment.Version.Major}.0\",\"framework\":{{\"name\":\"Microsoft.NETCore.App\",\"version\":\"{Environment.Version.Major}.0.0\"}}}}}}");
        var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
        var dst = Path.Combine(dir, Path.GetFileName(rt));
        if (!File.Exists(dst) || File.GetLastWriteTimeUtc(dst) < File.GetLastWriteTimeUtc(rt)) File.Copy(rt, dst, true);
    }

    static int Census(List<string> paths)
    {
        bool seq = paths.Remove("--sequential");
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.al", SearchOption.TopDirectoryOnly).Order().ToList() : new List<string> { p }).ToList();
        var nodes = new Dictionary<string, int>();
        var blockers = new Dictionary<string, int>();
        var details = new Dictionary<string, int>();
        var programs = new List<string>();
        int methods = 0, ok = 0;
        foreach (var f in files)
        {
            string status;
            try
            {
                var res = IlSpikeCompiler.Compile(AliceCompiler.LoadWithRequires([f]), "census", new SpikeOptions(), censusOnly: true, sequential: seq);
                foreach (var (k, v) in res.Nodes) nodes[k] = nodes.GetValueOrDefault(k) + v;
                foreach (var (k, v) in res.Blockers) blockers[k] = blockers.GetValueOrDefault(k) + v;
                foreach (var (k, v) in res.Details) details[k] = details.GetValueOrDefault(k) + v;
                methods += res.Methods;
                ok += res.MethodsOk;
                status = res.Blockers.Count == 0 ? "OK" : string.Join(", ", res.Blockers.OrderByDescending(x => x.Value).Select(x => $"{x.Key} x{x.Value}"));
                status = $"{res.MethodsOk}/{res.Methods} bodies; " + status;
            }
            catch (Exception e) { status = "front end: " + e.GetType().Name + " " + e.Message.Split('\n')[0]; blockers["FrontEnd:" + e.GetType().Name] = blockers.GetValueOrDefault("FrontEnd:" + e.GetType().Name) + 1; }
            programs.Add($"| {Path.GetFileName(Path.GetDirectoryName(f))}/{Path.GetFileName(f)} | {status} |");
        }
        Console.WriteLine($"files {files.Count}, bodies {methods}, emitted {ok}\n\n| node kind | count |\n|---|---|");
        foreach (var (k, v) in nodes.OrderByDescending(x => x.Value)) Console.WriteLine($"| {k} | {v} |");
        Console.WriteLine("\n| blocker (first unsupported per body) | bodies |\n|---|---|");
        foreach (var (k, v) in blockers.OrderByDescending(x => x.Value)) Console.WriteLine($"| {k} | {v} |");
        Console.WriteLine("\n| blocker detail | bodies |\n|---|---|");
        foreach (var (k, v) in details.OrderByDescending(x => x.Value).Take(60)) Console.WriteLine($"| {k.Replace("|", "\\|")} | {v} |");
        Console.WriteLine("\n| program | status |\n|---|---|");
        foreach (var p in programs) Console.WriteLine(p);
        return 0;
    }

    /// <summary>Steady state: runs the entry point reps times in this process (stdout of all but the first run discarded).</summary>
    static int Bench(List<string> a)
    {
        var asm = Assembly.LoadFrom(Path.GetFullPath(a[0]));
        int reps = int.Parse(a[1]);
        var args = a.Skip(2).ToArray();
        var entry = asm.EntryPoint!;
        var times = new List<double>();
        var cpu = new List<double>();
        var old = Console.Out;
        var proc = Process.GetCurrentProcess();
        for (int i = 0; i < reps; i++)
        {
            proc.Refresh();
            var c0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            entry.Invoke(null, [args]);
            times.Add(sw.Elapsed.TotalMilliseconds);
            proc.Refresh();
            cpu.Add((proc.TotalProcessorTime - c0).TotalMilliseconds);
            if (i == 0) Console.SetOut(TextWriter.Null);
        }
        Console.SetOut(old);
        var tail = times.Skip(reps / 2).Order().ToList();
        var ctail = cpu.Skip(reps / 2).Order().ToList();
        Console.WriteLine($"first {times[0]:F1} ms; steady (last {tail.Count}) wall min {tail[0]:F1} median {tail[tail.Count / 2]:F1} ms; cpu min {ctail[0]:F1} median {ctail[ctail.Count / 2]:F1} ms");
        return 0;
    }

    static readonly Dictionary<short, System.Reflection.Emit.OpCode> Ops = typeof(System.Reflection.Emit.OpCodes).GetFields()
        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>Minimal IL listing of one method (opcodes + operand bytes) to compare the two backends' IL.</summary>
    static int Dump(string dll, string method)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            if (md.GetString(m.Name) != method || m.RelativeVirtualAddress == 0) continue;
            var body = System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(pe, m.RelativeVirtualAddress);
            var il = body.GetILBytes()!;
            Console.WriteLine($"{method}: IL {il.Length} B, maxstack {body.MaxStack}, locals init {body.LocalVariablesInitialized}");
            for (int i = 0; i < il.Length;)
            {
                int at = i;
                short v = il[i] == 0xFE ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
                i += il[i] == 0xFE ? 2 : 1;
                var op = Ops[v];
                int n = op.OperandType switch
                {
                    System.Reflection.Emit.OperandType.InlineNone => 0,
                    System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                    System.Reflection.Emit.OperandType.InlineVar => 2,
                    System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                    System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4,
                };
                string arg = n switch
                {
                    1 => op.OperandType == System.Reflection.Emit.OperandType.ShortInlineBrTarget ? $"IL_{i + 1 + (sbyte)il[i]:x4}" : ((sbyte)il[i]).ToString(),
                    2 => BitConverter.ToInt16(il, i).ToString(),
                    4 when op.OperandType == System.Reflection.Emit.OperandType.InlineBrTarget => $"IL_{i + 4 + BitConverter.ToInt32(il, i):x4}",
                    4 when op.OperandType == System.Reflection.Emit.OperandType.ShortInlineR => BitConverter.ToSingle(il, i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    4 when op.OperandType == System.Reflection.Emit.OperandType.InlineI => BitConverter.ToInt32(il, i).ToString(),
                    4 => $"tok {BitConverter.ToInt32(il, i):x8}",
                    8 => op.OperandType == System.Reflection.Emit.OperandType.InlineR ? BitConverter.ToDouble(il, i).ToString(System.Globalization.CultureInfo.InvariantCulture) : BitConverter.ToInt64(il, i).ToString(),
                    _ => "",
                };
                Console.WriteLine($"  IL_{at:x4}: {op.Name} {arg}");
                i += n;
            }
        }
        return 0;
    }
}
