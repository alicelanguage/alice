namespace Alice.Compiler;

sealed class ClassCtx
{
    public string Name = "Program";
    public string Kind = "program";
    public string Generic = "";
    public HashSet<string> Members = new();
    public List<string> AutoFields = new();
    public HashSet<string> Ops = new();
    public List<string> Bases = new();
}
