using Alice.Compiler;

namespace Alice.Tests.Integration;

/// <summary>Every standalone program in examples/ compiles and prints what its README promises.</summary>
[Collection("console")]
public class ExampleTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static string Run(string rel, params string[] args)
    {
        var path = Path.Combine(Root(), "examples", rel);
        return AliceCompiler.Run(File.ReadAllText(path), path, args).Trim().ReplaceLineEndings("\n");
    }

    [Theory]
    [InlineData("basics/hello.al", "Hello, Alice!\nsimd dot = 30.0\nsquares = [1, 4, 9, 16, 25, 36, 49, 64, 81, 100]")]
    [InlineData("data/invoices.al", "paid invoices=3\nrevenue cents=4250\nlarge invoice cents=3000")]
    [InlineData("data/json_report.al", "{\"service\":\"alice\",\"healthy\":true,\"requests\":42}\nALICE: 42 requests")]
    [InlineData("data/trees.al", "depth 16, leaves sum 2147450880")]
    [InlineData("concurrency/channels.al", "sum of squares=385")]
    [InlineData("dotnet/library_api.al", "0,2,4,6,8,10\n{\"start\":2,\"end\":9}\n15\n3,6,9\nshape of area 9.0, grown 1\nlocked section")]
    public void Example_prints_its_documented_output(string file, string expected) => Assert.Equal(expected, Run(file));

    [Fact]
    public void Libc_interop_example()
    {
        if (!OperatingSystem.IsLinux()) return;   // the example links libc.so.6
        Assert.Equal("strlen(\"héllo\") = 6\nhas PATH: true\nclock ok: true\nsorted by C: 3, 7, 19, 25, 42\nembedded NUL rejected", Run("interop/libc.al"));
    }

    [Fact]
    public void Wand_tool_generates_files()
    {
        var output = Run("tools/wand/src/main.al", "model", "blog_post");
        Assert.StartsWith("# app/models/blog_post.al\nclass BlogPost", output);
        Assert.Contains("usage: wand", Run("tools/wand/src/main.al"));
    }
}
