using System.Reflection;
using ImmichFrame.WebApi.Models;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Models;

/// <summary>
/// Keeps the admin editor's two settings DTOs in step with the settings classes they mirror. The
/// compiler does not check this.
/// <para>
/// <c>AdminConfigService</c> reads every value a save writes from the DTO by reflection. It looks
/// each value up by the name of a property on the settings class, and a name the DTO lacks reads
/// back as null. For a general setting of a reference type, that null was written over the stored
/// value. For one of a value type, the null was refused, so every save that declared the setting
/// failed. For an account, the setting was left out of the file. A read fails in the other
/// direction: a value the DTO constructor never copied reaches the editor as null.
/// </para>
/// </summary>
[TestFixture]
public class AdminConfigDtoTests
{
    /// <summary>
    /// The three general settings a read masks. A read reports each one through a <c>Has</c> flag
    /// instead of its value, and a save reads it from a DTO property of the same name.
    /// </summary>
    private static readonly string[] GeneralSecrets =
    [
        nameof(GeneralSettings.WeatherApiKey),
        nameof(GeneralSettings.Webhook),
        nameof(GeneralSettings.AuthenticationSecret)
    ];

    private static readonly string[] GeneralSecretFlags = [.. GeneralSecrets.Select(secret => $"Has{secret}")];

    [Test]
    public void GeneralSettingsDto_HasAPropertyForEverySettingAndNoOther()
    {
        // Arrange
        var settings = WritableProperties(typeof(GeneralSettings));
        var dto = Properties(typeof(AdminGeneralSettingsDto));

        // Assert
        Assert.Multiple(() =>
        {
            // Every setting except the three secrets is a value that a read reports and a save writes back.
            Assert.That(dto.Keys.Except(GeneralSecrets).Except(GeneralSecretFlags),
                Is.EquivalentTo(settings.Keys.Except(GeneralSecrets)));

            // Each secret is still on the DTO under its own name, because a save reads it from there.
            // A read leaves that property empty and reports the secret through its own flag.
            Assert.That(dto.Keys, Is.SupersetOf(GeneralSecrets));
            Assert.That(GeneralSecretFlags.Select(flag => dto.GetValueOrDefault(flag)?.PropertyType),
                Is.All.EqualTo(typeof(bool)));

            AssertSameTypes(settings, dto);
        });
    }

    [Test]
    public void AccountSettingsDto_HasAPropertyForEverySettingAndNoOther()
    {
        // Arrange
        var settings = WritableProperties(typeof(ServerAccountSettings));
        var dto = Properties(typeof(AdminAccountSettingsDto));

        // These exist only on the DTO and never in a settings file. Each has its own reason:
        string[] editorOnly =
        [
            // Id is the handle a read issues so that a save can put a masked key back on the account
            // it came from. It is derived from the stored settings' version and the account's
            // position, and is never stored.
            nameof(AdminAccountSettingsDto.Id),
            // HasApiKey is how a read reports the masked API key, as a Has flag reports a general secret.
            nameof(AdminAccountSettingsDto.HasApiKey),
            // ApiKeyFromFile says whether that key is read from ApiKeyFile. A read works it out from
            // ApiKeyFile, and a save never writes it.
            nameof(AdminAccountSettingsDto.ApiKeyFromFile)
        ];

        // Assert
        Assert.Multiple(() =>
        {
            // ApiKey is compared with the rest. A read masks it, but it is still the property a save
            // takes a newly typed key from.
            Assert.That(dto.Keys.Except(editorOnly), Is.EquivalentTo(settings.Keys));

            AssertSameTypes(settings, dto);
        });
    }

    [Test]
    public void GeneralSettingsDto_FromTheSettings_CarriesEveryValueButNoSecret()
    {
        // Arrange
        var settings = WithEverySettingChanged(new GeneralSettings());

        // Act
        var dto = new AdminGeneralSettingsDto(settings);

        // Assert
        Assert.Multiple(() =>
        {
            foreach (var (name, setting) in WritableProperties(typeof(GeneralSettings)))
            {
                if (GeneralSecrets.Contains(name))
                {
                    Assert.That(ValueOf(dto, name), Is.Null, $"{name} is a secret, so a read must never carry it");
                    Assert.That(ValueOf(dto, $"Has{name}"), Is.EqualTo(true), $"Has{name} has to report it as set");
                }
                else
                {
                    Assert.That(ValueOf(dto, name), Is.EqualTo(setting.GetValue(settings)), $"{name} is not carried");
                }
            }
        });
    }

