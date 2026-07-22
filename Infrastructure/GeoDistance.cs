namespace CafePOS.Api.Infrastructure;

/// <summary>Straight-line (Haversine) distance between two lat/long points, in meters —
/// used only for the punch-in/out geofence check (AttendanceController), not for any
/// routing/driving-distance purpose.</summary>
public static class GeoDistance
{
    private const double EarthRadiusMeters = 6371000;

    public static double Meters(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        var phi1 = ToRadians((double)lat1);
        var phi2 = ToRadians((double)lat2);
        var deltaPhi = ToRadians((double)(lat2 - lat1));
        var deltaLambda = ToRadians((double)(lon2 - lon1));

        var a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2)
            + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
