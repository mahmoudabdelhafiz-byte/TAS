using Tas.Application.Yard;

namespace Tas.Application.Tests;

public class RoadGraphTests
{
    private static GeoPoint P(double lon, double lat) => new(lon, lat);

    // A square block of roads ~110 m a side: west and east sides, a north road and a south road.
    //   N: (0,0.002)──(0.002,0.002)
    //       │              │
    //   S: (0,0)─────(0.002,0)
    private static readonly Road[] Square =
    {
        new(new[] { P(0, 0), P(-0.0005, 0.001), P(0, 0.002) }),     // west, two-way, bends out (longer)
        new(new[] { P(0.002, 0.002), P(0.002, 0) }, OneWay: true), // east, southbound only
        new(new[] { P(0, 0.002), P(0.002, 0.002) }),
        new(new[] { P(0, 0), P(0.002, 0) }),
    };

    [Fact]
    public void Roads_meet_at_shared_points_and_routes_take_the_shortest_way()
    {
        var graph = new RoadGraph(Square);
        Assert.Equal(5, graph.NodeCount);

        var (path, meters) = graph.Route(P(0, 0.0021), P(0.002, -0.0001));
        Assert.Equal(P(0, 0.002), path[0]);
        Assert.Equal(P(0.002, 0), path[^1]);
        Assert.Equal(3, path.Count); // north road then east road (southbound): ~440 m, shorter than round the west
        Assert.InRange(meters, 400, 480);
    }

    [Fact]
    public void One_way_roads_are_respected()
    {
        var graph = new RoadGraph(Square);

        // From the south-east corner to the north-east corner the east road can't be used northbound.
        var (path, meters) = graph.Route(P(0.002, 0), P(0.002, 0.002));
        Assert.Equal(new[] { P(0.002, 0), P(0, 0), P(-0.0005, 0.001), P(0, 0.002), P(0.002, 0.002) }, path.ToArray());
        Assert.InRange(meters, 650, 730);
    }

    [Fact]
    public void No_roads_or_no_way_through_gives_an_empty_route()
    {
        Assert.Empty(new RoadGraph(Array.Empty<Road>()).Route(P(0, 0), P(1, 1)).Path);
        var oneWay = new RoadGraph(new[] { new Road(new[] { P(0, 0), P(0.001, 0) }, OneWay: true) });
        Assert.Empty(oneWay.Route(P(0.001, 0), P(0, 0)).Path);
        Assert.Equal(2, oneWay.Route(P(0, 0), P(0.001, 0)).Path.Count);
    }

    [Fact]
    public void Centroid_of_a_closed_ring()
    {
        var c = RoadGraph.Centroid(new[] { P(0, 0), P(2, 0), P(2, 2), P(0, 2), P(0, 0) });
        Assert.Equal(P(1, 1), c);
    }
}
