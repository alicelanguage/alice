using Alice.Cli.Projects;

namespace Alice.Tests.Tooling;

/// <summary>Helper programs (dotnet, node, clang) come from absolute PATH entries, never from the working directory.</summary>
public class ExecutablesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "alice-exe-" + Guid.NewGuid().ToString("N"));
    static readonly string Name = "alice-probe-tool";

    public ExecutablesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    string Plant(string dir)
    {
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, OperatingSystem.IsWindows() ? Name + ".exe" : Name);
        File.WriteAllText(f, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, (UnixFileMode)0b111_101_101);
        return f;
    }

    [Fact]
    public void Resolves_from_absolute_path_entries_only()
    {
        Plant(Path.Combine(root, "checkout"));
        var trusted = Plant(Path.Combine(root, "bin"));
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(root, "checkout"));
        // On Windows, GetRelativePath returns an absolute path when crossing drives (temp on C:, repo on D:)
        if (Path.IsPathFullyQualified(relative)) relative = ".";
        string path = string.Join(Path.PathSeparator, "", ".", relative, Path.Combine(root, "bin"));
        Assert.Equal(trusted, Executables.Resolve(Name, path));
    }

    [Fact]
    public void Missing_tool_is_not_found_even_when_the_checkout_has_one()
    {
        Plant(Path.Combine(root, "checkout"));
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(root, "checkout"));
        // On Windows, GetRelativePath returns an absolute path when crossing drives (temp on C:, repo on D:)
        if (Path.IsPathFullyQualified(relative)) relative = ".";
        Assert.Throws<System.ComponentModel.Win32Exception>(() => Executables.Resolve(Name, string.Join(Path.PathSeparator, ".", relative)));
    }

    [Fact]
    public void Non_executable_files_are_skipped_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = Plant(Path.Combine(root, "a"));
        File.SetUnixFileMode(first, (UnixFileMode)0b110_100_100);
        var second = Plant(Path.Combine(root, "b"));
        Assert.Equal(second, Executables.Resolve(Name, string.Join(Path.PathSeparator, Path.Combine(root, "a"), Path.Combine(root, "b"))));
    }
}
