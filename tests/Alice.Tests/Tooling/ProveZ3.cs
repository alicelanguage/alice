using Alice.Cli.Proof;

namespace Alice.Tests.Tooling;

/// <summary>
/// Whether the SMT tests (`alice prove --smt`, ADR 0022) can run: Z3 is optional, so without it they return early.
/// ALICE_REQUIRE_Z3=1 (CI, which installs Z3 on every runner) turns a missing Z3 into a failure, so the SMT path can
/// never silently stop being tested.
/// </summary>
static class ProveZ3
{
    public static bool Available()
    {
        if (SmtProver.Unavailable() is not { } why) return true;
        if (Environment.GetEnvironmentVariable("ALICE_REQUIRE_Z3") == "1") Assert.Fail("ALICE_REQUIRE_Z3=1 but " + why);
        return false;
    }
}
