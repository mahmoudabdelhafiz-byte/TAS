namespace Yardsyncs.Application.Yard;

/// <summary>A point as [longitude, latitude].</summary>
public readonly record struct GeoPoint(double Lon, double Lat)
{
    public double[] ToArray() => [Lon, Lat];

    /// <summary>Great-circle distance in metres.</summary>
    public double MetersTo(GeoPoint other)
    {
        const double r = 6_371_000;
        var dLat = (other.Lat - Lat) * Math.PI / 180;
        var dLon = (other.Lon - Lon) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Lat * Math.PI / 180) * Math.Cos(other.Lat * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

/// <summary>A road inside the terminal, as drawn on the terminal map.</summary>
public sealed record Road(IReadOnlyList<GeoPoint> Points, bool OneWay = false, string? Name = null);

/// <summary>
/// The terminal's internal roads as a graph: roads meet where they share a point (to ~10 cm). Public map services
/// don't know these roads, so routes inside the yard are found here (Dijkstra, respecting one-way roads).
/// </summary>
public sealed class RoadGraph
{
    private readonly List<GeoPoint> _nodes = [];
    private readonly Dictionary<(long, long), int> _index = [];
    private readonly List<List<(int To, double Meters)>> _edges = [];
    private readonly List<HashSet<int>> _neighbours = [];

    public RoadGraph(IEnumerable<Road> roads)
    {
        foreach (var road in roads)
        {
            for (var i = 1; i < road.Points.Count; i++)
            {
                var a = Node(road.Points[i - 1]);
                var b = Node(road.Points[i]);
                if (a == b)
                    continue;
                _neighbours[a].Add(b);
                _neighbours[b].Add(a);
                var meters = _nodes[a].MetersTo(_nodes[b]);
                _edges[a].Add((b, meters));
                if (!road.OneWay)
                    _edges[b].Add((a, meters));
            }
        }
    }

    public int NodeCount => _nodes.Count;

    /// <summary>Where roads meet or branch (three or more ways to go, whatever their direction): a driver has a choice.</summary>
    public bool IsJunction(GeoPoint p) =>
        _index.TryGetValue(((long)Math.Round(p.Lon * 1_000_000), (long)Math.Round(p.Lat * 1_000_000)), out var id) && _neighbours[id].Count >= 3;

    private int Node(GeoPoint p)
    {
        var key = ((long)Math.Round(p.Lon * 1_000_000), (long)Math.Round(p.Lat * 1_000_000));
        if (_index.TryGetValue(key, out var id))
            return id;
        id = _nodes.Count;
        _nodes.Add(p);
        _edges.Add([]);
        _neighbours.Add([]);
        _index[key] = id;
        return id;
    }

    /// <summary>The road point closest to a place (straight line), or null without roads.</summary>
    public int? Nearest(GeoPoint p)
    {
        int? best = null;
        var bestMeters = double.MaxValue;
        for (var i = 0; i < _nodes.Count; i++)
        {
            var m = _nodes[i].MetersTo(p);
            if (m < bestMeters)
            {
                bestMeters = m;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// The shortest drivable path between two places: from the road point nearest <paramref name="from"/> to the one
    /// nearest <paramref name="to"/>. Empty when there are no roads or no way through (e.g. against one-way roads).
    /// </summary>
    public (IReadOnlyList<GeoPoint> Path, double Meters) Route(GeoPoint from, GeoPoint to)
    {
        if (Nearest(from) is not { } start || Nearest(to) is not { } goal)
            return (Array.Empty<GeoPoint>(), 0);
        if (start == goal)
            return (new[] { _nodes[start] }, 0);

        var distance = Enumerable.Repeat(double.MaxValue, _nodes.Count).ToArray();
        var previous = Enumerable.Repeat(-1, _nodes.Count).ToArray();
        var queue = new PriorityQueue<int, double>();
        distance[start] = 0;
        queue.Enqueue(start, 0);

        while (queue.TryDequeue(out var node, out var d))
        {
            if (d > distance[node])
                continue;
            if (node == goal)
                break;
            foreach (var (next, meters) in _edges[node])
            {
                var candidate = d + meters;
                if (candidate < distance[next])
                {
                    distance[next] = candidate;
                    previous[next] = node;
                    queue.Enqueue(next, candidate);
                }
            }
        }

        if (previous[goal] < 0)
            return (Array.Empty<GeoPoint>(), 0);

        var path = new List<GeoPoint>();
        for (var n = goal; n >= 0; n = previous[n])
            path.Add(_nodes[n]);
        path.Reverse();
        return (path, Math.Round(distance[goal], 1));
    }

    /// <summary>Where a truck reaches an area: the middle of its outline (the route then ends at the closest road point).</summary>
    public static GeoPoint Centroid(IReadOnlyList<GeoPoint> ring)
    {
        var points = ring.Count > 1 && ring[0] == ring[^1] ? ring.Take(ring.Count - 1).ToList() : ring.ToList();
        return new GeoPoint(points.Average(p => p.Lon), points.Average(p => p.Lat));
    }
}
