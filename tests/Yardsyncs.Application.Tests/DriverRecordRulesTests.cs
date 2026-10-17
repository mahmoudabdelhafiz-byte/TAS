using Yardsyncs.Application.MasterData;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Application.Tests;

public class DriverRecordRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Points_count_for_the_period_unless_withdrawn_or_the_dispute_was_accepted()
    {
        Assert.True(DriverRecordRules.Counts(Now.AddDays(-179), null, null, 180, Now));
        Assert.False(DriverRecordRules.Counts(Now.AddDays(-181), null, null, 180, Now));
        Assert.False(DriverRecordRules.Counts(Now.AddDays(-1), Now, null, 180, Now));
        Assert.False(DriverRecordRules.Counts(Now.AddDays(-1), null, DisputeStatus.Accepted, 180, Now));
        Assert.True(DriverRecordRules.Counts(Now.AddDays(-1), null, DisputeStatus.Open, 180, Now));
        Assert.True(DriverRecordRules.Counts(Now.AddDays(-1), null, DisputeStatus.Rejected, 180, Now));
    }

    [Fact]
    public void Where_a_driver_stands_against_the_terminals_thresholds()
    {
        var defaults = TerminalSettings.Defaults; // warning at 6, no limit
        Assert.Equal(DriverStanding.Clear, DriverRecordRules.Standing(5, defaults));
        Assert.Equal(DriverStanding.Warning, DriverRecordRules.Standing(6, defaults));
        Assert.Equal(DriverStanding.Warning, DriverRecordRules.Standing(60, defaults));

        var strict = defaults with { DriverWarnPoints = 4, DriverLimitPoints = 8 };
        Assert.Equal(DriverStanding.OverLimit, DriverRecordRules.Standing(8, strict));
        Assert.Equal(DriverStanding.Clear, DriverRecordRules.Standing(100, defaults with { DriverWarnPoints = 0 }));

        Assert.Equal("Driver Abdullah has 9 penalty points at this terminal (limit 8): check with the control room.",
            DriverRecordRules.GateWarning("Abdullah", 9, DriverStanding.OverLimit, strict));
        Assert.Null(DriverRecordRules.GateWarning("Abdullah", 1, DriverStanding.Clear, strict));
    }

    [Fact]
    public void Entries_need_a_kind_sensible_points_and_a_note_for_other()
    {
        Assert.Null(DriverRecordRules.Check(new IncidentInput(IncidentKind.Speeding), Now));
        Assert.NotNull(DriverRecordRules.Check(new IncidentInput(IncidentKind.Speeding, Points: 11), Now));
        Assert.NotNull(DriverRecordRules.Check(new IncidentInput((IncidentKind)99), Now));
        Assert.NotNull(DriverRecordRules.Check(new IncidentInput(IncidentKind.Other), Now));
        Assert.Null(DriverRecordRules.Check(new IncidentInput(IncidentKind.Other, Note: "Smoking by the reefer stack"), Now));
        Assert.NotNull(DriverRecordRules.Check(new IncidentInput(IncidentKind.NoPpe, OccurredAt: Now.AddDays(-91)), Now));
        Assert.Equal(3, DriverRecordRules.DefaultPoints(IncidentKind.Speeding));
        Assert.Equal(5, DriverRecordRules.DefaultPoints(IncidentKind.Abusive));
    }
}
