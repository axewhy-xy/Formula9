using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.UI;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public class RealMapTests
    {
        private sealed class FakeTransport : IHttpTransport
        {
            public readonly List<HttpRequest> Sent = new List<HttpRequest>();
            public Func<HttpRequest, HttpResponse> Reply;

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                Sent.Add(request);
                return Task.FromResult(Reply(request));
            }
        }

        private static byte[] Png()
        {
            var texture = new Texture2D(2, 2);
            try { return texture.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void LocalMetresMatchTheBackendProjectionAndRoundTrip()
        {
            // scene_repository._project: (lon - lon0) * 111320 * cos(lat0), (lat - lat0) * 110574
            var east = MapGeo.ToLocal(new LatLon(MapGeo.SingaporeLatitude, 103.8298));
            Assert.AreEqual(0.01 * 111320.0 * Math.Cos(MapGeo.SingaporeLatitude * Math.PI / 180), east.X, 1e-6);
            Assert.AreEqual(0, east.Y, 1e-9);
            Assert.AreEqual(0.01 * 110574.0, MapGeo.ToLocal(new LatLon(1.3621, MapGeo.SingaporeLongitude)).Y, 1e-6);
            var again = MapGeo.ToLocal(MapGeo.ToLatLon(2500, -4000));
            Assert.AreEqual(2500, again.X, 1e-6);
            Assert.AreEqual(-4000, again.Y, 1e-6);
        }

        [Test]
        public void TilesAreTheStandardWebMercatorGrid()
        {
            // Central Singapore is OSM tile 1614/1016 at zoom 11
            var tile = MapGeo.TileAt(new LatLon(1.3521, 103.8198), 11);
            Assert.AreEqual(1614, (int)Math.Floor(tile.X));
            Assert.AreEqual(1016, (int)Math.Floor(tile.Y));
            var corner = MapGeo.TileCorner(tile.X, tile.Y, 11);
            Assert.AreEqual(1.3521, corner.Latitude, 1e-9);
            Assert.AreEqual(103.8198, corner.Longitude, 1e-9);
            // The whole island in a window: about 60 m a pixel is zoom 11; street level is the maximum
            Assert.AreEqual(11, MapGeo.TileZoomFor(60, 1.35, 1));
            Assert.AreEqual(12, MapGeo.TileZoomFor(60, 1.35, 2), "a HiDPI screen gets tiles one level finer");
            Assert.AreEqual(MapGeo.MaxTileZoom, MapGeo.TileZoomFor(0.02, 1.35, 1));
        }

        [Test]
        public async Task BusStopEntriesWithoutCodeOrCoordinatesAreSkipped()
        {
            var transport = new FakeTransport
            {
                Reply = _ => new HttpResponse
                {
                    Status = 200,
                    Body = @"{""stops"": [
                        {""code"": ""01012"", ""description"": ""Hotel Grand Pacific"", ""road_name"": ""Victoria St"", ""latitude"": 1.29685, ""longitude"": 103.85254},
                        {""code"": """", ""description"": ""No code"", ""latitude"": 1.3, ""longitude"": 103.8},
                        {""code"": ""01013"", ""description"": ""No position"", ""latitude"": null, ""longitude"": 103.8}
                    ]}",
                },
            };
            var stops = await new TransitClient(transport, new Uri("http://backend.test")).GetBusStopsAsync(1.25, 103.8, 1.3, 103.9);
            Assert.AreEqual(1, stops.Count);
            Assert.AreEqual("01012", stops[0].Code);
            Assert.AreEqual("Hotel Grand Pacific", stops[0].Name);
            Assert.AreEqual("Victoria St", stops[0].RoadName);
            Assert.AreEqual(103.85254, stops[0].Longitude);
            StringAssert.Contains("/api/transit/bus-stops?south=1.250000&west=103.800000&north=1.300000&east=103.900000&limit=2000", transport.Sent[0].Url.ToString());
            Assert.IsFalse(transport.Sent[0].Headers.ContainsKey("Authorization"));
        }

        [TestCase("{\"detail\": \"No bus data\"}")]
        [TestCase("{\"error\": {\"code\": \"UNAVAILABLE\", \"message\": \"No bus data\"}}")]
        public void BusStopsReportWhenTheDataHasNotBeenImported(string body)
        {
            var transport = new FakeTransport { Reply = _ => new HttpResponse { Status = 503, Body = body } };
            var client = new TransitClient(transport, new Uri("http://backend.test"));
            Assert.ThrowsAsync<TransitUnavailableException>(() => client.GetBusStopsAsync(1.25, 103.8, 1.3, 103.9));
        }

        [Test]
        public void ABusStopReplyWithoutStopsIsRefused()
        {
            var transport = new FakeTransport { Reply = _ => new HttpResponse { Status = 200, Body = "{}" } };
            var client = new TransitClient(transport, new Uri("http://backend.test"));
            Assert.ThrowsAsync<ContractException>(() => client.GetBusStopsAsync(1.25, 103.8, 1.3, 103.9));
        }

        [Test]
        public void TilesAreFetchedOnceIdentifiedAndKeptInMemory()
        {
            var transport = new FakeTransport { Reply = _ => new HttpResponse { Status = 200, Data = Png() } };
            var tiles = new MapTiles(transport, "https://tiles.test/{z}/{x}/{y}.png");
            try
            {
                var loaded = 0;
                tiles.TileLoaded += () => loaded++;
                Assert.IsNull(tiles.Cached(11, 1614, 1016), "looking in memory never queues a download");
                Assert.IsEmpty(transport.Sent);
                Assert.IsNotNull(tiles.Tile(11, 1614, 1016));
                Assert.IsNotNull(tiles.Tile(11, 1614, 1016));
                Assert.AreEqual(1, transport.Sent.Count);
                Assert.AreEqual(1, loaded);
                Assert.AreEqual("https://tiles.test/11/1614/1016.png", transport.Sent[0].Url.ToString());
                StringAssert.StartsWith("Formula9-FleetMap", transport.Sent[0].Headers["User-Agent"]);
            }
            finally { tiles.Dispose(); }
        }

        [Test]
        public void AFailedTileIsNotAskedForAgainStraightAway()
        {
            var transport = new FakeTransport { Reply = _ => new HttpResponse { Status = 500 } };
            var tiles = new MapTiles(transport, "https://tiles.test/{z}/{x}/{y}.png");
            try
            {
                Assert.IsNull(tiles.Tile(11, 1614, 1016));
                Assert.IsNull(tiles.Tile(11, 1614, 1016));
                Assert.AreEqual(1, transport.Sent.Count);
            }
            finally { tiles.Dispose(); }
        }

        [Test]
        public void TheBasemapCreditsOpenStreetMapAndCanBeTurnedOff()
        {
            var before = Environment.GetEnvironmentVariable("FORMULA9_TILE_URL");
            var transport = new FakeTransport();
            try
            {
                Environment.SetEnvironmentVariable("FORMULA9_TILE_URL", "none");
                Assert.IsNull(MapTiles.FromEnvironment(transport));
                Environment.SetEnvironmentVariable("FORMULA9_TILE_URL", "https://tiles.test/{z}/{x}/{y}.png");
                var tiles = MapTiles.FromEnvironment(transport);
                try { Assert.AreEqual("© OpenStreetMap contributors · tiles: tiles.test", tiles.Attribution); }
                finally { tiles.Dispose(); }
            }
            finally { Environment.SetEnvironmentVariable("FORMULA9_TILE_URL", before); }
        }

        [Test]
        public void TheNextStopIsTheFirstOneAheadAndWrapsAroundTheLoop()
        {
            Position At(double x) => new Position { XM = x, YM = 0 };
            var route = new RouteRevision
            {
                EdgePathsM = new List<List<Position>> { new List<Position> { At(0), At(100) }, new List<Position> { At(100), At(200) } },
                StopsM = new List<RouteStop> { new RouteStop { StopId = "b", Position = At(150) }, new RouteStop { StopId = "a", Position = At(50) } },
            };
            Assert.AreEqual("a", TrainingPresenter.NextStop(route, 10, 0));
            Assert.AreEqual("a", TrainingPresenter.NextStop(route, 50.5, 0), "still reaching a stop it is at");
            Assert.AreEqual("b", TrainingPresenter.NextStop(route, 60, 0));
            Assert.AreEqual("a", TrainingPresenter.NextStop(route, 190, 0));
        }

        [TestCase("IN_SERVICE", "Moving")]
        [TestCase("AWAITING_GUIDANCE", "Awaiting guidance")]
        [TestCase("SOMETHING_NEW", "SOMETHING_NEW")]
        public void StatusesAreReadableAndUnknownOnesShownAsSent(string state, string expected) =>
            Assert.AreEqual(expected, TrainingPresenter.StatusText(state));

        [Test]
        public void FleetRowsShowTheIncidentOnEachShuttle()
        {
            Incident Open(string id, long tick) => new Incident { IncidentId = id, VehicleId = "shuttle-01", State = "OPTIONS_READY", DetectedTick = tick };
            var snapshot = new FullSnapshot
            {
                Vehicles = new List<FleetVehicle> { new FleetVehicle { VehicleId = "shuttle-01", ServiceState = "IN_SERVICE", BatteryKwh = 28, BatteryCapacityKwh = 40, Provenance = new Provenance { Kind = "SIMULATED" } } },
                Incidents = new List<Incident> { Open("incident-1", 10), Open("incident-2", 20) },
                Routes = new List<RouteRevision>(),
            };
            Assert.AreEqual("! Incident incident-2 · Options ready (+1 more)", TrainingPresenter.Fleet(snapshot)[0].Incident);
            Assert.AreEqual("! Incident incident-1 · Options ready (+1 more) · selected", TrainingPresenter.Fleet(snapshot, "incident-1")[0].Incident);
            snapshot.Incidents.ForEach(i => i.State = "RESOLVED");
            Assert.IsNull(TrainingPresenter.Fleet(snapshot)[0].Incident);
        }
    }
}
