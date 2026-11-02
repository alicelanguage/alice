using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>`ALICE_PAR=0` runs forks and parallel loops serially without changing the CPU count .NET sees.</summary>
public class ParSerialSwitchTests
{
    [Theory]
    [InlineData("0", true)]
    [InlineData(" off ", true)]
    [InlineData("FALSE", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Setting_values(string? value, bool serial) => Assert.Equal(serial, Par.IsSerialSetting(value));

    [Fact]
    public void Switch_follows_the_environment_and_gates_forks_and_workers()
    {
        Assert.Equal(Par.IsSerialSetting(Environment.GetEnvironmentVariable("ALICE_PAR")), Par.Serial);
        if (Par.Serial)
        {
            Assert.False(Par.Forkable);
            Assert.Equal(1, Par.Workers);
        }
        else
        {
            Assert.Equal(Environment.ProcessorCount, Par.Workers);
            Assert.Equal(Environment.ProcessorCount > 1, Par.Forkable);
        }
    }
}
