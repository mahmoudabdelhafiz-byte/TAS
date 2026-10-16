using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

public class InspectionRulesTests
{
    [Fact]
    public void The_seal_seen_is_checked_against_the_declared_one()
    {
        Assert.Equal(SealStatus.Ok, InspectionRules.Seal("ML-449 120", "ml449120"));
        Assert.Equal(SealStatus.Mismatch, InspectionRules.Seal("ML449120", "ML449121"));
        Assert.Equal(SealStatus.Ok, InspectionRules.Seal(null, "ML449121"));
        Assert.Equal(SealStatus.NotChecked, InspectionRules.Seal("ML449120", " "));
        Assert.Equal(SealStatus.Broken, InspectionRules.Seal("ML449120", "ML449120", broken: true));
        Assert.Equal(SealStatus.Missing, InspectionRules.Seal("ML449120", null, missing: true));
        Assert.Null(InspectionRules.NormalizeSeal(" - "));
    }

    [Fact]
    public void Damage_found_at_gate_out_but_not_at_gate_in_is_new()
    {
        var found = InspectionRules.NewAtGateOut(
        [
            ("MSCU1234566", InspectionStage.In, [(DamageSide.Door, DamageKind.Dent, DamageSeverity.Minor)]),
            ("MSCU1234566", InspectionStage.Out, [(DamageSide.Door, DamageKind.Dent, DamageSeverity.Minor), (DamageSide.Left, DamageKind.Hole, DamageSeverity.Major)]),
            ("CSQU3054383", InspectionStage.Out, [(DamageSide.Door, DamageKind.Dent, DamageSeverity.Minor)]),
        ]);

        Assert.Equal(2, found.Count);
        Assert.Contains(new NewDamage("MSCU1234566", DamageSide.Left, DamageKind.Hole, DamageSeverity.Major), found);
        Assert.Contains(new NewDamage("CSQU3054383", DamageSide.Door, DamageKind.Dent, DamageSeverity.Minor), found);
    }

    [Fact]
    public void A_camera_s_side_is_understood_when_it_can_be()
    {
        Assert.Equal(DamageSide.Left, InspectionRules.SideFrom("left side"));
        Assert.Equal(DamageSide.Rear, InspectionRules.SideFrom("BACK"));
        Assert.Equal(DamageSide.Underside, InspectionRules.SideFrom("bottom"));
        Assert.Null(InspectionRules.SideFrom("corner casting 3"));
        Assert.Null(InspectionRules.SideFrom(null));
    }

    [Fact]
    public void An_inspection_as_entered_is_checked()
    {
        Assert.Empty(InspectionRules.Validate(new InspectionInput(InspectionStage.In, "MSCU1234566", "ML1", Damage: [new(DamageSide.Door, DamageKind.Dent)])));
        Assert.NotEmpty(InspectionRules.Validate(new InspectionInput(InspectionStage.In, SealSeen: new string('9', 21))));
        Assert.NotEmpty(InspectionRules.Validate(new InspectionInput(InspectionStage.Out, Damage: [new(null, (DamageKind)99)])));
        Assert.NotEmpty(InspectionRules.Validate(new InspectionInput(InspectionStage.Out, Damage: Enumerable.Repeat(new DamageEntryInput(null, DamageKind.Rust), 31).ToList())));
    }
}
