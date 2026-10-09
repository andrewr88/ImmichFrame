using ImmichFrame.Core.Exceptions;
using ImmichFrame.WebApi.Database;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Services;

/// <summary>
/// The settings store with another process writing to the same database: two
/// <see cref="SettingsService"/>s on one database file, each opening connections of its own, which
/// is what two ImmichFrame containers sharing a configuration directory are to SQLite. The admin
/// editor's lock serialises the saves of one process only; between two, the store is all there is.
/// <para>
/// The other process's write is run from inside this one's save, by a
/// <see cref="SaveChangesInterceptor"/> - after the save has read the stored row and before it
/// writes, which is the one moment no check the save makes itself can see coming.
/// </para>
/// </summary>
[TestFixture]
public class SettingsServiceTests
{
    // Three documents a test can tell apart in the store. Not valid settings, and not meant to be:
    // the store keeps text, and nothing here binds it.
    private const string Stored = """{ "General": { "Interval": 1 } }""";
    private const string Ours = """{ "General": { "Interval": 2 } }""";
    private const string Theirs = """{ "General": { "Interval": 3 } }""";

    private const string SettingsJson = """
        {
          "General": {
            "Interval": 45
          },
          "Accounts": [
            {
              "ImmichServerUrl": "http://mock-immich-server.com",
              "ApiKey": "base-api-key"
            }
          ]
        }
        """;

    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"immichframe-settings-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// The version check in the store itself, rather than the editor's in front of it: a caller
    /// presenting a version the stored row is not at is refused, whatever the version is.
    /// </summary>
    [Test]
    public async Task Save_VersionTheStoredRowIsNotAt_IsRefusedWithTheStoredSettingsUntouched()
    {
        // Arrange
        var store = await Started();
        store.Save(Stored, ConfigFormat.Json, 0);

        // Act
        var save = () => store.Save(Ours, ConfigFormat.Json, 99);

        // Assert
        Assert.That(save, Throws.TypeOf<ConfigSaveRefusedException>());
        Assert.That(store.Read(), Is.EqualTo(new StoredSettings(Stored, ConfigFormat.Json, 1)));
    }

    /// <summary>
    /// Another process saving between this save's read and its write. Both read the row at the same
    /// version, so the version check passes for both; what refuses the second write is the row's
    /// version being an EF concurrency token, which makes the update match no row once the first has
    /// moved it on.
    /// </summary>
    [Test]
    public async Task Save_AnotherProcessSavingInBetween_IsRefusedAndTheirSettingsAreKept()
    {
        // Arrange - a stored row both processes read at version 1
        var theirs = await Started();
        theirs.Save(Stored, ConfigFormat.Json, 0);

        var ours = await Started(interleaved: () => theirs.Save(Theirs, ConfigFormat.Json, 1));

        // Act
        var save = () => ours.Save(Ours, ConfigFormat.Json, 1);

        // Assert
        Assert.That(save, Throws.TypeOf<ConfigSaveRefusedException>());
        Assert.That(theirs.Read(), Is.EqualTo(new StoredSettings(Theirs, ConfigFormat.Json, 2)),
            "the save that got there first is the one stored");
    }

    /// <summary>
    /// The same race on a database that holds no settings yet, where both processes insert the row
    /// rather than update it - so there is no version on the row for a concurrency token to catch,
    /// and the second insert fails on the row's key instead. That has to be the same refusal, not
    /// the constraint failure it surfaced as.
    /// </summary>
    [Test]
    public async Task Save_AnotherProcessStoringTheFirstSettingsInBetween_IsRefusedAndTheirSettingsAreKept()
    {
        // Arrange - two processes on a database that holds no settings, so both read version 0
        var theirs = await Started();
        var ours = await Started(interleaved: () => theirs.Save(Theirs, ConfigFormat.Json, 0));

        // Act
        var save = () => ours.Save(Ours, ConfigFormat.Json, 0);

        // Assert
        Assert.That(save, Throws.TypeOf<ConfigSaveRefusedException>());
        Assert.That(theirs.Read(), Is.EqualTo(new StoredSettings(Theirs, ConfigFormat.Json, 1)),
            "the save that got there first is the one stored");
    }

