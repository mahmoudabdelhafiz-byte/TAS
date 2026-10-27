using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Booking.Bulk;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Tests;

/// <summary>RoRo: vehicles booked by VIN, car carriers, drive-away visits.</summary>
public class RoroTests
{
    private static readonly Dictionary<string, TransactionCategory> Categories = new()
    {
        ["IMP_PICKUP"] = new() { CategoryId = 1, Code = "IMP_PICKUP", Mode = CategoryMode.Container },
        ["RORO_PICKUP"] = new() { CategoryId = 2, Code = "RORO_PICKUP", Mode = CategoryMode.Vehicle },
        ["RORO_DRIVEAWAY"] = new() { CategoryId = 3, Code = "RORO_DRIVEAWAY", Mode = CategoryMode.Vehicle, DriveAway = true },
    };

    private static readonly string[] Vins =
        ["JTDBR32E730012457", "JTDBR32E730012461", "KMHCT41BAEU123458", "KMHCT41BAEU123462", "MALA841CAFM123455",
         "JN1TBNT30Z0123456", "JN1TBNT30Z0123460", "WVWZZZAWZKU012345", "WDD2050421R123456"];

    private static BookingRequest Request(int? truckId, params BookingItemRequest[] items) => new(1, 1, 1, truckId, null, items);
    private static BookingItemRequest Car(string vin, string? bl = null) => new("RORO_PICKUP", Vin: vin, CargoRef: bl, VehicleDesc: "Toyota Corolla");

    [Theory]
    [InlineData(" jtdbr32e-7300 12457 ", true)]
    [InlineData("JTDBR32E73001245", false)]   // 16 characters
    [InlineData("JTDBR32E73001245O", false)]  // O is never used
    [InlineData("JTDIR32E730012457", false)]  // nor I
    public void A_vin_is_17_letters_and_digits_without_i_o_or_q(string value, bool valid) =>
        Assert.Equal(valid, Vin.HasValidFormat(Vin.Normalize(value)));

    [Fact]
    public void A_car_carrier_takes_several_vehicles_by_vin_and_the_bl_is_the_release_reference()
    {
        var (items, errors) = BookingRequestValidator.Validate(
            Request(5, Car("jtdbr32e730012457", "nyks260415ab"), Car(Vins[1]), Car(Vins[2]), Car(Vins[3]), Car(Vins[4]), Car(Vins[5])), Categories);

        Assert.Empty(errors);
        Assert.Equal(6, items.Count);
        Assert.Equal("JTDBR32E730012457", items[0].Vin);
        Assert.Equal("NYKS260415AB", items[0].EdoRef);
        Assert.Null(items[0].CargoRef);
        Assert.Equal("Toyota Corolla", items[0].VehicleDesc);
    }

    [Fact]
    public void The_terminal_limits_vehicles_per_truck()
    {
        var nine = Vins.Select(v => Car(v)).ToArray();
        Assert.Contains("One truck takes at most 8 vehicles.", BookingRequestValidator.Validate(Request(5, nine), Categories).Errors);
        Assert.Empty(BookingRequestValidator.Validate(Request(5, nine), Categories, maxVehicles: 10).Errors);
        Assert.Contains("One truck takes at most 4 vehicles.", BookingRequestValidator.Validate(Request(5, nine[..5]), Categories, maxVehicles: 4).Errors);
    }

    [Fact]
    public void Vehicles_dont_share_a_visit_with_containers_and_need_a_vin()
    {
        var mixed = BookingRequestValidator.Validate(Request(5, Car(Vins[0]), new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566")), Categories);
        Assert.Contains("A visit moves either vehicles or containers / cargo, not both.", mixed.Errors);

        var noVin = BookingRequestValidator.Validate(Request(5, new BookingItemRequest("RORO_PICKUP", CargoRef: "BL1")), Categories);
        Assert.Contains("Item 1: RORO_PICKUP needs the vehicle's VIN (chassis number).", noVin.Errors);

        var twice = BookingRequestValidator.Validate(Request(5, Car(Vins[0]), Car(Vins[0].ToLowerInvariant())), Categories);
        Assert.Contains($"Item 2: vehicle {Vins[0]} appears twice.", twice.Errors);

        var vinOnContainer = BookingRequestValidator.Validate(Request(5, new BookingItemRequest("IMP_PICKUP", ContainerNo: "MSCU1234566", Vin: Vins[0])), Categories);
        Assert.Contains("Item 1: IMP_PICKUP is not a vehicle category; remove the VIN.", vinOnContainer.Errors);
    }

    [Fact]
    public void A_drive_away_visit_is_one_vehicle_and_no_truck()
    {
        BookingItemRequest Drive(string vin) => new("RORO_DRIVEAWAY", Vin: vin);
        Assert.Empty(BookingRequestValidator.Validate(Request(null, Drive(Vins[0])), Categories).Errors);
        Assert.Contains("A drive-away visit has no truck: the driver drives the vehicle.",
            BookingRequestValidator.Validate(Request(5, Drive(Vins[0])), Categories).Errors);
        Assert.Contains("A drive-away visit is one vehicle, driven by its driver.",
            BookingRequestValidator.Validate(Request(null, Drive(Vins[0]), Drive(Vins[1])), Categories).Errors);
    }

    [Fact]
    public void The_bulk_file_reads_vins_by_name_or_chassis_and_groups_a_car_carrier()
    {
        var table = new List<IReadOnlyList<string>> { new[] { "visit", "date", "time", "category", "Chassis No", "Make Model", "truck_plate" } };
        table.AddRange(Vins.Take(6).Select(v => (IReadOnlyList<string>)new[] { "C1", "2026-10-20", "10:00", "RORO_PICKUP", v, "Toyota Corolla", "3307 KSA" }));
        table.Add(new[] { "", "2026-10-20", "11:00", "RORO_PICKUP", Vins[0], "", "" });

        var (rows, parseErrors) = BulkSheet.Parse(table);
        Assert.Empty(parseErrors);
        Assert.Equal(Vins[0], rows[0].Vin);
        Assert.Equal("Toyota Corolla", rows[0].VehicleDesc);

        var (visits, errors) = BulkSheet.Group(rows);
        var visit = Assert.Single(visits);
        Assert.Equal(6, visit.Rows.Count);                         // more than 4: a car carrier
        Assert.Contains(errors, e => e.Row == 8 && e.Message == $"vehicle {Vins[0]} is already on row 2.");
    }

    [Fact]
    public void At_the_exit_vehicles_are_checked_by_vin()
    {
        ExitItem Out(string vin) => new("RORO_PICKUP", CategoryMode.Vehicle, CategoryFlow.Out, vin);

        Assert.Equal(ExitCheckStatus.Ok, ExitRules.Check([Out(Vins[0]), Out(Vins[1])], [Vins[0], Vins[1]]).Status);

        var check = ExitRules.Check([Out(Vins[0]), Out(Vins[1])], [Vins[0], Vins[2]]);
        Assert.Equal(ExitCheckStatus.Problem, check.Status);
        Assert.Equal(new[] { (ExitProblemKind.NotOnTruck, Vins[1]), (ExitProblemKind.NotBooked, Vins[2]) },
            check.Problems.Select(p => (p.Kind, p.ContainerNo)));
    }
}
