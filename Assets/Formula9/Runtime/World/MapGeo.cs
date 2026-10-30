using System;

namespace Formula9.World
{
    public readonly struct LatLon
    {
        public readonly double Latitude, Longitude;
        public LatLon(double latitude, double longitude) { Latitude = latitude; Longitude = longitude; }
    }

    // Ties the fleet map's local metres to real latitude/longitude, and lat/lon to the Web Mercator
    // tiles OpenStreetMap serves. Port of the Qt client's MapGeo.
    //
    // Local metres use the backend's projection (scene_repository._project): x east and y north of
    // central Singapore, the frame the real-streets scenario is built in. Over Singapore's 50 km
    // this differs from Mercator by far less than a pixel, so tiles are placed by their corners and
    // drawn as plain rectangles.
    public static class MapGeo
    {
        public const double SingaporeLatitude = 1.3521, SingaporeLongitude = 103.8198;
        public const int MaxTileZoom = 19;
        private const double MetresPerDegreeLatitude = 110574.0;
        private const double MetresPerDegreeLongitudeAtEquator = 111320.0;
        // Metres per pixel of a 256 px tile at zoom 0, on the equator
        private const double MercatorMetresPerPixelZoom0 = 156543.03392;
        private const double MaxMercatorLatitude = 85.05112878;
        private const double DegreesToRadians = Math.PI / 180;

        private static double MetresPerDegreeLongitude => MetresPerDegreeLongitudeAtEquator * Math.Cos(SingaporeLatitude * DegreesToRadians);

        public static (double X, double Y) ToLocal(LatLon point) => (
            (point.Longitude - SingaporeLongitude) * MetresPerDegreeLongitude,
            (point.Latitude - SingaporeLatitude) * MetresPerDegreeLatitude);

        public static LatLon ToLatLon(double x, double y) => new LatLon(
            SingaporeLatitude + y / MetresPerDegreeLatitude,
            SingaporeLongitude + x / MetresPerDegreeLongitude);

        // Fractional tile coordinates at a zoom: x grows east, y grows south
        public static (double X, double Y) TileAt(LatLon point, int zoom)
        {
            var scale = Math.Pow(2, zoom);
            var latitude = Math.Max(-MaxMercatorLatitude, Math.Min(MaxMercatorLatitude, point.Latitude)) * DegreesToRadians;
            return ((point.Longitude + 180) / 360 * scale,
                (1 - Math.Log(Math.Tan(latitude) + 1 / Math.Cos(latitude)) / Math.PI) / 2 * scale);
        }

        // The north-west corner of tile (x, y); x + 1, y + 1 gives the south-east corner
        public static LatLon TileCorner(double x, double y, int zoom)
        {
            var scale = Math.Pow(2, zoom);
            var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / scale)));
            return new LatLon(latitude / DegreesToRadians, x / scale * 360 - 180);
        }

        // The tile zoom whose pixels best match the screen's physical pixels at this scale
        public static int TileZoomFor(double metresPerPoint, double latitude, double pixelsPerPoint)
        {
            var metresPerPixel = metresPerPoint / Math.Max(pixelsPerPoint, 1);
            var zoom = Math.Log(MercatorMetresPerPixelZoom0 * Math.Cos(latitude * DegreesToRadians) / metresPerPixel, 2);
            return Math.Max(0, Math.Min(MaxTileZoom, (int)Math.Round(zoom)));
        }
    }
}
