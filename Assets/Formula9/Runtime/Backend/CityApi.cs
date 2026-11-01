using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    public sealed class CityBuilding
    {
        public string Id, Type, Name;
        public double HeightM;
        // Closed outlines in local metres: x east, y north of central Singapore.
        public List<List<(double X, double Y)>> Rings;
    }

    public sealed class CityRoad
    {
        public string Id, Class;
        public long FromNode, ToNode;
        public bool Oneway;
        // In each direction of a two-way road
        public int Lanes;
        public int SpeedKph;
        // The centreline in local metres: x east, y north of central Singapore.
        public List<(double X, double Y)> Points;
    }

    public sealed class CityTrafficModel
    {
        public string Id;
        public float LengthM, WidthM, HeightM;
    }

    public sealed class CityTrafficVehicle
    {
        public string Id, Model, Road, Color;
        public int Direction, Lane, SpeedKph;
        public float DistanceM;
    }

    public sealed class CityTrafficData
    {
        public Dictionary<string, CityTrafficModel> Models = new Dictionary<string, CityTrafficModel>();
        public List<CityTrafficVehicle> Vehicles = new List<CityTrafficVehicle>();
    }

    // /api/buildings and /api/roads: public map data, so no login is needed.
    public sealed class CityClient : BackendClientBase
    {
        public CityClient(IHttpTransport transport, Uri baseUrl = null) : base(transport, baseUrl) { }

        public async Task<List<CityBuilding>> GetBuildingsAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", "/api/buildings/" + Uri.EscapeDataString(area));
            request.TimeoutSeconds = 30;
            var reply = await SendAsync(request, cancellation);
            try
            {
                return (JObject.Parse(reply)["buildings"] as JArray ?? throw new ContractException("Building reply has no buildings"))
                    .Select(b => new CityBuilding
                    {
                        Id = (string)b["id"], Type = (string)b["type"], Name = (string)b["name"], HeightM = (double)b["height_m"],
                        Rings = b["rings_m"].Select(ring => ring.Select(p => ((double)p[0], (double)p[1])).ToList()).ToList(),
                    }).ToList();
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid building reply: " + error.Message);
            }
        }

        public async Task<List<CityRoad>> GetRoadsAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", "/api/roads/" + Uri.EscapeDataString(area));
            request.TimeoutSeconds = 30;
            var reply = await SendAsync(request, cancellation);
            try
            {
                return (JObject.Parse(reply)["roads"] as JArray ?? throw new ContractException("Road reply has no roads"))
                    .Select(r => new CityRoad
                    {
                        Id = (string)r["id"], Class = (string)r["class"], FromNode = (long)r["from_node"], ToNode = (long)r["to_node"],
                        Oneway = (bool)r["oneway"], Lanes = (int)r["lanes"], SpeedKph = (int?)r["speed_kph"] ?? 40,
                        Points = r["points_m"].Select(p => ((double)p[0], (double)p[1])).ToList(),
                    }).ToList();
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid road reply: " + error.Message);
            }
        }

        public async Task<CityTrafficData> GetTrafficAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", "/api/traffic/" + Uri.EscapeDataString(area));
            request.TimeoutSeconds = 30;
            var reply = await SendAsync(request, cancellation);
            try
            {
                var root = JObject.Parse(reply);
                var modelsToken = root["models"] as JObject ?? throw new ContractException("Traffic reply has no models");
                var vehiclesToken = root["vehicles"] as JArray ?? throw new ContractException("Traffic reply has no vehicles");
                var traffic = new CityTrafficData();
                foreach (var entry in modelsToken.Properties())
                {
                    traffic.Models[entry.Name] = new CityTrafficModel
                    {
                        Id = entry.Name,
                        LengthM = (float)entry.Value["length_m"],
                        WidthM = (float)entry.Value["width_m"],
                        HeightM = (float)entry.Value["height_m"]
                    };
                }
                traffic.Vehicles = vehiclesToken.Select(v => new CityTrafficVehicle
                {
                    Id = (string)v["id"],
                    Model = (string)v["model"],
                    Color = (string)v["color"],
                    Road = (string)v["road"],
                    Direction = (int?)v["direction"] ?? 1,
                    Lane = (int?)v["lane"] ?? 0,
                    DistanceM = (float?)v["distance_m"] ?? 0,
                    SpeedKph = (int?)v["speed_kph"] ?? 40,
                }).ToList();
                return traffic;
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid traffic reply: " + error.Message);
            }
        }
    }
}
