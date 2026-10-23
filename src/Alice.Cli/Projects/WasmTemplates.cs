namespace Alice.Cli.Projects;

/// <summary>`alice new --wasm` (ADR 0023): an Alice program and the static page that boots it.</summary>
public static class WasmTemplates
{
    public static string Source(string name) => $$"""
        # {{name}}: Alice in the browser (WebAssembly). `alice build` writes the site to dist/wwwroot.
        using System.Runtime.InteropServices.JavaScript

        module App
          # callable from JavaScript (wwwroot/main.js): app.App.greet("Alice")
          @[JSExport]
          def self.greet(who : String) : String
            "Hello, #{who}!"
          end
        end

        puts "{{name}} is running in WebAssembly"

        """;

    public static string Index(string name) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{name}}</title>
          <script type="module" src="./main.js"></script>
        </head>
        <body>
          <h1>{{name}}</h1>
          <p id="out">loading…</p>
        </body>
        </html>

        """;

    public const string MainJs = """
        // boots the .NET WebAssembly runtime, calls an exported Alice method, then runs the program's top level
        import { dotnet } from './_framework/dotnet.js';

        const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
        const app = await getAssemblyExports(getConfig().mainAssemblyName);
        document.getElementById('out').textContent = app.App.greet('browser');
        await runMain();

        """;

    /// <summary>`alice run` on a wasm package: the same bundle under Node, arguments passed through.</summary>
    public static string NodeRunner(string dotnetJs) => $$"""
        import { dotnet } from '{{new Uri(dotnetJs).AbsoluteUri}}';
        await dotnet.withApplicationArguments(...process.argv.slice(2)).run();

        """;
}
