namespace Alice.Runtime;

public static partial class Tasks
{
    public static void await_all(params Task[] ts) => Task.WaitAll(ts);
}
