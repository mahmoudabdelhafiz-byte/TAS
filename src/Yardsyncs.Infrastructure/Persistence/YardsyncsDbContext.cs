using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Capacity;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping onto db/schema.sql (PostgreSQL, snake_case names).
/// The schema is created from db/schema.sql for now; EF migrations come later.
/// </summary>
public sealed class YardsyncsDbContext(DbContextOptions<YardsyncsDbContext> options) : DbContext(options)
{
    public DbSet<TerminalOperator> TerminalOperators => Set<TerminalOperator>();
    public DbSet<Branding> Brandings => Set<Branding>();
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
    public DbSet<TerminalSetting> TerminalSettings => Set<TerminalSetting>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordToken> PasswordTokens => Set<PasswordToken>();
    public DbSet<ContainerWatch> ContainerWatches => Set<ContainerWatch>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<GateLane> GateLanes => Set<GateLane>();
    public DbSet<GateLaneDevice> LaneDevices => Set<GateLaneDevice>();
    public DbSet<GateSystemKey> GateSystemKeys => Set<GateSystemKey>();
    public DbSet<CompanyApiKey> CompanyApiKeys => Set<CompanyApiKey>();
    public DbSet<LaneDeviceRead> LaneDeviceReads => Set<LaneDeviceRead>();
    public DbSet<LaneDeviceCommand> LaneDeviceCommands => Set<LaneDeviceCommand>();
    public DbSet<LaneTransaction> LaneTransactions => Set<LaneTransaction>();
    public DbSet<TosGateMessage> TosGateMessages => Set<TosGateMessage>();
    public DbSet<GateInspection> GateInspections => Set<GateInspection>();
    public DbSet<InspectionDamage> InspectionDamages => Set<InspectionDamage>();
    public DbSet<InspectionPhoto> InspectionPhotos => Set<InspectionPhoto>();
    public DbSet<InspectionDispute> InspectionDisputes => Set<InspectionDispute>();
    public DbSet<GateExitCheck> GateExitChecks => Set<GateExitCheck>();
    public DbSet<GateEvent> GateEvents => Set<GateEvent>();
    public DbSet<GateTrouble> GateTroubles => Set<GateTrouble>();
    public DbSet<Truck> Trucks => Set<Truck>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<DriverDevice> DriverDevices => Set<DriverDevice>();
    public DbSet<DriverVerification> DriverVerifications => Set<DriverVerification>();
    public DbSet<DriverOtp> DriverOtps => Set<DriverOtp>();
    public DbSet<GatePass> GatePasses => Set<GatePass>();
    public DbSet<AppointmentEta> AppointmentEtas => Set<AppointmentEta>();
    public DbSet<GeofenceEvent> GeofenceEvents => Set<GeofenceEvent>();
    public DbSet<TerminalMap> TerminalMaps => Set<TerminalMap>();
    public DbSet<YardInstruction> YardInstructions => Set<YardInstruction>();
    public DbSet<IntegrationEvent> IntegrationEvents => Set<IntegrationEvent>();
    public DbSet<CargoAllocation> CargoAllocations => Set<CargoAllocation>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentItem> AppointmentItems => Set<AppointmentItem>();
    public DbSet<ValidationResult> ValidationResults => Set<ValidationResult>();
    public DbSet<AppointmentStatusHistory> AppointmentStatusHistory => Set<AppointmentStatusHistory>();
    public DbSet<AppointmentCharge> AppointmentCharges => Set<AppointmentCharge>();
    public DbSet<AppointmentMessage> AppointmentMessages => Set<AppointmentMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // PostgreSQL timestamptz only accepts UTC offsets through Npgsql; normalize everything on the way in.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TerminalOperator>(e =>
        {
            e.HasKey(x => x.OperatorId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Branding>(e =>
        {
            e.HasKey(x => x.BrandingId);
            e.Property(x => x.Color).HasColumnType("char(7)");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });

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
            e.Property(x => x.Flow).HasConversion(new UpperSnakeEnumConverter<CategoryFlow>());
        });

