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

    // The walk network the backend and the views share: a node where walkways meet (x east, y north, metres)
    public sealed class CityWalkNode
    {
        public int Id;
        public float X, Y;
        // corner, kerb (a crossing's end) or joint
        public string Kind;
    }

    public enum CityWalkwayKind { Sidewalk, Link, Crossing }

    // How a crossing walkway is controlled: by a light (signal), with priority to people (zebra), or neither
    // (uncontrolled, over a side street's or driveway's mouth); None for a walkway that is not a crossing
    public enum CityCrossingControl { None, Zebra, Signal, Uncontrolled }

    // A walkway between two nodes, walked either way
    public sealed class CityWalkway
    {
        public int Id, From, To, Lanes;
        public CityWalkwayKind Kind;
        // x, y of each point in metres; the first is From's place and the last To's
        public float[] PointsM = Array.Empty<float>();
        public float LengthM, WidthM;
        // A sidewalk's road and side of it (of the way the road's traffic runs)
        public string RoadId, Side;
        // A crossing's id (LTA's, or uncontrolled/<road>/<end>) and control; a signalled one's junction and the
        // phase whose red lets people over
        public string CrossingId, JunctionId;
        public CityCrossingControl Control;
        public int Phase;
    }

    // Where a walker starts: a distance along a walkway (from its From node), and how it walks and looks
    public sealed class CityWalkerSeed
    {
        public string Id;
        public int Walkway, Model;
        public float DistanceM, SpeedMps, HeightM;
    }

    // What the importer counted, to check the views against
    public sealed class CityWalkReport
    {
        public int Walkways, CrossingWalkways, UncontrolledCrossings, UnattachedCrossings, UnmatchedSignalCrossings, Components;
        public float MainComponentShare;
    }

    public sealed class CityWalkwayData
    {
        public string Area, SourceHash;
        public int Version;
        public float SurfaceY, KerbRiseM, KerbStoneM, RampDepthM;
        public List<CityWalkwayTile> Tiles = new List<CityWalkwayTile>();
        // The walk network; empty in a file from before it (version 1)
        public List<CityWalkNode> Nodes = new List<CityWalkNode>();
        public List<CityWalkway> Walkways = new List<CityWalkway>();
        public List<CityWalkerSeed> Seeds = new List<CityWalkerSeed>();
        public CityWalkReport Report = new CityWalkReport();

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
                            case "nodes": data.Nodes = JArray.Load(reader).Select(ReadNode).ToList(); break;
                            case "walkways": data.Walkways = JArray.Load(reader).Select(ReadWalkway).ToList(); break;
                            case "seeds": data.Seeds = JArray.Load(reader).Select(ReadSeed).ToList(); break;
                            case "report": data.Report = ReadReport(JObject.Load(reader)); break;
                            default: reader.Skip(); break;
                        }
                    }
                    if (!sawRender) throw new ContractException("Walkways reply has no render data");
                    if (data.KerbRiseM <= 0 || data.RampDepthM <= 0) throw new ContractException("Walkways reply has no kerb sizes");
                    CheckNetwork(data);
                    return data;
                }
            }
            catch (Exception error) when (error is JsonException || error is InvalidCastException || error is FormatException || error is OverflowException || error is NullReferenceException || error is ArgumentException)
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

        private static CityWalkNode ReadNode(JToken token)
        {
            var at = token["xy_m"] as JArray ?? throw new ContractException("A walk node has no place");
            if (at.Count != 2) throw new ContractException("A walk node's place is not two numbers");
            return new CityWalkNode { Id = (int)token["id"], X = (float)at[0], Y = (float)at[1], Kind = (string)token["kind"] };
        }

        private static CityWalkway ReadWalkway(JToken token)
        {
            var points = token["points_m"] as JArray ?? throw new ContractException("A walkway has no points");
            var flat = new float[points.Count * 2];
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i] as JArray;
                if (point == null || point.Count != 2) throw new ContractException("A walkway point is not two numbers");
                flat[i * 2] = (float)point[0];
                flat[i * 2 + 1] = (float)point[1];
            }
            var kind = (string)token["kind"];
            var control = (string)token["control"];
            return new CityWalkway
            {
                Id = (int)token["id"],
                From = (int)token["from"],
                To = (int)token["to"],
                Kind = kind == "sidewalk" ? CityWalkwayKind.Sidewalk : kind == "link" ? CityWalkwayKind.Link
                    : kind == "crossing" ? CityWalkwayKind.Crossing : throw new ContractException("Unknown walkway kind " + kind),
                PointsM = flat,
                LengthM = (float)token["length_m"],
                WidthM = (float?)token["width_m"] ?? 1.5f,
                Lanes = (int?)token["lanes"] ?? 1,
                RoadId = (string)token["road_id"],
                Side = (string)token["side"],
                CrossingId = (string)token["crossing_id"],
                Control = control == null ? CityCrossingControl.None : control == "signal" ? CityCrossingControl.Signal
                    : control == "zebra" ? CityCrossingControl.Zebra : control == "uncontrolled" ? CityCrossingControl.Uncontrolled
                    : throw new ContractException("Unknown crossing control " + control),
                JunctionId = (string)token["junction_id"],
                Phase = (int?)token["phase"] ?? 0,
            };
        }

        private static CityWalkerSeed ReadSeed(JToken token) => new CityWalkerSeed
        {
            Id = (string)token["id"],
            Walkway = (int)token["walkway"],
            DistanceM = (float)token["distance_m"],
            SpeedMps = (float)token["speed_mps"],
            HeightM = (float?)token["height_m"] ?? 1.7f,
            Model = (int?)token["model"] ?? 0,
        };

        private static CityWalkReport ReadReport(JObject report) => new CityWalkReport
        {
            Walkways = (int?)report["walkways"] ?? 0,
            CrossingWalkways = (int?)report["crossing_walkways"] ?? 0,
            UncontrolledCrossings = (int?)report["uncontrolled_crossings"] ?? 0,
            UnattachedCrossings = (int?)report["unattached_crossings"] ?? 0,
            UnmatchedSignalCrossings = (int?)report["unmatched_signal_crossings"] ?? 0,
            Components = (int?)report["components"] ?? 0,
            MainComponentShare = (float?)report["main_component_share"] ?? 0,
        };

        // Every walkway joins nodes there are, at their places, and every walker starts on a walkway there is
        private static void CheckNetwork(CityWalkwayData data)
        {
            for (var i = 0; i < data.Nodes.Count; i++)
                if (data.Nodes[i].Id != i) throw new ContractException("Walk nodes are not numbered in order");
            for (var i = 0; i < data.Walkways.Count; i++)
            {
                var way = data.Walkways[i];
                if (way.Id != i) throw new ContractException("Walkways are not numbered in order");
                if (way.From < 0 || way.From >= data.Nodes.Count || way.To < 0 || way.To >= data.Nodes.Count || way.From == way.To)
                    throw new ContractException($"Walkway {i} joins nodes there are not");
                if (way.PointsM.Length < 4) throw new ContractException($"Walkway {i} has fewer than two points");
                if (way.Kind == CityWalkwayKind.Crossing && way.Control == CityCrossingControl.None)
                    throw new ContractException($"Crossing walkway {i} says nothing of how it is controlled");
                if (way.Control == CityCrossingControl.Signal && string.IsNullOrEmpty(way.JunctionId))
                    throw new ContractException($"Signalled crossing walkway {i} names no junction");
            }
            foreach (var seed in data.Seeds)
                if (seed.Walkway < 0 || seed.Walkway >= data.Walkways.Count || seed.DistanceM < 0 || seed.DistanceM > data.Walkways[seed.Walkway].LengthM + 0.01f)
                    throw new ContractException($"Walker {seed.Id} starts off its walkway");
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
