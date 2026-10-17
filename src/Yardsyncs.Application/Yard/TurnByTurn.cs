namespace Yardsyncs.Application.Yard;

public enum TurnKind
{
    SlightLeft,
    Left,
    SharpLeft,
    SlightRight,
    Right,
    SharpRight,
    UTurn,
}

/// <summary>A turn on the way to a stop, for prompts like "in 50 metres turn left at block A1".</summary>
/// <param name="Point">The junction, [longitude, latitude].</param>
/// <param name="AtMeters">How far along the drive it comes.</param>
/// <param name="Near">The block (or other named area) beside the junction, to say where; null when none is close.</param>
/// <param name="NearIsBlock">Near is a yard block's code (said as "block A1"), not an area's name.</param>
public sealed record TurnStep(double[] Point, TurnKind Turn, double AtMeters, string? Near = null, bool NearIsBlock = false);

/// <summary>A place drivers know, to name junctions by: a block code or an area's name, and its middle.</summary>
public sealed record Landmark(string Name, bool IsBlock, GeoPoint Center);

/// <summary>
/// The turns along a route: at each junction (where roads meet, so the driver has a choice) the change of heading
/// between the road coming in and the road going out. Bends of a single road and going straight on are not turns.
/// </summary>
public static class TurnByTurn
{
    /// <summary>Headings are taken over this distance either side of a junction, so a drawn curve doesn't count.</summary>
    public const double LookMeters = 15;

    /// <summary>A landmark further than this from the junction is not used to name it.</summary>
    public const double NearMeters = 60;

    public static IReadOnlyList<TurnStep> Steps(IReadOnlyList<GeoPoint> path, Func<GeoPoint, bool> isJunction, IReadOnlyList<Landmark> landmarks)
    {
        var steps = new List<TurnStep>();
        if (path.Count < 3)
            return steps;

        var along = new double[path.Count];
        for (var i = 1; i < path.Count; i++)
            along[i] = along[i - 1] + path[i - 1].MetersTo(path[i]);

        for (var i = 1; i < path.Count - 1; i++)
        {
            if (!isJunction(path[i]))
                continue;
            var back = i - 1;
            while (back > 0 && along[i] - along[back] < LookMeters)
                back--;
            var ahead = i + 1;
            while (ahead < path.Count - 1 && along[ahead] - along[i] < LookMeters)
                ahead++;

            var change = Normalise(Bearing(path[i], path[ahead]) - Bearing(path[back], path[i]));
            if (Kind(change) is not { } kind)
                continue;

            var near = landmarks
                .Select(l => (Landmark: l, Meters: l.Center.MetersTo(path[i])))
                .Where(x => x.Meters <= NearMeters)
                .OrderBy(x => x.Meters)
                .Select(x => x.Landmark)
                .FirstOrDefault();
            steps.Add(new TurnStep(path[i].ToArray(), kind, Math.Round(along[i], 1), near?.Name, near?.IsBlock ?? false));
        }
        return steps;
    }

    /// <summary>The turn for a change of heading in degrees (positive = to the right); null when going straight on.</summary>
    public static TurnKind? Kind(double change)
    {
        var size = Math.Abs(change);
        if (size < 30)
            return null;
        if (size >= 165)
            return TurnKind.UTurn;
        var right = change > 0;
        return size switch
        {
            < 60 => right ? TurnKind.SlightRight : TurnKind.SlightLeft,
            < 135 => right ? TurnKind.Right : TurnKind.Left,
            _ => right ? TurnKind.SharpRight : TurnKind.SharpLeft,
        };
    }

    /// <summary>Compass heading from one point to another, degrees clockwise from north.</summary>
    public static double Bearing(GeoPoint from, GeoPoint to)
    {
        var lat1 = from.Lat * Math.PI / 180;
        var lat2 = to.Lat * Math.PI / 180;
        var dLon = (to.Lon - from.Lon) * Math.PI / 180;
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static double Normalise(double degrees)
    {
        var d = degrees % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }
}
