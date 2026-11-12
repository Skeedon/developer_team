using DeveloperTeam.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Data;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<AgentTask> Tasks => Set<AgentTask>();
    public DbSet<ReviewerMemory> ReviewerMemories => Set<ReviewerMemory>();
    public DbSet<GeneratedAsset> GeneratedAssets => Set<GeneratedAsset>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ReviewerMemory>().HasKey(x => x.RepositoryKey);
        builder.Entity<GeneratedAsset>().HasKey(x => x.Id);
        builder.Entity<GeneratedAsset>().HasIndex(x => x.TaskId);
        builder.Entity<AgentTask>().HasKey(x => x.Id);
        builder.Entity<AgentTask>().HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
