using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Profiler.Api.Entities;

namespace Profiler.Api.DAL.Configurations
{
    public class TypingSessionConfiguration : IEntityTypeConfiguration<TypingSession>
    {
        public void Configure(EntityTypeBuilder<TypingSession> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(x => x.RawDataJson)
                .IsRequired()
                .HasColumnType("jsonb");

            builder.Property(x => x.Platform)
                .HasMaxLength(50);

            builder.Property(x => x.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            builder.HasIndex(x => x.UserId);
        }
    }
}