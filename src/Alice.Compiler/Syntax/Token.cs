namespace Alice.Compiler;


public sealed class Token
{
    public string Kind = "";   // id const ivar int float str char op nl eof
    public string Text = "";
    public int Line;
    public int Col;
    public bool Space;         // whitespace precedes the token
}
