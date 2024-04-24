using Microsoft.EntityFrameworkCore;
using Susurri.Client.Abstractions;
using Susurri.Client.Entities;

namespace Susurri.Client.DAL;

internal sealed class SusurriDbContext : DbContext, ISusurriDbContext
{
    public DbSet<ChatMessage> ChatMessages { get; init; }
    public DbSet<User> Users { get; init; }

    public SusurriDbContext() {}
    public SusurriDbContext(DbContextOptions<SusurriDbContext> dbContextOptions) : base(dbContextOptions)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
    }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseNpgsql("Host=localhost;Database=postgres;Username=postgres;Password=DtMMaNtC44i4");
        }
    }

}