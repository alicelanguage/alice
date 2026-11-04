using System.Diagnostics;

namespace Alice.Tests.Runtime;

/// <summary>
/// Runs compiled images in a fresh `dotnet` process that runs nothing else, for allocation tests that must read an
/// exact 0. Inside the test host other threads allocate all the time (about 1.7 KB/s with no test running, measured
/// on the forge: 168 B about ten times a second), so a process-wide counter there never reads 0, and a current-thread
/// counter can move when another thread's GC retires this thread's allocation context (<see cref="AllocationProbe"/>).
/// </summary>
static class ChildRun
{
    /// <summary>Writes <paramref name="exe"/> as NAME.dll (plus <paramref name="libs"/> and Alice.Runtime.dll) to a
    /// temporary directory, runs it with <paramref name="args"/> and returns its output; fails on a non-zero exit.</summary>
    public static string Run(byte[] exe, string name, IEnumerable<(string Name, byte[] Image)>? libs = null, params string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, name + ".dll"), exe);
            foreach (var (lib, image) in libs ?? []) File.WriteAllBytes(Path.Combine(dir, lib + ".dll"), image);
            var runtime = typeof(Alice.Runtime.Prelude).Assembly.Location;
            File.Copy(runtime, Path.Combine(dir, Path.GetFileName(runtime)));
            string major = Environment.Version.Major.ToString();
            File.WriteAllText(Path.Combine(dir, name + ".runtimeconfig.json"),
                """{"runtimeOptions":{"tfm":"netMAJOR.0","framework":{"name":"Microsoft.NETCore.App","version":"MAJOR.0.0"}}}""".Replace("MAJOR", major));
            var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir };
            psi.ArgumentList.Add(Path.Combine(dir, name + ".dll"));
            foreach (var a in args) psi.ArgumentList.Add(a);
            var (code, output) = Alice.Tests.Integration.ChildProcess.Run(psi, timeoutMinutes: 5);
            Assert.True(code == 0, output);
            return output.Replace("\r", "");
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A C# program (one file) compiled against the test host's references, Alice.Runtime included.</summary>
    public static byte[] CSharp(string source, string name) => Alice.Compiler.AliceCompiler.Emit([source], name, exe: true);
}
