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
        // The centreline in local metres: x east, y north of central Singapore.
        public List<(double X, double Y)> Points;
    }

    //public sealed class CitySignalTrafficLight
    //{
    //    public string Junction;
    //    public int Phase;
    //    public double XM, YM, FacingDegree;
    //}

    //public sealed class TrafficLightData
    //{
    //    public Dictionary<string, int> Offsets;  // junction id 
    //    public List<CitySignal> Heads; // all 683 lights
    //    public double GreenS, YellowS; // 30 and 3
    //    public double PhaseS; // which grp of roads a light belongs to
    //    public double CycleS; // one full round of the lights, which is 70s, then it repeats
    //    public string Credit; // the LTA credit text
    //}

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
                        Id = (string)r["id"],
                        Class = (string)r["class"],
                        FromNode = (long)r["from_node"],
                        ToNode = (long)r["to_node"],
                        Oneway = (bool)r["oneway"],
                        Lanes = (int)r["lanes"],
                        Points = r["points_m"].Select(p => ((double)p[0], (double)p[1])).ToList(),
                    }).ToList();
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid road reply: " + error.Message);
            }
        }

        //public async Task<List<CitySignalTrafficLight>> GetSignalTrafficLightAsync(string area, CancellationToken cancellation = default)
        //{
        //    var request = Request("GET", "/api/signaltrafficlight/" + Uri.EscapeDataString(area));
        //    request.TimeoutSeconds = 30;
        //    var reply = await SendAsync(request, cancellation);

        //    try
        //    {
        //        return (JObject.Parse(reply)["signaltrafficlight"] as JArray ?? throw new ContractException("Signal Traffic Light reply has no signal traffic lights"))
        //            .Select(r => new CitySignalTrafficLight
        //            {
        //                Junction = (string)r[""];

        //    }

        //    catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
        //    {
        //        throw new ContractException("Invalid Signal Traffic Light reply " + error.Message);
        //    }
        //}

        public async Task<List<CitySignalTrafficLight>> GetSignalTrafficLightAsync(string area, CancellationToken cancellation = default)
        {
            var request = Request("GET", $"/api/road-features/{area}/signals");
            request.TimeoutSeconds = 30;
            var reply = await SendAsync(request, cancellation);

            try
            {
                // The JSON root contains a "signals" array matching the loaded fixture data[cite: 7]
                return (JObject.Parse(reply)["signals"] as JArray ?? throw new ContractException("Signal traffic light reply has no signals"))
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
                        YM = (double)r["y_m"]
                    }).ToList();
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException || error is NullReferenceException)
            {
                throw new ContractException("Invalid signal traffic light reply: " + error.Message);
            }
        }
    }
}
