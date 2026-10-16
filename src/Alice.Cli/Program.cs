using System.Diagnostics;
using Alice.Cli;
using Alice.Compiler;

return Cli.Execute(args);

namespace Alice.Cli
{
    /// <summary>Thin composition root: argument parsing + file IO around Alice.Compiler.</summary>
    public static class Cli
    {
        const string Usage = """
            alice <command> [options] <files.al...>

              run <files> [-- args]       compile in memory and run
              build <files> [-o out.dll]  emit an IL assembly (+runtimeconfig) runnable with `dotnet`
                    [--native]            publish a NativeAOT executable instead
                    [-r ref.dll]...       reference extra .NET assemblies (NuGet, C# libs)
              emit <files>                print the lowered C# (debugging)
              bootstrap [selfhost-dir]    self-host: stage0 -> stage1 -> stage2, check fixed point
            """;

        public static int Execute(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
            try
            {
                return args[0] switch
                {
                    "run" => Run(args[1..]),
                    "build" => Build(args[1..]),
                    "emit" => Emit(args[1..]),
                    "bootstrap" => Bootstrap.Run(args.Length > 1 ? args[1] : "selfhost"),
                    _ => Fail(Usage),
                };
            }
            catch (CompileException e) { Console.Error.WriteLine(e.Message); return 1; }
        }

        static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

        static List<SourceFile> Read(IEnumerable<string> paths) => paths.Select(p => new SourceFile(p, File.ReadAllText(p))).ToList();

        static (List<string> files, string? output, List<string> refs, bool native, string[] rest) Parse(string[] a)
        {
            var files = new List<string>(); var refs = new List<string>(); string? output = null; bool native = false;
            int i = 0;
            for (; i < a.Length; i++)
            {
                if (a[i] == "--") { i++; break; }
                if (a[i] == "-o") output = a[++i];
                else if (a[i] == "-r") refs.Add(a[++i]);
                else if (a[i] == "--native") native = true;
                else files.Add(a[i]);
            }
            if (files.Count == 0) throw new CompileException("no input files");
            return (files, output, refs, native, a[i..]);
        }

        static int Emit(string[] a)
        {
            var (files, _, _, _, _) = Parse(a);
            foreach (var cs in AliceCompiler.TranspileAll(Read(files))) Console.WriteLine(cs);
            return 0;
        }

        static int Run(string[] a)
        {
            var (files, _, refs, _, rest) = Parse(a);
            var cs = AliceCompiler.TranspileAll(Read(files));
            var asm = AliceCompiler.Load(AliceCompiler.Emit(cs, Path.GetFileNameWithoutExtension(files[0]), exe: true, refs));
            var entry = asm.EntryPoint ?? throw new CompileException("no top-level statements to run");
            entry.Invoke(null, [rest]);
            return 0;
        }

        static int Build(string[] a)
        {
            var (files, output, refs, native, _) = Parse(a);
            var name = Path.GetFileNameWithoutExtension(output ?? files[0]);
            var dir = Path.GetDirectoryName(Path.GetFullPath(output ?? files[0] + ".dll"))!;
            var cs = AliceCompiler.TranspileAll(Read(files));
            Directory.CreateDirectory(dir);
            return native ? Native(cs, name, dir, refs) : Managed(cs, name, dir, refs);
        }

        static int Managed(List<string> cs, string name, string dir, List<string> refs)
        {
            bool exe = cs.Any(c => c.Contains("public static void Main(string[] ARGV)"));
            var dll = Path.Combine(dir, name + ".dll");
            File.WriteAllBytes(dll, AliceCompiler.Emit(cs, name, exe, refs));
            var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
            File.Copy(rt, Path.Combine(dir, Path.GetFileName(rt)), true);
            var so = Path.Combine(Path.GetDirectoryName(rt)!, "libalice_native.so");
            if (File.Exists(so)) File.Copy(so, Path.Combine(dir, "libalice_native.so"), true);
            foreach (var r in refs) File.Copy(r, Path.Combine(dir, Path.GetFileName(r)), true);
            if (exe)
                File.WriteAllText(Path.Combine(dir, name + ".runtimeconfig.json"),
                    "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + Environment.Version.Major + ".0.0\"}}}");
            Console.WriteLine(dll);
            return 0;
        }

        /// <summary>NativeAOT: the lowered C# goes through a generated project so MSBuild/ILCompiler do the native link.</summary>
        static int Native(List<string> cs, string name, string dir, List<string> refs)
        {
            var work = Path.Combine(dir, "obj", "alice-native", name);
            Directory.CreateDirectory(work);
            for (int i = 0; i < cs.Count; i++) File.WriteAllText(Path.Combine(work, $"{name}.{i}.g.cs"), cs[i]);
            var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
            var refItems = string.Join("\n", refs.Append(rt).Select(r => $"<Reference Include=\"{Path.GetFullPath(r)}\" />"));
            File.WriteAllText(Path.Combine(work, name + ".csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net{{Environment.Version.Major}}.0</TargetFramework>
                    <AssemblyName>{{name}}</AssemblyName>
                    <PublishAot>true</PublishAot>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <Nullable>enable</Nullable>
                    <LangVersion>preview</LangVersion>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <OptimizationPreference>Speed</OptimizationPreference>
                    <IlcInstructionSet>native</IlcInstructionSet>
                    <WarningsAsErrors>CS8600;CS8601;CS8602;CS8603;CS8604;CS8625</WarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>{{refItems}}</ItemGroup>
                </Project>
                """);
            var psi = new ProcessStartInfo("dotnet", ["publish", work, "-c", "Release", "-o", dir, "--nologo", "-v", "q"]);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            var so = Path.Combine(Path.GetDirectoryName(rt)!, "libalice_native.so");
            if (File.Exists(so)) File.Copy(so, Path.Combine(dir, "libalice_native.so"), true);
            return proc.ExitCode;
        }
    }
}
