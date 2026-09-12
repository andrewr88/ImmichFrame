using Microsoft.EntityFrameworkCore;

namespace ImmichFrame.WebApi.Database;

public class SettingsDbContext : DbContext
{
    public SettingsDbContext(DbContextOptions<SettingsDbContext> options) : base(options) { }

    public DbSet<SettingsDocument> SettingsDocuments => Set<SettingsDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettingsDocument>()
            .Property(d => d.Version)
            .IsConcurrencyToken();
    }
}

public class SettingsDocument
{
    public int Id { get; set; }

    // Raw ServerSettings V2 JSON, pre-Validate (ApiKeyFile stays unresolved)
    public string Json { get; set; } = "{}";

    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// Which format <see cref="Json"/> is written in - "json" or "yaml". Kept rather than
    /// normalising everything to JSON on import: YAML's representation model carries no resolved
    /// scalar type, so converting a YAML document would turn every number and boolean in it into a
    /// string. The document is stored exactly as it was imported and rewritten in the same format.
    /// </summary>
    public string Format { get; set; } = "json";

    public DateTime UpdatedAtUtc { get; set; }

    // "Settings.json" | "Settings.yml" | "env" | null when created via admin UI
    public string? ImportedFrom { get; set; }

    public long Version { get; set; }
}
