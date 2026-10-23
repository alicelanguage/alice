using Alice.Compiler;
using Alice.Cli.Projects;

namespace Alice.Tests.Compiler;

/// <summary>`[package] naming = "dotnet"`: an Alice library whose public API follows .NET naming for C# and F# callers.</summary>
[Collection("console")]
public class NamingTests
{
    const string Lib = """
        namespace Geo
          record Point(x : Float64, y : Float64)
          class Bag
            getter item_count : Int32 = 0
            def add_item(n : Int32)
              @item_count += n
            end
            def empty? : Bool
              @item_count == 0
            end
            def has_items? : Bool
              !empty?
            end
            private def secret_helper : Int32
              1
            end
          end
          module Geometry
            def self.total_area(ps : Array(Point)) : Float64
              ps.sum { |p| p.x * p.y }
            end
          end
        end
        """;

    [Theory]
    [InlineData("total_area", "TotalArea")]
    [InlineData("x", "X")]
    [InlineData("empty?", "IsEmpty")]
    [InlineData("has_items?", "HasItems")]
    [InlineData("save!", "Save")]
    [InlineData("to_utf8_bytes", "ToUtf8Bytes")]
    [InlineData("Dispose", "Dispose")]
    [InlineData("GetEnumerator", "GetEnumerator")]
    public void Dotnet_spelling(string alice, string dotnet) => Assert.Equal(dotnet, Alice.Runtime.Names.Dotnet(alice));

    [Fact]
    public void Public_api_is_pascal_case_for_csharp()
    {
        var cs = AliceCompiler.TranspileDotnetNames(Lib);
        Assert.Contains("record Point(double X, double Y)", cs);
        Assert.Contains("TotalArea(", cs);
        Assert.Contains("ItemCount", cs);
        Assert.Contains("bool IsEmpty(", cs);
        Assert.Contains("bool HasItems(", cs);
        Assert.Contains("secret_helper", cs);   // private members keep their Alice spelling
        // a plain C# caller written against .NET conventions compiles
        const string caller = """
            public static class Caller
            {
                public static string Use()
                {
                    var b = new Geo.Bag();
                    b.AddItem(2);
                    return $"{Geo.Geometry.TotalArea([new Geo.Point(2, 3)])} {b.ItemCount} {b.IsEmpty()} {b.HasItems()}";
                }
            }
            """;
        var asm = AliceCompiler.Load(AliceCompiler.Emit([cs, caller], "naming", exe: false));
        Assert.Equal("6 2 False True", asm.GetType("Caller")!.GetMethod("Use")!.Invoke(null, null));
    }

    [Fact]
    public void Alice_code_keeps_alice_names()
    {
        var src = Lib + "\nb = Geo::Bag.new\nb.add_item(3)\nputs b.item_count\nputs b.empty?\nputs Geo::Geometry.total_area([Geo::Point.new(1.0, 4.0)])\nputs b.send(\"has_items?\")\n";
        var cs = AliceCompiler.TranspileDotnetNames(src);
        var asm = AliceCompiler.Load(AliceCompiler.Emit([cs], "naming2", exe: true));
        var sw = new StringWriter();
        var old = Console.Out;
        Console.SetOut(sw);
        try { asm.EntryPoint!.Invoke(null, [Array.Empty<string>()]); }
        finally { Console.SetOut(old); }
        Assert.Equal("3\nfalse\n4.0\ntrue", sw.ToString().Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Manifest_naming_reaches_msbuild_and_is_validated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-naming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"n\"\nkind = \"lib\"\nnaming = \"dotnet\"\n");
        Assert.Contains("<AliceNaming>dotnet</AliceNaming>", File.ReadAllText(Manifest.Sync(dir)));
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"n\"\nnaming = \"camel\"\n");
        Assert.Contains("alice, dotnet", Assert.Throws<InvalidOperationException>(() => Manifest.Sync(dir)).Message);
    }
}
