using System.Text;

namespace Alice.Compiler;

/// <summary>Single-pass Alice -> C# lowering. Roslyn then emits IL; C# text is an internal representation.</summary>
public sealed partial class Translator
{
    readonly Globals g;
    readonly string file;
    List<Token> t = new();
    int p;
    List<Dictionary<string, string>> scopes = new();
    List<string> ctx = new();
    ClassCtx cur = new();
    string method = "";
    string methodArgs = "";
    bool noDo;
    bool lastIsType;
    string kind = "value";
    int tmp;
    readonly List<string> attrs = new();

    const string Keywords = " if unless while until for in do end then else elsif case when begin rescue ensure return break next and or not def class struct module interface enum lib fun namespace using require getter property setter include abstract private protected self nil true false super yield loop raise of forall spawn ";
    const string CsKeywords = " abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while ";
    const string Levels = "|| ;&& ;== != === ;< > <= >= <=> ;| ;^ ;& ;<< >> ;+ - ;* / % ";

    public Translator(Globals g, string file)
    {
        this.g = g;
        this.file = file;
    }
}
