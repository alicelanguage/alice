using System.Globalization;

namespace Alice.Cli.Localization;

/// <summary>CLI text in English, Portuguese and Spanish. Picked from ALICE_LANG, --lang, or the OS UI culture.</summary>
public static class Strings
{
    static string lang = Detect();

    static string Detect()
    {
        var env = Environment.GetEnvironmentVariable("ALICE_LANG");
        var name = string.IsNullOrEmpty(env) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : env;
        return Normalize(name);
    }

    static string Normalize(string name)
    {
        name = name.ToLowerInvariant();
        if (name.StartsWith("pt")) return "pt";
        if (name.StartsWith("es")) return "es";
        return "en";
    }

    public static string Language => lang;

    public static void Use(string name)
    {
        lang = Normalize(name);
        var culture = lang switch { "pt" => "pt-BR", "es" => "es-ES", _ => "en-US" };
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        Environment.SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", culture);   // MSBuild/Roslyn child processes
    }

    public static string T(string key, params object[] args)
    {
        var table = lang switch { "pt" => Pt, "es" => Es, _ => En };
        var fmt = table.TryGetValue(key, out var v) ? v : En.TryGetValue(key, out var e) ? e : key;
        return args.Length == 0 ? fmt : string.Format(CultureInfo.InvariantCulture, fmt, args);
    }

    static readonly Dictionary<string, string> En = new()
    {
        ["tagline"] = "Alice {0} — Ruby ergonomics, Rust safety, C speed, on .NET",
        ["usage"] = "Usage",
        ["commands"] = "Commands",
        ["cmd.new"] = "Create a new project (--lib, --game)",
        ["cmd.build"] = "Compile a project or files to .NET IL (--release, --native)",
        ["cmd.run"] = "Build and run a project or a single .al file",
        ["cmd.check"] = "Type-check without producing output",
        ["cmd.test"] = "Run test_* functions in test/*.al",
        ["cmd.add"] = "Add a NuGet package to the project",
        ["cmd.clean"] = "Remove build outputs",
        ["cmd.emit"] = "Print the lowered C# (debugging the compiler)",
        ["cmd.sln"] = "Solutions: sln new <name.slnx> | sln add <sln|slnx> <project>",
        ["cmd.version"] = "Show version, runtime and SIMD capabilities",
        ["cmd.fmt"] = "Format .al files (--check for CI)",
        ["fmt.formatted"] = "Formatted",
        ["fmt.would"] = "Unformatted",
        ["fmt.summary"] = "{0} file(s), {1} changed",
        ["compiling"] = "Compiling",
        ["checking"] = "Checking",
        ["running"] = "Running",
        ["restoring"] = "Restoring packages",
        ["building"] = "Building",
        ["finished"] = "Finished",
        ["finished.detail"] = "{0} target(s) in {1}",
        ["created"] = "Created",
        ["created.next"] = "Next: cd {0} && alice run",
        ["adding"] = "Adding",
        ["added"] = "Added",
        ["cleaned"] = "Cleaned",
        ["error"] = "error",
        ["warning"] = "warning",
        ["help"] = "help",
        ["aborting"] = "could not compile due to {0} previous error(s)",
        ["no.project"] = "no .alproj found here; pass a file or project",
        ["no.files"] = "no input files",
        ["unknown.command"] = "unknown command '{0}'",
        ["exists"] = "destination '{0}' already exists",
        ["test.running"] = "running {0} test(s)",
        ["test.ok"] = "ok",
        ["test.failed"] = "FAILED",
        ["test.result"] = "test result: {0}. {1} passed; {2} failed; finished in {3}",
        ["hint.AL0201"] = "add the missing `when` branches or an `else`",
        ["hint.AL0202"] = "send the value through a Channel(T), use an Actor, or return it from the task",
        ["hint.AL0203"] = "declare it with plain assignment (`x = ...`) to make it mutable",
        ["hint.AL0204"] = "add a type: `x : Type? = nil`",
        ["hint.AL0205"] = "annotate the type: `name : Type`",
        ["hint.AL0206"] = "declare the block parameter: `def name(&block : Int32 -> Int32)`",
        ["hint.CS8602"] = "this value may be nil: check it with `if x.nil?` or assert with `x.not_nil!`",
        ["hint.CS0103"] = "not found: check the spelling or add `using Namespace`",
        ["hint.CS1061"] = "the type has no such member; .NET methods need parentheses, properties do not",
        ["hint.CS0246"] = "unknown type: add `using Namespace` or a package with `alice add`",
        ["hint.CS0029"] = "the types do not match; convert with `.to_i`, `.to_f`, `.to_s` or `.as(Type)`",
    };

