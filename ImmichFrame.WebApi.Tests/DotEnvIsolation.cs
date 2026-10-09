using NUnit.Framework;

/// <summary>
/// Keeps every host the suite starts from loading the developer's own <c>docker/.env</c>.
/// <para>
/// <c>WebApplicationFactory</c> runs the host as Development, and a Development host loads
/// <c>../docker/.env</c> from its working directory - which is wherever the test runner started. From
/// a project directory that is the developer's real file, and what it sets is set for the whole
/// process: its admin sign-in settings switched the OpenID Connect handler on in every host after
/// it, and the tests that start a host with the surface off, half set up or set up their own way
/// failed for reasons nobody could see in the tests themselves.
/// </para>
/// <para>
/// Outside any namespace, which is what makes NUnit run it around every fixture in the assembly.
/// <c>DevelopmentDotEnvTests</c> opts back in, because the load itself is what it tests.
/// </para>
/// </summary>
[SetUpFixture]
public class DotEnvIsolation
{
    private string? _previous;

    [OneTimeSetUp]
    public void SkipDotEnv()
    {
        _previous = Environment.GetEnvironmentVariable(Program.SkipDotEnvVariable);
        Environment.SetEnvironmentVariable(Program.SkipDotEnvVariable, "true");
    }

    [OneTimeTearDown]
    public void RestoreEnvironment() => Environment.SetEnvironmentVariable(Program.SkipDotEnvVariable, _previous);
}
