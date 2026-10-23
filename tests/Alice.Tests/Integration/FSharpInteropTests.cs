using System.Diagnostics;

namespace Alice.Tests.Integration;

/// <summary>An F# project consumes an Alice library built with naming = "dotnet" (ADR 0019).</summary>
[Trait("Category", "Integration")]
[Collection("msbuild")]
public class FSharpInteropTests
{
    static string Dotnet(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, o);
        return o;
    }

    [Fact]
    public void Fsharp_calls_alice_with_dotnet_names_and_matches_its_sum_types()
    {
        var root = Path.Combine(Path.GetTempPath(), "alice-fs-" + Guid.NewGuid().ToString("N"));
        var lib = Path.Combine(root, "geo");
        Directory.CreateDirectory(Path.Combine(lib, "src"));
        File.WriteAllText(Path.Combine(lib, "alice.toml"), "[package]\nname = \"geo\"\nkind = \"lib\"\nnaming = \"dotnet\"\n");
        File.WriteAllText(Path.Combine(lib, "src", "lib.al"), """
            namespace Geo
              record Point(x : Float64, y : Float64)
              enum Shape
                Circle(r : Float64)
                Rect(w : Float64, h : Float64)
              end
              module Geometry
                def self.distance(a : Point, b : Point) : Float64
                  Math.Sqrt((a.x - b.x) ** 2 + (a.y - b.y) ** 2)
                end
                def self.empty?(shapes : Array(Shape)) : Bool
                  shapes.size == 0
                end
              end
            end
            """);
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", lib]));
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.fsproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../geo/.alice/geo.alproj" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(app, "Program.fs"), """
            open Geo
            let shapes : Shape[] = [| Shape.Circle(1.0); Shape.Rect(2.0, 3.0) |]
            let describe (s : Shape) =
                match s with
                | :? Shape.Circle as c -> sprintf "circle %g" c.R
                | :? Shape.Rect as r -> sprintf "rect %gx%g" r.W r.H
                | _ -> "?"
            printfn "distance=%g empty=%b" (Geometry.Distance(Point(0.0, 0.0), Point(3.0, 4.0))) (Geometry.IsEmpty shapes)
            shapes |> Array.iter (describe >> printfn "%s")
            """);
        var output = Dotnet(app, "run");
        Assert.Contains("distance=5 empty=false", output);
        Assert.Contains("circle 1", output);
        Assert.Contains("rect 2x3", output);
    }
}