    static readonly Dictionary<string, string> Pt = new()
    {
        ["tagline"] = "Alice {0} — ergonomia de Ruby, segurança de Rust, velocidade de C, no .NET",
        ["usage"] = "Uso",
        ["commands"] = "Comandos",
        ["cmd.new"] = "Cria um projeto novo (--lib, --game)",
        ["cmd.build"] = "Compila um projeto ou arquivos para IL .NET (--release, --native)",
        ["cmd.run"] = "Compila e executa um projeto ou um arquivo .al",
        ["cmd.check"] = "Verifica tipos sem gerar saída",
        ["cmd.test"] = "Executa as funções test_* em test/*.al",
        ["cmd.add"] = "Adiciona um pacote NuGet ao projeto",
        ["cmd.clean"] = "Remove os artefatos de build",
        ["cmd.emit"] = "Mostra o C# gerado (depuração do compilador)",
        ["cmd.sln"] = "Adiciona um .alproj a uma solution: sln add <sln> <proj>",
        ["cmd.version"] = "Mostra versão, runtime e capacidades SIMD",
        ["cmd.fmt"] = "Formata arquivos .al (--check para CI)",
        ["fmt.formatted"] = "Formatado",
        ["fmt.would"] = "Não formatado",
        ["fmt.summary"] = "{0} arquivo(s), {1} alterado(s)",
        ["compiling"] = "Compilando",
        ["checking"] = "Verificando",
        ["running"] = "Executando",
        ["restoring"] = "Restaurando pacotes",
        ["building"] = "Construindo",
        ["finished"] = "Concluído",
        ["finished.detail"] = "{0} alvo(s) em {1}",
        ["created"] = "Criado",
        ["created.next"] = "Próximo passo: cd {0} && alice run",
        ["adding"] = "Adicionando",
        ["added"] = "Adicionado",
        ["cleaned"] = "Limpo",
        ["error"] = "erro",
        ["warning"] = "aviso",
        ["help"] = "ajuda",
        ["aborting"] = "não foi possível compilar devido a {0} erro(s) anterior(es)",
        ["no.project"] = "nenhum .alproj encontrado aqui; informe um arquivo ou projeto",
        ["no.files"] = "nenhum arquivo de entrada",
        ["unknown.command"] = "comando desconhecido '{0}'",
        ["exists"] = "o destino '{0}' já existe",
        ["test.running"] = "executando {0} teste(s)",
        ["test.ok"] = "ok",
        ["test.failed"] = "FALHOU",
        ["test.result"] = "resultado: {0}. {1} passaram; {2} falharam; concluído em {3}",
        ["hint.AL0201"] = "adicione os ramos `when` que faltam ou um `else`",
        ["hint.AL0202"] = "envie o valor por um Channel(T), use um Actor ou retorne-o da tarefa",
        ["hint.AL0203"] = "declare com atribuição simples (`x = ...`) para torná-la mutável",
        ["hint.AL0204"] = "informe o tipo: `x : Tipo? = nil`",
        ["hint.AL0205"] = "anote o tipo: `nome : Tipo`",
        ["hint.AL0206"] = "declare o parâmetro de bloco: `def nome(&bloco : Int32 -> Int32)`",
        ["hint.CS8602"] = "este valor pode ser nil: verifique com `if x.nil?` ou afirme com `x.not_nil!`",
        ["hint.CS0103"] = "não encontrado: confira a grafia ou adicione `using Namespace`",
        ["hint.CS1061"] = "o tipo não tem esse membro; métodos .NET precisam de parênteses, propriedades não",
        ["hint.CS0246"] = "tipo desconhecido: adicione `using Namespace` ou um pacote com `alice add`",
        ["hint.CS0029"] = "os tipos não batem; converta com `.to_i`, `.to_f`, `.to_s` ou `.as(Tipo)`",
    };

