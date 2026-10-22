using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Arrays of fixed-width literal rows (`points[i] = [x, y]`) that never escape are stored as one flat array
/// (`points[i * 2 + j]`): one allocation instead of n + 1, contiguous rows, no pointer chasing.
/// </summary>
[Collection("console")]
public class FlatRowsTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    const string Points = "n = 5\npoints = Array(Array(Int32)).new(n)\nfor i in 0...n\n  points[i] = [i, i * 10]\nend\n";

    [Fact]
    public void Literal_rows_become_one_flat_array()
    {
        var cs = AliceCompiler.Transpile(Points + "s = 0\nfor i in 0...points.size\n  s += points[i][0] + points[i][1]\nend\nputs s\n");
        Assert.Contains("new int[n * 2]", cs);      // one flat allocation
        Assert.DoesNotContain("new int[] {", cs);   // no per-row allocation
    }

    [Theory]
    [InlineData("s = 0\nfor i in 0...points.size\n  s += points[i][0] + points[i][1]\nend\nputs s", "110")]
    [InlineData("points[2][1] += 7\npoints[3][0] = -1\nputs points[2][1]\nputs points[3][0]\nputs points[3][1]", "27\n-1\n30")]
    [InlineData("puts points.size", "5")]
    public void Flat_rows_keep_array_semantics(string tail, string expected)
        => Assert.Equal(expected, Run(Points + tail));

    [Theory]
    [InlineData("puts points[1][2]")]     // column out of range must raise, not read the next row
    [InlineData("puts points[5][0]")]     // row out of range
    [InlineData("j = -1\nputs points[1][j]")]
    public void Out_of_range_indices_still_raise(string tail)
        => Assert.ThrowsAny<Exception>(() => Run(Points + tail));

    [Theory]
    [InlineData("puts points.inspect")]                                                       // the outer array is used as a value
    [InlineData("row = points[1]\nputs row[0]")]                                              // a row escapes
    [InlineData("for p in points\n  puts p[0]\nend")]                                          // iterated as rows
    [InlineData("points[0] = [1, 2, 3]\nputs points[0][2]")]                                   // ragged
    public void Escaping_or_ragged_rows_stay_jagged(string tail)
        => Assert.DoesNotContain("new int[n * 2]", AliceCompiler.Transpile(Points + tail));

    [Fact]
    public void Rows_must_be_initialized_by_a_full_loop()
    {
        // rows 0 and 1 only: an unassigned row is nil in the jagged form, so it may not become zeros
        var cs = AliceCompiler.Transpile("points = Array(Array(Int32)).new(4)\nfor i in 0...2\n  points[i] = [i, i]\nend\nputs points[1][0]\n");
        Assert.DoesNotContain("new int[4 * 2]", cs);
    }
}
