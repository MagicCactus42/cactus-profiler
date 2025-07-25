using Microsoft.EntityFrameworkCore;
using Profiler.Api.DAL.Configurations;
using Profiler.Api.Entities;

namespace Profiler.Api.DAL;

public class ProfilerDbContext : DbContext
{
    public ProfilerDbContext(DbContextOptions<ProfilerDbContext> options) : base(options)
    {
    }

    public DbSet<TypingSession> TypingSessions { get; set; }
    public DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new TypingSessionConfiguration());
    
        modelBuilder.ApplyConfiguration(new UserConfiguration());
    }
}