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

    public sealed class CitySignalTrafficLight
    {
        public string Id { get; set; }
        public string Junction { get; set; }
        public int Phase { get; set; }
        public string Approach { get; set; }
        public double TrafficHeadingDeg { get; set; }
        public double FacingHeadingDeg { get; set; }
        public string DirectionSource { get; set; }
        public string TypeCode { get; set; }
        public double BearingDeg { get; set; }
        public string RoadCode { get; set; }
        public double XM { get; set; }
        public double YM { get; set; }
        public double OffsetS { get; set; }

        public double GL_duration { get; set; } //green light duration
        public double YL_duration { get; set; } //yellow light duration
        public double RL_duration { get; set; } //red light duration
        public double Cycle_duration { get; set; } //traffic light cycle duration
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

    public enum SignalColor { Green, Yellow, Red }

    // The backend's made-up signal cycle (src.traffic.signal_cycle), which any client may work out for itself.
    public sealed class CitySignalTiming
    {
        public int GreenS = 30, YellowS = 3, AllRedS = 2, Phases = 2;

        public SignalColor ColorAt(double timeS, int offsetS, int phase)
        {
            var into = IntoCycleS(timeS, offsetS, phase);
            return into < GreenS ? SignalColor.Green : into < GreenS + YellowS ? SignalColor.Yellow : SignalColor.Red;
        }

        // Seconds of red still to run for the approach; 0 when it is not red
        public double RedRemainingS(double timeS, int offsetS, int phase)
        {
            var into = IntoCycleS(timeS, offsetS, phase);
            return into < GreenS + YellowS ? 0 : Phases * (GreenS + YellowS + AllRedS) - into;
        }

        // How far into the approach's own cycle it is, where the green starts at 0
        private double IntoCycleS(double timeS, int offsetS, int phase)
        {
            var phaseS = GreenS + YellowS + AllRedS;
            var cycleS = Phases * phaseS;
            var into = (timeS + offsetS - phase * phaseS) % cycleS;
            if (into < 0) into += cycleS;
            return into;
        }
    }

    // One road into a signalled junction.
    public sealed class CitySignalApproach
    {
        public string Road;
        public float HeadingDeg, StopX, StopY;
        public int Phase;
    }

    public sealed class CitySignalJunction
    {
        public string Id;
        public int OffsetS;
        public List<CitySignalApproach> Approaches = new List<CitySignalApproach>();
    }

    public sealed class CitySignalData
    {
        public CitySignalTiming Timing = new CitySignalTiming();
        public List<CitySignalJunction> Junctions = new List<CitySignalJunction>();
    }

    // One 250 m square of sidewalk from the walkways file (backend src.traffic.import_walkways). Corners are
    // centimetres (x east, y north); triangles and edges index them. The arrays are flat, as they come.
    public sealed class CityWalkwayTile
    {
        public int KeyX, KeyY;
        // x, y of each corner, in centimetres
        public int[] VerticesCm = Array.Empty<int>();
        // Three corners per triangle, clockwise seen from above
        public int[] Footpath = Array.Empty<int>(), KerbStone = Array.Empty<int>(), Verge = Array.Empty<int>();
        // Four per ramp: the kerb's x and y in centimetres, and its unit direction away from the road in 1/10000ths
        public int[] Ramps = Array.Empty<int>();
        // Four per triangle: three corners and the ramp it slopes with
        public int[] RampTriangles = Array.Empty<int>();
        // Three per edge: from, to (the face looks out to the right of from->to) and its ramp, or -1
        public int[] KerbEdges = Array.Empty<int>(), OuterEdges = Array.Empty<int>(), CheekEdges = Array.Empty<int>();
    }

    public sealed class CityWalkwayData
    {
        public string Area, SourceHash;
        public int Version;
        public float SurfaceY, KerbRiseM, KerbStoneM, RampDepthM;
        public List<CityWalkwayTile> Tiles = new List<CityWalkwayTile>();

        // Reads the file a token at a time, its long number lists straight into arrays, as it runs to a
        // megabyte. Nothing here touches Unity, so it can run off the main thread.
        public static CityWalkwayData Parse(string json)
        {
            try
            {
                using (var reader = new JsonTextReader(new System.IO.StringReader(json)))
                {
                    var data = new CityWalkwayData();
                    var sawRender = false;
                    Expect(reader, JsonToken.StartObject);
                    while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                    {
                        var name = (string)reader.Value;
                        reader.Read();
                        switch (name)
                        {
                            case "area": data.Area = (string)reader.Value; break;
                            case "version": data.Version = Convert.ToInt32(reader.Value); break;
                            case "source_hash": data.SourceHash = (string)reader.Value; break;
                            case "constants":
                                var constants = JObject.Load(reader);
                                data.SurfaceY = (float)constants["surface_y"];
                                data.KerbRiseM = (float)constants["kerb_rise_m"];
                                data.KerbStoneM = (float)constants["kerb_stone_m"];
                                data.RampDepthM = (float)constants["ramp_depth_m"];
                                break;
                            case "render": ReadRender(reader, data.Tiles); sawRender = true; break;
                            default: reader.Skip(); break;
                        }
                    }
                    if (!sawRender) throw new ContractException("Walkways reply has no render data");
                    if (data.KerbRiseM <= 0 || data.RampDepthM <= 0) throw new ContractException("Walkways reply has no kerb sizes");
                    return data;
                }
            }
            catch (Exception error) when (error is JsonException || error is InvalidCastException || error is FormatException || error is OverflowException || error is NullReferenceException)
            {
                throw new ContractException("Invalid walkways reply: " + error.Message);
            }
        }

        private static void ReadRender(JsonTextReader reader, List<CityWalkwayTile> tiles)
        {
            if (reader.TokenType != JsonToken.StartObject) throw new ContractException("Walkways render data is not an object");
            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                var name = (string)reader.Value;
                reader.Read();
                if (name != "tiles") { reader.Skip(); continue; }
                if (reader.TokenType != JsonToken.StartArray) throw new ContractException("Walkways tiles are not a list");
                while (reader.Read() && reader.TokenType == JsonToken.StartObject) tiles.Add(ReadTile(reader));
            }
        }

        private static CityWalkwayTile ReadTile(JsonTextReader reader)
        {
            var tile = new CityWalkwayTile();
            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                var name = (string)reader.Value;
                reader.Read();
                switch (name)
                {
                    case "key":
                        var key = ReadInts(reader);
                        if (key.Length != 2) throw new ContractException("A walkways tile's key is not two numbers");
                        tile.KeyX = key[0]; tile.KeyY = key[1];
                        break;
                    case "vertices_cm": tile.VerticesCm = ReadInts(reader); break;
                    case "zones":
                        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                        {
                            var zone = (string)reader.Value;
                            reader.Read();
                            var triangles = ReadInts(reader);
                            if (zone == "footpath") tile.Footpath = triangles;
                            else if (zone == "kerb_stone") tile.KerbStone = triangles;
                            else if (zone == "verge") tile.Verge = triangles;
                        }
                        break;
                    case "ramps": tile.Ramps = ReadInts(reader); break;
                    case "ramp_triangles": tile.RampTriangles = ReadInts(reader); break;
                    case "kerb_edges": tile.KerbEdges = ReadInts(reader); break;
                    case "outer_edges": tile.OuterEdges = ReadInts(reader); break;
                    case "cheek_edges": tile.CheekEdges = ReadInts(reader); break;
                    default: reader.Skip(); break;
                }
            }
            Check(tile);
            return tile;
        }

        // A list of whole numbers, lists inside it flattened (the ramps come as one list each)
        private static int[] ReadInts(JsonTextReader reader)
        {
            if (reader.TokenType != JsonToken.StartArray) throw new ContractException("Expected a list of numbers in the walkways");
            var values = new List<int>();
            var depth = 1;
            while (depth > 0 && reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonToken.StartArray: depth++; break;
                    case JsonToken.EndArray: depth--; break;
                    case JsonToken.Integer: values.Add(Convert.ToInt32(reader.Value)); break;
                    default: throw new ContractException("Expected whole numbers in the walkways, not " + reader.TokenType);
                }
            }
            return values.ToArray();
        }

        // Every index points at something, so a broken file fails here rather than in the mesh
        private static void Check(CityWalkwayTile tile)
        {
            var vertices = tile.VerticesCm.Length / 2;
            var ramps = tile.Ramps.Length / 4;
            if (tile.VerticesCm.Length % 2 != 0 || tile.Ramps.Length % 4 != 0) throw new ContractException("A walkways tile's corners or ramps are cut short");
            foreach (var triangles in new[] { tile.Footpath, tile.KerbStone, tile.Verge })
            {
                if (triangles.Length % 3 != 0) throw new ContractException("A walkways zone's triangles are cut short");
                if (triangles.Any(i => i < 0 || i >= vertices)) throw new ContractException("A walkways triangle points past its tile's corners");
            }
            void Grouped(int[] values, int stride, string what)
            {
                if (values.Length % stride != 0) throw new ContractException($"A walkways tile's {what} are cut short");
                for (var i = 0; i < values.Length; i += stride)
                {
                    for (var k = 0; k < stride - 1; k++)
                        if (values[i + k] < 0 || values[i + k] >= vertices) throw new ContractException($"A walkways tile's {what} point past its corners");
                    var ramp = values[i + stride - 1];
                    if (ramp < -1 || ramp >= ramps) throw new ContractException($"A walkways tile's {what} name a ramp it does not have");
                }
            }
            Grouped(tile.RampTriangles, 4, "ramp triangles");
            if (tile.RampTriangles.Where((_, i) => i % 4 == 3).Any(ramp => ramp < 0)) throw new ContractException("A walkways ramp triangle has no ramp");
            Grouped(tile.KerbEdges, 3, "kerb edges");
            Grouped(tile.OuterEdges, 3, "outer edges");
            Grouped(tile.CheekEdges, 3, "ramp sides");
        }

        private static void Expect(JsonTextReader reader, JsonToken token)
        {
            if (!reader.Read() || reader.TokenType != token) throw new ContractException("Walkways reply is not a JSON object");
        }
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
                        Id = (string)b["id"],
                        Type = (string)b["type"],
                        Name = (string)b["name"],
                        HeightM = (double)b["height_m"],
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
                        Id = (string)r["id"],
                        Class = (string)r["class"],
                        FromNode = (long)r["from_node"],
                        ToNode = (long)r["to_node"],
                        Oneway = (bool)r["oneway"],
                        Lanes = (int)r["lanes"],
                        SpeedKph = (int?)r["speed_kph"] ?? 40,
                        Points = r["points_m"].Select(p => ((double)p[0], (double)p[1])).ToList(),
                    }).ToList();
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid road reply: " + error.Message);
            }
        }

        public async Task<List<CitySignalTrafficLight>> GetSignalTrafficLightAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", $"/api/road-features/{area}/signals");
            request.TimeoutSeconds = 30;
            var reply = await SendAsync(request, cancellation);

            try { return ParseSignalTrafficLights(reply); }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid signal traffic light reply: " + error.Message);
            }
        }

        public static List<CitySignalTrafficLight> ParseSignalTrafficLights(string reply)
        {
            var root = JObject.Parse(reply);

            // Parse the timing object from the JSON root
            var timingToken = root["timing"];

            // A head runs on its junction's offset, which the reply gives on the junction
            var offsets = (root["junctions"] as JArray ?? throw new ContractException("Signal traffic light reply has no junctions"))
                .ToDictionary(j => (string)j["id"], j => (double)j["offset_s"]);

            // The JSON root contains a "signals" array matching the loaded fixture data
            return (root["signals"] as JArray ?? throw new ContractException("Signal traffic light reply has no signals"))
                .Select(r => new CitySignalTrafficLight
                {
                    Id = (string)r["id"],
                    Junction = (string)r["junction"],
                    Phase = (int)r["phase"],
                    Approach = (string)r["approach"],
                    TrafficHeadingDeg = (double)r["traffic_heading_deg"],
                    FacingHeadingDeg = (double)r["facing_heading_deg"],
                    DirectionSource = (string)r["direction_source"],
                    TypeCode = (string)r["type_code"],
                    BearingDeg = (double)r["bearing_deg"],
                    RoadCode = (string)r["road_code"],
                    XM = (double)r["x_m"],
                    YM = (double)r["y_m"],
                    OffsetS = offsets.TryGetValue((string)r["junction"], out var offset) ? offset : 0,
                    GL_duration = (double)timingToken["green_s"],
                    YL_duration = (double)timingToken["yellow_s"],
                    RL_duration = (double)timingToken["all_red_s"],
                    Cycle_duration = (double)timingToken["cycle_s"],

                }).ToList();
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

        // Sidewalk triangles, kerbs and ramps. The reply is about a megabyte (a third of that gzipped), so it
        // is read on a worker thread; the caller's await still comes back on its own thread.
        public async Task<CityWalkwayData> GetWalkwaysAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", "/api/road-features/" + Uri.EscapeDataString(area) + "/walkways");
            request.TimeoutSeconds = 60;
            var reply = await SendAsync(request, cancellation);
            return await Task.Run(() => CityWalkwayData.Parse(reply), cancellation);
        }

        // The lights' places and cycle. Their colours follow from these, so they need no polling.
        public async Task<CitySignalData> GetSignalsAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", "/api/road-features/" + Uri.EscapeDataString(area) + "/signals");
            request.TimeoutSeconds = 30;
            var reply = await SendAsync(request, cancellation);
            try { return ParseSignals(reply); }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid signal reply: " + error.Message);
            }
        }

        public static CitySignalData ParseSignals(string reply)
        {
            var root = JObject.Parse(reply);
            var timing = root["timing"] as JObject ?? throw new ContractException("Signal reply has no timing");
            var junctions = root["junctions"] as JArray ?? throw new ContractException("Signal reply has no junctions");
            return new CitySignalData
            {
                Timing = new CitySignalTiming { GreenS = (int)timing["green_s"], YellowS = (int)timing["yellow_s"], AllRedS = (int)timing["all_red_s"], Phases = (int)timing["phases"] },
                Junctions = junctions.Select(j => new CitySignalJunction
                {
                    Id = (string)j["id"],
                    OffsetS = (int)j["offset_s"],
                    Approaches = j["approaches"].Select(a => new CitySignalApproach
                    {
                        Road = (string)a["road"],
                        HeadingDeg = (float)a["heading_deg"],
                        StopX = (float)a["stop_x_m"],
                        StopY = (float)a["stop_y_m"],
                        Phase = (int)a["phase"],
                    }).ToList(),
                }).ToList(),
            };
        }
    }
}
