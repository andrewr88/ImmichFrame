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

    /// <summary>
    /// A line means what it means to docker compose, short of escapes and of values over several
    /// lines: it is trimmed, an <c>export</c> in front is ignored, so is whitespace around the name
    /// and the value, and a value loses one pair of quotes - keeping what they hold as written - or,
    /// unquoted, a comment, which a space and a '#' begin. Each was taken as it stood, so a line set
    /// another name or value under <c>dotnet run</c> than compose hands the container, and a line
    /// naming nothing threw and stopped the host starting.
    /// </summary>
    [TestCase("  KEY=value  ", "value", TestName = "Load_IndentedLine_IsReadTrimmed")]
    [TestCase("export KEY=value", "value", TestName = "Load_ExportInFront_IsIgnored")]
    [TestCase("KEY = value", "value", TestName = "Load_SpacesAroundTheEqualsSign_AreIgnored")]
    [TestCase("=no-name\nKEY=value", "value", TestName = "Load_LineThatNamesNothing_IsSkipped")]
    [TestCase("KEY=\"double quoted\"", "double quoted", TestName = "Load_DoubleQuotedValue_LosesItsQuotes")]
    [TestCase("KEY='single quoted'", "single quoted", TestName = "Load_SingleQuotedValue_LosesItsQuotes")]
    [TestCase("KEY=\" kept # as written \"", " kept # as written ", TestName = "Load_QuotedValue_KeepsWhatItsQuotesHold")]
    [TestCase("KEY=\"value\" # comment", "value", TestName = "Load_CommentAfterAQuotedValue_IsLeftOut")]
    [TestCase("KEY=value # comment", "value", TestName = "Load_CommentAfterAnUnquotedValue_IsLeftOut")]
    [TestCase("KEY=value#not-a-comment", "value#not-a-comment", TestName = "Load_HashWithNoSpaceBeforeIt_IsPartOfTheValue")]
    public void Load_Line_IsReadAsDockerComposeReadsIt(string content, string expected)
    {
        // Arrange - KEY stands for the marker, so a line read as some other name is found all the same
        File.WriteAllText(_file, content.Replace("KEY", _marker));

        // Act
        DotEnv.Load(_file);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(VariablesCarryingTheMarker(), Is.EquivalentTo(new[] { _marker }));
            Assert.That(Environment.GetEnvironmentVariable(_marker), Is.EqualTo(expected));
        });
    }

    private List<string> VariablesCarryingTheMarker() =>
        Environment.GetEnvironmentVariables().Keys
            .Cast<string>()
            .Where(name => name.Contains(_marker, StringComparison.Ordinal))
            .ToList();
}
