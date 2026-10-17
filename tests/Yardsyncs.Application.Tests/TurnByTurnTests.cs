using Yardsyncs.Application.Yard;

namespace Yardsyncs.Application.Tests;

public class TurnByTurnTests
{
    private static GeoPoint P(double lon, double lat) => new(lon, lat);

    // A main road running north with a crossing road at its middle (a junction), and a bend further north:
    //
    //            (0,0.004)
    //              │  bend
    //   (-0.001,0.002)──(0,0.002)──(0.001,0.002)
    //              │
    //            (0,0)
    private static readonly Road[] Roads =
    {
        new(new[] { P(0, 0), P(0, 0.001), P(0, 0.002), P(0, 0.003), P(0.0003, 0.004) }),
        new(new[] { P(-0.001, 0.002), P(-0.0005, 0.002), P(0, 0.002), P(0.0005, 0.002), P(0.001, 0.002) }),
    };

    private static readonly Landmark[] Blocks =
    {
        new("A1", true, P(0.0003, 0.0023)),
        new("Truck parking", false, P(-0.003, 0.002)),
    };

    [Fact]
    public void A_turn_is_found_at_the_junction_and_named_by_the_block_beside_it()
    {
        var graph = new RoadGraph(Roads);
        var (path, _) = graph.Route(P(0, 0), P(0.001, 0.002));

        var turn = Assert.Single(TurnByTurn.Steps(path, graph.IsJunction, Blocks));
        Assert.Equal(TurnKind.Right, turn.Turn);
        Assert.Equal(new[] { 0.0, 0.002 }, turn.Point);
        Assert.InRange(turn.AtMeters, 215, 230); // ~222 m north of the start
        Assert.Equal(("A1", true), (turn.Near, turn.NearIsBlock));

        var (west, _) = graph.Route(P(0, 0), P(-0.001, 0.002));
        Assert.Equal(TurnKind.Left, Assert.Single(TurnByTurn.Steps(west, graph.IsJunction, Blocks)).Turn);
    }

    [Fact]
    public void Straight_on_and_bends_of_one_road_are_not_turns()
    {
        var graph = new RoadGraph(Roads);
        var (path, _) = graph.Route(P(0, 0), P(0.0003, 0.004));
        Assert.Empty(TurnByTurn.Steps(path, graph.IsJunction, Blocks));
        Assert.False(graph.IsJunction(P(0, 0.003)));
        Assert.True(graph.IsJunction(P(0, 0.002)));
    }

    [Fact]
    public void Junctions_far_from_any_landmark_have_no_name()
    {
        var graph = new RoadGraph(Roads);
        var (path, _) = graph.Route(P(0, 0), P(-0.001, 0.002));
        Assert.Null(Assert.Single(TurnByTurn.Steps(path, graph.IsJunction, [new Landmark("Far", true, P(0.01, 0.01))])).Near);
    }

    [Theory]
    [InlineData(10, null)]
    [InlineData(-29, null)]
    [InlineData(45, TurnKind.SlightRight)]
    [InlineData(-45, TurnKind.SlightLeft)]
    [InlineData(90, TurnKind.Right)]
    [InlineData(-90, TurnKind.Left)]
    [InlineData(150, TurnKind.SharpRight)]
    [InlineData(-150, TurnKind.SharpLeft)]
    [InlineData(175, TurnKind.UTurn)]
    public void Changes_of_heading_become_turns(double change, TurnKind? kind) => Assert.Equal(kind, TurnByTurn.Kind(change));

    [Fact]
    public void Bearings_are_clockwise_from_north() =>
        Assert.Equal(90, TurnByTurn.Bearing(P(0, 0), P(0.001, 0)), 1);
}
