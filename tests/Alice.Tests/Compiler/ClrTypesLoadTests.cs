using Alice.Compiler.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Alice.Tests.Compiler;

/// <summary>
/// Type lookups over referenced assemblies never crash `alice gen` on an assembly the runtime cannot load. On the
/// windows-2025 runner the web API sample died with BadImageFormatException ("Cannot load a reference assembly for
/// execution", Microsoft.Extensions.Logging.Abstractions) inside ClrTypes.Search: Assembly.GetType followed a type
/// forward, and next to the loaded facade the loader found only a reference assembly.
/// </summary>
public class ClrTypesLoadTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-clrload-" + Guid.NewGuid().ToString("N"));

    public ClrTypesLoadTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        // assemblies loaded from here stay locked until the process exits on Windows
        try { Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static IEnumerable<MetadataReference> Framework() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

    static byte[] Compile(string name, string source, IEnumerable<MetadataReference> refs, bool referenceAssembly)
    {
        var comp = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], Framework().Concat(refs),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var r = comp.Emit(ms, options: new EmitOptions(metadataOnly: referenceAssembly));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return ms.ToArray();
    }

    [Fact]
    public void A_type_forward_into_a_reference_assembly_is_not_found_instead_of_a_crash()
    {
        // RefOnly.dll is a reference assembly ([ReferenceAssembly]: the runtime refuses to run it); Facade.dll forwards
        // RefOnly.Thing to it and sits in the same directory, as a facade sits next to its siblings in a reference pack
        string tag = Guid.NewGuid().ToString("N")[..8];   // fresh names: an earlier load in this process cannot answer
        var refOnly = Compile("RefOnly" + tag, $"[assembly: System.Runtime.CompilerServices.ReferenceAssembly]\nnamespace RefOnly{tag} {{ public class Thing {{ }} }}", [], referenceAssembly: true);
        File.WriteAllBytes(Path.Combine(dir, $"RefOnly{tag}.dll"), refOnly);
        var facade = Compile("Facade" + tag, $"[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(RefOnly{tag}.Thing))]",
            [MetadataReference.CreateFromImage(refOnly)], referenceAssembly: false);
        File.WriteAllBytes(Path.Combine(dir, $"Facade{tag}.dll"), facade);

        var clr = new ClrTypes([Path.Combine(dir, $"Facade{tag}.dll")]);
        Assert.Null(clr.Find($"RefOnly{tag}.Thing", 0, []));      // threw BadImageFormatException before
        Assert.NotNull(clr.Find("System.String", 0, []));          // the other assemblies still answer
    }

    [Fact]
    public void A_framework_reference_pack_resolves_to_the_installed_shared_framework()
    {
        // dotnet/shared/Microsoft.NETCore.App/<v>/System.Private.CoreLib.dll -> dotnet/
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        var refPack = Path.Combine(root, "packs", "Microsoft.AspNetCore.App.Ref");
        Assert.True(Directory.Exists(refPack), $"no ASP.NET Core reference pack under {root}");
        var reference = Directory.GetFiles(refPack, "Microsoft.AspNetCore.Http.Abstractions.dll", SearchOption.AllDirectories)
            .First(p => p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}"));

        var impl = ClrTypes.ImplementationOf(reference);
        Assert.StartsWith(Path.Combine(root, "shared", "Microsoft.AspNetCore.App"), impl);
        Assert.True(File.Exists(impl), impl);
        // the reference assembly itself cannot be loaded; through the redirect its types are found
        Assert.NotNull(new ClrTypes([reference]).Find("Microsoft.AspNetCore.Http.HttpContext", 0, []));
    }

    [Fact]
    public void The_core_reference_pack_is_left_to_the_running_runtime()
    {
        var core = Path.Combine("dotnet", "packs", "Microsoft.NETCore.App.Ref", "10.0.0", "ref", "net10.0", "System.Runtime.dll");
        Assert.Equal(core, ClrTypes.ImplementationOf(core));
    }
}
