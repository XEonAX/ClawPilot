using ClawPilot.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClawPilot.Database;

public class ClawPilotDbContext : DbContext
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<SkillState> SkillStates => Set<SkillState>();

    public ClawPilotDbContext(DbContextOptions<ClawPilotDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(e =>
        {
            e.HasIndex(c => c.ChatId).IsUnique();
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.HasIndex(m => new { m.ConversationId, m.Status });
            e.HasIndex(m => m.CreatedAt);
        });

        modelBuilder.Entity<ScheduledTask>(e =>
        {
            e.HasIndex(t => t.ChatId);
        });

        modelBuilder.Entity<SkillState>(e =>
        {
            e.HasIndex(s => s.SkillName).IsUnique();
        });
    }
}