        b.Entity<GateExitCheck>(e =>
        {
            e.HasKey(x => x.AppointmentId);
            e.Property(x => x.AppointmentId).ValueGeneratedNever();
            e.Property(x => x.Status).HasUpperCaseConversion();
            e.Property(x => x.WeightInKg).HasColumnType("numeric(9,1)");
            e.Property(x => x.WeightOutKg).HasColumnType("numeric(9,1)");
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

        b.Entity<TerminalSetting>(e => e.HasKey(x => new { x.TerminalId, x.SettingKey }));

        // ---------- Gate ----------

        b.Entity<GateLane>(e =>
        {
            e.HasKey(x => x.LaneId);
            e.Property(x => x.Direction).HasUpperCaseConversion();
            e.Property(x => x.State).HasColumnName("lane_state").HasUpperCaseConversion();
            e.Property(x => x.Mode).HasColumnName("operation_mode").HasUpperCaseConversion();
        });

        b.Entity<GateLaneDevice>(e =>
        {
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.Kind).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.KeyHash).HasColumnType("char(64)");
            e.Property(x => x.ReportedStatus).HasConversion(new UpperSnakeEnumConverter<DeviceHealth>());
        });

        b.Entity<CompanyApiKey>(e =>
        {
            e.HasKey(x => x.KeyId);
            e.Property(x => x.Scope).HasUpperCaseConversion();
            e.Property(x => x.KeyHash).HasColumnType("char(64)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<GateSystemKey>(e =>
        {
            e.HasKey(x => x.KeyId);
            e.Property(x => x.KeyHash).HasColumnType("char(64)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<LaneDeviceRead>(e =>
        {
            e.HasKey(x => x.ReadId);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.ReceivedAt).HasDefaultValueSql("now()");
        });

        b.Entity<GateInspection>(e =>
        {
            e.HasKey(x => x.InspectionId);
            e.Property(x => x.Stage).HasUpperCaseConversion();
            e.Property(x => x.SealStatus).HasUpperCaseConversion();
            e.Property(x => x.Source).HasUpperCaseConversion();
            e.HasMany(x => x.Damage).WithOne().HasForeignKey(d => d.InspectionId);
        });

        b.Entity<InspectionDamage>(e =>
        {
            e.HasKey(x => x.DamageId);
            e.Property(x => x.Side).HasConversion(new UpperSnakeEnumConverter<DamageSide>());
            e.Property(x => x.Kind).HasUpperCaseConversion();
            e.Property(x => x.Severity).HasUpperCaseConversion();
        });

        b.Entity<InspectionPhoto>(e =>
        {
            e.HasKey(x => x.PhotoId);
            e.Property(x => x.UploadedAt).HasDefaultValueSql("now()");
        });

        b.Entity<InspectionDispute>(e =>
        {
            e.HasKey(x => x.DisputeId);
            e.Property(x => x.Status).HasUpperCaseConversion();
        });

        b.Entity<TosGateMessage>(e =>
        {
            e.HasKey(x => x.MessageId);
            e.Property(x => x.Stage).HasUpperCaseConversion();
            e.Property(x => x.Status).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<LaneTransaction>(e =>
        {
            e.HasKey(x => x.TransactionId);
            e.Property(x => x.Status).HasUpperCaseConversion();
            e.Property(x => x.WeightKg).HasColumnType("numeric(9,1)");
        });

        b.Entity<LaneDeviceCommand>(e =>
        {
            e.HasKey(x => x.CommandId);
            e.Property(x => x.Command).HasUpperCaseConversion();
            e.Property(x => x.Result).HasConversion(new UpperSnakeEnumConverter<CommandResult>());
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<GateEvent>(e =>
        {
            e.HasKey(x => x.GateEventId);
            e.Property(x => x.EventType).HasUpperCaseConversion();
        });

        b.Entity<GateTrouble>(e =>
        {
            e.HasKey(x => x.TroubleId);
            e.Property(x => x.ReasonCode).HasUpperCaseConversion();
            e.Property(x => x.Status).HasUpperCaseConversion();
            e.Property(x => x.Resolution).HasConversion(new UpperSnakeEnumConverter<TroubleResolution>());
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<AppointmentMessage>(e =>
        {
            e.HasKey(x => x.MessageId);
            e.Property(x => x.Direction).HasUpperCaseConversion();
            e.Property(x => x.MessageType).HasUpperCaseConversion();
            e.Property(x => x.SentAt).HasDefaultValueSql("now()");
        });

        b.Entity<NotificationLog>(e =>
        {
            e.HasKey(x => x.NotificationId);
            e.Property(x => x.Channel).HasUpperCaseConversion();
            e.Property(x => x.Status).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Params).HasColumnType("jsonb");
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.AuditId);
            e.Property(x => x.Action).HasUpperCaseConversion();
            e.Property(x => x.OldValues).HasColumnType("jsonb");
            e.Property(x => x.NewValues).HasColumnType("jsonb");
            e.Property(x => x.ChangedAt).HasDefaultValueSql("now()");
        });

        // ---------- Parties ----------

        b.Entity<Company>(e =>
        {
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyType).HasUpperCaseConversion();
            e.Property(x => x.Status).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.Role).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<ContainerWatch>(e =>
        {
            e.HasKey(x => x.WatchId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<PasswordToken>(e =>
        {
            e.HasKey(x => x.TokenId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.RefreshTokenId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Truck>(e => e.HasKey(x => x.TruckId));
        b.Entity<Driver>(e => e.HasKey(x => x.DriverId));

        b.Entity<DriverDevice>(e =>
        {
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.RegisteredAt).HasDefaultValueSql("now()");
        });

        b.Entity<DriverVerification>(e =>
        {
            e.HasKey(x => x.VerificationId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<DriverOtp>(e =>
        {
            e.HasKey(x => x.OtpId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<GatePass>(e =>
        {
            e.HasKey(x => x.GatePassId);
            e.Property(x => x.IssuedAt).HasDefaultValueSql("now()");
        });

        b.Entity<AppointmentEta>(e =>
        {
            e.HasKey(x => x.EtaId);
            e.Property(x => x.RecordedAt).HasDefaultValueSql("now()");
        });

        b.Entity<GeofenceEvent>(e => e.HasKey(x => x.GeofenceEventId));

        b.Entity<TerminalMap>(e =>
        {
            e.HasKey(x => x.TerminalId);
            e.Property(x => x.TerminalId).ValueGeneratedNever();
            e.Property(x => x.Roads).HasColumnType("jsonb");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<YardInstruction>(e =>
        {
            e.HasKey(x => x.InstructionId);
            e.Property(x => x.IssuedAt).HasDefaultValueSql("now()");
        });

        b.Entity<IntegrationEvent>(e =>
        {
            e.HasKey(x => x.EventId);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        // ---------- Booking ----------

        b.Entity<CargoAllocation>(e =>
        {
            e.HasKey(x => x.CargoAllocationId);
            e.Property(x => x.CargoType).HasUpperCaseConversion();
            e.Property(x => x.Status).HasUpperCaseConversion();
        });

        b.Entity<Appointment>(e =>
        {
            e.HasKey(x => x.AppointmentId);
            e.Property(x => x.Status).HasUpperCaseConversion();
            e.Property(x => x.TrafficLight).HasUpperCaseConversion();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Slot).WithMany().HasForeignKey(x => x.SlotId);
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
            e.HasOne(x => x.Truck).WithMany().HasForeignKey(x => x.TruckId);
            e.HasOne(x => x.Driver).WithMany().HasForeignKey(x => x.DriverId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.AppointmentId);
            e.HasMany(x => x.History).WithOne().HasForeignKey(h => h.AppointmentId);
        });

        b.Entity<AppointmentItem>(e =>
        {
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ValidationStatus).HasUpperCaseConversion();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId);
            e.HasOne(x => x.CargoAllocation).WithMany().HasForeignKey(x => x.CargoAllocationId);
            e.HasMany(x => x.ValidationResults).WithOne().HasForeignKey(v => v.ItemId);
        });

        b.Entity<ValidationResult>(e =>
        {
            e.HasKey(x => x.ValidationId);
            e.Property(x => x.CheckType).HasUpperCaseConversion();
            e.Property(x => x.Result).HasUpperCaseConversion();
        });

        b.Entity<AppointmentCharge>(e =>
        {
            e.HasKey(x => x.ChargeId);
            e.Property(x => x.Kind).HasUpperCaseConversion();
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<AppointmentStatusHistory>(e =>
        {
            e.HasKey(x => x.HistoryId);
            e.Property(x => x.FromStatus).HasConversion(new UpperSnakeEnumConverter<AppointmentStatus>());
            e.Property(x => x.ToStatus).HasUpperCaseConversion();
        });

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
    : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v)
{
}

/// <summary>
/// Stores enums as the upper-case strings used in the schema's CHECK constraints:
/// Adjust → "ADJUST", EnRoute → "EN_ROUTE", RejectedAtGate → "REJECTED_AT_GATE".
/// </summary>
internal sealed class UpperSnakeEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(v => EnumNames.ToDb(v), v => EnumNames.FromDb<TEnum>(v))
    where TEnum : struct, Enum
{
}

internal static class EnumNames
{
    public static string ToDb<TEnum>(TEnum value) where TEnum : struct, Enum =>
        YardsyncsDbContext.ToSnakeCase(value.ToString()).ToUpperInvariant();

    public static TEnum FromDb<TEnum>(string value) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(value.Replace("_", ""), ignoreCase: true);
}

internal static class EnumConversionExtensions
{
    public static PropertyBuilder<TEnum> HasUpperCaseConversion<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum =>
        property.HasConversion(new UpperSnakeEnumConverter<TEnum>());
}
