using DeveloperTeam.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Data;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<AgentTask> Tasks => Set<AgentTask>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AgentTask>().HasKey(x => x.Id);
        builder.Entity<AgentTask>().HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
