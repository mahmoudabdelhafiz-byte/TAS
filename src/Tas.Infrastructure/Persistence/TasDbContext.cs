using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tas.Domain;
using Tas.Domain.Capacity;

namespace Tas.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping onto db/schema.sql (PostgreSQL, snake_case names).
/// The schema is created from db/schema.sql for now; EF migrations come later.
/// </summary>
public sealed class TasDbContext(DbContextOptions<TasDbContext> options) : DbContext(options)
{
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<TransactionCategory> TransactionCategories => Set<TransactionCategory>();
    public DbSet<CapacityTemplate> CapacityTemplates => Set<CapacityTemplate>();
    public DbSet<CapacityTemplateSlot> CapacityTemplateSlots => Set<CapacityTemplateSlot>();
    public DbSet<CapacityTemplateQuota> CapacityTemplateQuotas => Set<CapacityTemplateQuota>();
    public DbSet<CapacityOverride> CapacityOverrides => Set<CapacityOverride>();
    public DbSet<QuotaReleaseRule> QuotaReleaseRules => Set<QuotaReleaseRule>();
    public DbSet<CapacityReservation> CapacityReservations => Set<CapacityReservation>();
    public DbSet<Slot> Slots => Set<Slot>();
    public DbSet<SlotCategoryCapacity> SlotCategoryCapacities => Set<SlotCategoryCapacity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // PostgreSQL timestamptz only accepts UTC offsets through Npgsql; normalize everything on the way in.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Terminal>(e =>
        {
            e.HasKey(x => x.TerminalId);
            e.Property(x => x.TerminalType).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<TransactionCategory>(e =>
        {
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.Mode).HasUpperCaseConversion();
            e.Property(x => x.UnitType).HasUpperCaseConversion();
        });

        b.Entity<CapacityTemplate>(e =>
        {
            e.HasKey(x => x.TemplateId);
            e.HasMany(x => x.Slots).WithOne().HasForeignKey(s => s.TemplateId);
        });

        b.Entity<CapacityTemplateSlot>(e =>
        {
            e.HasKey(x => x.TemplateSlotId);
            e.Property(x => x.Day).HasColumnName("day_of_week").HasConversion<short>();
            e.HasMany(x => x.Quotas).WithOne().HasForeignKey(q => q.TemplateSlotId);
        });

        b.Entity<CapacityTemplateQuota>(e => e.HasKey(x => new { x.TemplateSlotId, x.CategoryId }));

        b.Entity<CapacityOverride>(e =>
        {
            e.HasKey(x => x.OverrideId);
            e.Property(x => x.Action).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<QuotaReleaseRule>(e => e.HasKey(x => new { x.TerminalId, x.CategoryId }));

        b.Entity<CapacityReservation>(e => e.HasKey(x => x.ReservationId));

        b.Entity<Slot>(e =>
        {
            e.HasKey(x => x.SlotId);
            e.Property(x => x.GeneratedAt).HasDefaultValueSql("now()");
            e.HasMany(x => x.Categories).WithOne().HasForeignKey(c => c.SlotId);
        });

        b.Entity<SlotCategoryCapacity>(e => e.HasKey(x => new { x.SlotId, x.CategoryId }));

        ApplySnakeCaseNames(b);
    }

    /// <summary>PascalCase class/property names → snake_case table/column names, unless set explicitly.</summary>
    private static void ApplySnakeCaseNames(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnakeCase(entity.ClrType.Name));

            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                    property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name) =>
        Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}

internal sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);

internal static class EnumConversionExtensions
{
    /// <summary>Stores enums as the upper-case strings used in the schema's CHECK constraints, e.g. "ADJUST".</summary>
    public static PropertyBuilder<TEnum> HasUpperCaseConversion<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum =>
        property.HasConversion(
            v => v.ToString().ToUpperInvariant(),
            v => Enum.Parse<TEnum>(v, true));
}
