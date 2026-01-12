using Microsoft.EntityFrameworkCore;
using Susurri.Modules.Users.Core.Entities;

namespace Susurri.Modules.Users.Core.DAL;

    public sealed class UsersDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }
        
        public UsersDbContext(DbContextOptions<UsersDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
        }
    }
