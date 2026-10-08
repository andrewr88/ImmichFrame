using ImmichFrame.WebApi.Helpers;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

/// <summary>
/// What a <c>LOG_LEVEL</c> value resolves to. The case that matters is the value that names no
/// level: it has to leave logging at <c>Information</c>, not wherever <c>Enum.TryParse</c> happens
/// to leave its out parameter when it fails - which is <c>Trace</c>.
/// </summary>
[TestFixture]
public class LogLevelSettingTests
{
    [TestCase("verbose")]
    [TestCase("warn")]
    [TestCase("42")]
    public void Parse_ValueNamingNoLevel_KeepsInformationAndRejectsIt(string value)
    {
        // Act
        var level = LogLevelSetting.Parse(value, out var rejected);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(level, Is.EqualTo(LogLevel.Information));
            Assert.That(rejected, Is.True);
        });
    }

    /// <summary>Unset is the normal case, not a mistake, so it is not reported as one.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_Unset_KeepsInformationWithoutRejectingIt(string? value)
    {
        // Act
        var level = LogLevelSetting.Parse(value, out var rejected);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(level, Is.EqualTo(LogLevel.Information));
            Assert.That(rejected, Is.False);
        });
    }

    [TestCase("debug", LogLevel.Debug)]
    [TestCase("Warning", LogLevel.Warning)]
    public void Parse_ValueNamingALevel_UsesIt(string value, LogLevel expected)
    {
        // Act
        var level = LogLevelSetting.Parse(value, out var rejected);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(level, Is.EqualTo(expected));
            Assert.That(rejected, Is.False);
        });
    }
}
