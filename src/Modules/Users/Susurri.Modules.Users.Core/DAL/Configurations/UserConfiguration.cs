using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Susurri.Modules.Users.Core.Entities;
using Susurri.Modules.Users.Core.ValueObjects;

namespace Susurri.Modules.Users.Core.DAL.Configurations;

internal class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).HasConversion(x => x.Value, x => new UserId(x));
        builder.Property(x => x.PublicKey).IsRequired();
        builder.Property(x => x.Username)
            .HasConversion(x => x.Value, x => new Username(x))
            .IsRequired();
        builder.Property(x => x.CreatedAt);
        builder.Property(x => x.LastSeenAt);
    }
}