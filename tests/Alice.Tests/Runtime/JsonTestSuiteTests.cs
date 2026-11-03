using Alice.Core.Json;
using JsonDocument = Alice.Core.Json.JsonDocument;
using static Alice.Tests.Runtime.JsonTestUtil;

namespace Alice.Tests.Runtime;

/// <summary>
/// nst/JSONTestSuite (`test_parsing`, vendored under JsonTestSuite/): every y_ file is accepted, every n_ file is
/// rejected with <see cref="JsonParseException"/>, by the DOM and by a full on-demand walk, on every stage-1 tier;
/// i_ files may go either way but only through JsonParseException, and DOM and iterator agree.
/// </summary>
public class JsonTestSuiteTests
{
    public static TheoryData<string> Files()
    {
        var d = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(SuiteDir, "*.json").OrderBy(x => x, StringComparer.Ordinal)) d.Add(Path.GetFileName(f));
        return d;
    }

    static bool DomAccepts(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            _ = doc.Root.ToJson();
            return true;
        }
        catch (JsonParseException) { return false; }
    }

    static bool IteratorAccepts(byte[] bytes)
    {
        try
        {
            WalkDocument(bytes);
            return true;
        }
        catch (JsonParseException) { return false; }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Suite_file_is_classified_correctly(string file)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(SuiteDir, file));
        foreach (var tier in Tiers.Where(TierSupported))
        {
            bool dom = WithTier(tier, () => DomAccepts(bytes));
            bool it = WithTier(tier, () => IteratorAccepts(bytes));
            Assert.True(dom == it, $"{file} [{tier}]: DOM {(dom ? "accepts" : "rejects")}, iterator {(it ? "accepts" : "rejects")}");
            if (file.StartsWith("y_")) Assert.True(dom, $"{file} [{tier}] must be accepted");
            if (file.StartsWith("n_")) Assert.False(dom, $"{file} [{tier}] must be rejected");
        }
    }

    [Fact]
    public void Accepted_files_equal_System_Text_Json()
    {
        foreach (var (name, bytes) in Accepted())
        {
            using var ours = JsonDocument.Parse(bytes);
            using var stj = System.Text.Json.JsonDocument.Parse(bytes);
            AssertSame(ours.Root, stj.RootElement, name);
        }
    }

    [Fact]
    public void Suite_summary_counts()
    {
        int y = 0, n = 0, iAccepted = 0, iRejected = 0;
        foreach (var f in Directory.GetFiles(SuiteDir, "*.json"))
        {
            string name = Path.GetFileName(f);
            bool ok = DomAccepts(File.ReadAllBytes(f));
            if (name.StartsWith("y_")) { Assert.True(ok, name); y++; }
            else if (name.StartsWith("n_")) { Assert.False(ok, name); n++; }
            else if (ok) iAccepted++; else iRejected++;
        }
        Assert.Equal(95, y);
        Assert.Equal(188, n);
        Assert.Equal(35, iAccepted + iRejected);
    }
}