    /// <summary>
    /// Another process holding the database's write lock for longer than a save waits for it. SQLite
    /// answers SQLITE_BUSY, which is the other process's write in flight - the editor's "reload and
    /// try again", not a failure of ImmichFrame's.
    /// </summary>
    [Test]
    public async Task Save_WhileAnotherProcessHoldsTheWriteLock_IsRefusedWithTheStoredSettingsUntouched()
    {
        // Arrange - a store that waits a second for the database, and a writer that holds it longer
        var store = await Started(timeoutSeconds: 1);
        store.Save(Stored, ConfigFormat.Json, 0);

        using (var writer = new SqliteConnection(ConnectionString()))
        {
            writer.Open();
            using var begin = writer.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();

            // Act
            var save = () => store.Save(Ours, ConfigFormat.Json, 1);

            // Assert
            Assert.That(save, Throws.TypeOf<ConfigSaveRefusedException>());
        }

        Assert.That(store.Read(), Is.EqualTo(new StoredSettings(Stored, ConfigFormat.Json, 1)));
    }

    /// <summary>
    /// Two processes starting at once on a settings file neither has imported: both find no stored
    /// row, and both import the file. The second insert fails on the row's key - and the settings it
    /// was about to store are already there, so it has to start on them rather than fail to start.
    /// </summary>
    [Test]
    public async Task InitializeAsync_AnotherProcessImportingInBetween_StartsOnTheSettingsItImported()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_directory, "Settings.json"), SettingsJson);

        var theirs = Store();
        var ours = Store(interleaved: () => theirs.InitializeAsync());

        // Act
        await ours.InitializeAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(ours.IsUnconfigured, Is.False);
            Assert.That(ours.Read(), Is.EqualTo(new StoredSettings(SettingsJson, ConfigFormat.Json, 1)),
                "the one import, stored once");
            Assert.That(ours.LoadCatalog().Default.Accounts.Select(account => account.ApiKey),
                Is.EqualTo(new[] { "base-api-key" }));
        });
    }

    /// <summary>A store whose database has been migrated, as a process has by the time it serves anything.</summary>
    private async Task<SettingsService> Started(Action? interleaved = null, int timeoutSeconds = 30)
    {
        var store = Store(
            interleaved is null ? null : () =>
            {
                interleaved();
                return Task.CompletedTask;
            },
            timeoutSeconds);

        await store.InitializeAsync();

        return store;
    }

    /// <summary>
    /// One process's store on the database in <see cref="_directory"/>.
    /// </summary>
    /// <param name="interleaved">
    /// The other process's write, run from inside every save this one makes, after the save has read
    /// the row and before it writes it.
    /// </param>
    /// <param name="timeoutSeconds">How long a command waits for a database another process has locked.</param>
    private SettingsService Store(Func<Task>? interleaved = null, int timeoutSeconds = 30)
    {
        var options = new DbContextOptionsBuilder<SettingsDbContext>()
            .UseSqlite(ConnectionString(timeoutSeconds));

        if (interleaved is not null)
        {
            options.AddInterceptors(new Interleaving(interleaved));
        }

        return new SettingsService(
            new ContextFactory(options.Options),
            new ConfigLoader(NullLogger<ConfigLoader>.Instance),
            new ConfigLocation(_directory),
            NullLogger<SettingsService>.Instance);
    }

    /// <summary>
    /// Unpooled, so that each store's connections are its own, as two processes' would be, rather than
    /// drawn from the one pool both stores' connection strings would share - and none outlives the test.
    /// </summary>
    private string ConnectionString(int timeoutSeconds = 30) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(_directory, "immichframe.db"),
        Pooling = false,
        DefaultTimeout = timeoutSeconds
    }.ToString();

    private sealed class ContextFactory(DbContextOptions<SettingsDbContext> _options) : IDbContextFactory<SettingsDbContext>
    {
        public SettingsDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Runs another process's write when a save starts - after the store has read the row and decided
    /// what to write, and before EF has opened a transaction to write it in.
    /// </summary>
    private sealed class Interleaving(Func<Task> _otherProcess) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            _otherProcess().GetAwaiter().GetResult();

            return result;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await _otherProcess();

            return result;
        }
    }
}