    [Test]
    public void AccountSettingsDto_FromTheSettings_CarriesEveryValueButTheKey()
    {
        // Arrange
        var settings = WithEverySettingChanged(new ServerAccountSettings());

        // Act
        var dto = new AdminAccountSettingsDto(settings);

        // Assert
        Assert.Multiple(() =>
        {
            foreach (var (name, setting) in WritableProperties(typeof(ServerAccountSettings)))
            {
                switch (name)
                {
                    // Masked. A read never carries the key, and HasApiKey reports it instead.
                    case nameof(ServerAccountSettings.ApiKey):
                        Assert.That(dto.ApiKey, Is.Null, "the API key is a secret, so a read must never carry it");
                        break;

                    // The constructor is given only IAccountSettings, which has no label. The read
                    // sets the label from the concrete settings itself, and the host tests in
                    // AdminConfigControllerTests cover that.
                    case nameof(ServerAccountSettings.Label):
                        break;

                    default:
                        Assert.That(ValueOf(dto, name), Is.EqualTo(setting.GetValue(settings)), $"{name} is not carried");
                        break;
                }
            }

            // The settings name an ApiKeyFile, which is a key by either measure.
            Assert.That(dto.HasApiKey, Is.True);
            Assert.That(dto.ApiKeyFromFile, Is.True);
        });
    }

    /// <summary>
    /// Checks that each DTO property holds the type of the setting with the same name, either the
    /// setting's own type or that type made nullable. The DTO leaves unset any setting it is not
    /// given. Holding the same type is what lets the binder read the value a save writes back into
    /// the setting.
    /// </summary>
    private static void AssertSameTypes(
        Dictionary<string, PropertyInfo> settings, Dictionary<string, PropertyInfo> dto)
    {
        foreach (var (name, setting) in settings)
        {
            if (!dto.TryGetValue(name, out var property)) continue;

            Assert.That(WithoutNullable(property.PropertyType), Is.EqualTo(WithoutNullable(setting.PropertyType)),
                $"{name} on the DTO holds a different type from the setting");
        }
    }

    /// <summary>
    /// Returns <paramref name="settings"/> with every writable property set to a value other than
    /// its built-in default. A property the DTO constructor forgets then reads back as a default,
    /// instead of passing because the default happens to match. The values are chosen by type
    /// rather than listed by name, so a setting added later is covered without editing this test.
    /// </summary>
    private static T WithEverySettingChanged<T>(T settings) where T : new()
    {
        var defaults = new T();

        foreach (var property in WritableProperties(typeof(T)).Values)
        {
            var fallback = property.GetValue(defaults);
            property.SetValue(settings, OtherThan(fallback, property));

            Assert.That(property.GetValue(settings), Is.Not.EqualTo(fallback),
                $"{property.Name} still holds its default, so the test cannot tell whether it is carried");
        }

        return settings;
    }

    private static object OtherThan(object? fallback, PropertyInfo property)
    {
        var type = WithoutNullable(property.PropertyType);

        if (type == typeof(string)) return $"{property.Name}-value";
        if (type == typeof(bool)) return !(bool)(fallback ?? false);
        if (type == typeof(int)) return (int)(fallback ?? 0) + 7;
        if (type == typeof(double)) return (double)(fallback ?? 0d) + 0.5;
        if (type == typeof(DateTime)) return new DateTime(2001, 2, 3);
        if (type == typeof(List<string>)) return new List<string> { $"{property.Name}-entry" };
        if (type == typeof(List<Guid>)) return new List<Guid> { Guid.NewGuid() };

        throw new NotSupportedException($"There is no value here for {property.Name}, of type {type}. Add one.");
    }

    private static Type WithoutNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static object? ValueOf(object dto, string name) => dto.GetType().GetProperty(name)?.GetValue(dto);

    /// <summary>
    /// The settings a class accepts, collected the way <c>AdminConfigService.WritableProperties</c>
    /// collects them: every public instance property with a setter. Keyed by exact name, because a
    /// save looks each value up on the DTO by that name, case and all.
    /// </summary>
    private static Dictionary<string, PropertyInfo> WritableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.Ordinal);

    private static Dictionary<string, PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, StringComparer.Ordinal);
}
