using Microsoft.EntityFrameworkCore;

namespace Workbench.Data;

public sealed class WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options) : DbContext(options)
{
    public DbSet<ComparisonProfile> ComparisonProfiles => Set<ComparisonProfile>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var profile = modelBuilder.Entity<ComparisonProfile>();
        profile.ToTable("ComparisonProfiles", "workbench");
        profile.HasKey(p => p.Id);
        profile.Property(p => p.Name).HasMaxLength(100).IsRequired();
        profile.Property(p => p.Mode).HasMaxLength(20).IsRequired();
        profile.Property(p => p.SourceSchema).HasMaxLength(128);
        profile.Property(p => p.TargetSchema).HasMaxLength(128);
        profile.HasIndex(p => p.Name).IsUnique();
    }
}
// Shared team preferences only; never connection strings, credentials, or query results.
public sealed class ComparisonProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "full";
    public string? SourceSchema { get; set; }
    public string? TargetSchema { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
