using ImmichFrame.Core.Helpers;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Helpers;

/// <summary>
/// <c>DotEnv</c> reads the same <c>docker/.env</c> that docker compose does, so it has to agree
/// with compose about what a comment is. It used to split every line on its first <c>=</c>, which
/// turned a commented-out <c># IMMICHFRAME_CONFIG_PATH=/app/Config</c> into a variable named
/// <c># IMMICHFRAME_CONFIG_PATH</c>.
/// </summary>
[TestFixture]
public class DotEnvTests
{
    // Environment variables outlive the test, so every name this fixture can produce - including
    // the ones a comment line would - carries a marker it can find them by and remove them with.
    private string _marker = null!;
    private string _file = null!;

    [SetUp]
    public void Setup()
    {
        _marker = $"IMMICHFRAME_DOTENV_TEST_{Guid.NewGuid():N}";
        _file = Path.Combine(Path.GetTempPath(), $"immichframe-dotenv-{Guid.NewGuid():N}.env");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var name in VariablesCarryingTheMarker())
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        if (File.Exists(_file)) File.Delete(_file);
    }

    [Test]
    public void Load_CommentAndBlankLines_SetNothing()
    {
        // Arrange
        File.WriteAllLines(_file,
        [
            $"# {_marker}_COMMENTED=/app/Config",
            $"   #{_marker}_INDENTED=value",
            "",
            "   ",
            $"{_marker}=loaded"
        ]);

        // Act
        DotEnv.Load(_file);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(VariablesCarryingTheMarker(), Is.EquivalentTo(new[] { _marker }));
            Assert.That(Environment.GetEnvironmentVariable(_marker), Is.EqualTo("loaded"));
        });
    }

    private List<string> VariablesCarryingTheMarker() =>
        Environment.GetEnvironmentVariables().Keys
            .Cast<string>()
            .Where(name => name.Contains(_marker, StringComparison.Ordinal))
            .ToList();
}