    static readonly Dictionary<string, string> Es = new()
    {
        ["tagline"] = "Alice {0} — ergonomía de Ruby, seguridad de Rust, velocidad de C, en .NET",
        ["usage"] = "Uso",
        ["commands"] = "Comandos",
        ["cmd.new"] = "Crea un proyecto nuevo (--lib, --game)",
        ["cmd.build"] = "Compila un proyecto o archivos a IL .NET (--release, --native)",
        ["cmd.run"] = "Compila y ejecuta un proyecto o un archivo .al",
        ["cmd.check"] = "Comprueba tipos sin generar salida",
        ["cmd.test"] = "Ejecuta las funciones test_* en test/*.al",
        ["cmd.add"] = "Añade un paquete NuGet al proyecto",
        ["cmd.clean"] = "Elimina los artefactos de compilación",
        ["cmd.emit"] = "Muestra el C# generado (depuración del compilador)",
        ["cmd.sln"] = "Añade un .alproj a una solución: sln add <sln> <proj>",
        ["cmd.version"] = "Muestra versión, runtime y capacidades SIMD",
        ["cmd.fmt"] = "Formatea archivos .al (--check para CI)",
        ["fmt.formatted"] = "Formateado",
        ["fmt.would"] = "Sin formatear",
        ["fmt.summary"] = "{0} archivo(s), {1} cambiado(s)",
        ["compiling"] = "Compilando",
        ["checking"] = "Comprobando",
        ["running"] = "Ejecutando",
        ["restoring"] = "Restaurando paquetes",
        ["building"] = "Construyendo",
        ["finished"] = "Terminado",
        ["finished.detail"] = "{0} objetivo(s) en {1}",
        ["created"] = "Creado",
        ["created.next"] = "Siguiente: cd {0} && alice run",
        ["adding"] = "Añadiendo",
        ["added"] = "Añadido",
        ["cleaned"] = "Limpio",
        ["error"] = "error",
        ["warning"] = "advertencia",
        ["help"] = "ayuda",
        ["aborting"] = "no se pudo compilar debido a {0} error(es) anterior(es)",
        ["no.project"] = "no se encontró ningún .alproj aquí; indique un archivo o proyecto",
        ["no.files"] = "no hay archivos de entrada",
        ["unknown.command"] = "comando desconocido '{0}'",
        ["exists"] = "el destino '{0}' ya existe",
        ["test.running"] = "ejecutando {0} prueba(s)",
        ["test.ok"] = "ok",
        ["test.failed"] = "FALLÓ",
        ["test.result"] = "resultado: {0}. {1} pasaron; {2} fallaron; terminado en {3}",
        ["hint.AL0201"] = "añada las ramas `when` que faltan o un `else`",
        ["hint.AL0202"] = "envíe el valor por un Channel(T), use un Actor o devuélvalo de la tarea",
        ["hint.AL0203"] = "declárela con asignación simple (`x = ...`) para hacerla mutable",
        ["hint.AL0204"] = "indique el tipo: `x : Tipo? = nil`",
        ["hint.AL0205"] = "anote el tipo: `nombre : Tipo`",
        ["hint.AL0206"] = "declare el parámetro de bloque: `def nombre(&bloque : Int32 -> Int32)`",
        ["hint.CS8602"] = "este valor puede ser nil: compruébelo con `if x.nil?` o afírmelo con `x.not_nil!`",
        ["hint.CS0103"] = "no encontrado: revise la ortografía o añada `using Namespace`",
        ["hint.CS1061"] = "el tipo no tiene ese miembro; los métodos .NET necesitan paréntesis, las propiedades no",
        ["hint.CS0246"] = "tipo desconocido: añada `using Namespace` o un paquete con `alice add`",
        ["hint.CS0029"] = "los tipos no coinciden; convierta con `.to_i`, `.to_f`, `.to_s` o `.as(Tipo)`",
    };
}
