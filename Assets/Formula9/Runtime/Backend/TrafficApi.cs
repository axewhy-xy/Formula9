using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    // A shuttle's body as the backend drives it through the traffic: its middle in local metres, the way
    // it faces in degrees anticlockwise from east, and its speed.
    public sealed class TrafficShuttlePose
    {
        public string VehicleId;
        public double XM, YM, HeadingDeg, SpeedMps;
    }

    // One step of the traffic the backend drives: the vehicles on the roads and the shuttles' bodies, at
    // a tick of the session's clock.
    public sealed class TrafficFrame
    {
        // Each vehicle on the roads takes this many numbers in Vehicles: its index in /api/traffic's
        // vehicles, x and y in centimetres, its heading in tenths of a degree and its speed in cm/s.
        public const int Stride = 5;

        public long Tick;
        public double SimulationTimeS;
        public int[] Vehicles = Array.Empty<int>();
        public List<TrafficShuttlePose> Shuttles = new List<TrafficShuttlePose>();
    }

    public sealed partial class TrainingApi
    {
        // -----------------------------------------------------------------------------------
        // Function: GetTrafficFramesAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function fetches the backend's latest traffic frames after a tick, oldest first.
        //     They are presentation only, not part of the durable snapshot.
        //
        // Args:
        //     id (string): The session.
        //     afterTick (long): Only frames after this tick; -1 for every frame the backend keeps.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<List<TrafficFrame>>: The frames.
        //
        // Throws:
        //     BackendErrorException: With INVALID_REFERENCE when the session has no traffic.
        //     ContractException: If the reply is malformed or for another session.
        public async Task<List<TrafficFrame>> GetTrafficFramesAsync(string id, long afterTick, CancellationToken cancellation = default) =>
            ParseTrafficFrames(await SendTrainingAsync("GET", $"/{Segment(id)}/traffic?after_tick={Math.Max(-1, afterTick)}", null, cancellation), id);

        public static List<TrafficFrame> ParseTrafficFrames(string json, string sessionId)
        {
            try
            {
                var root = JObject.Parse(json);
                if ((string)root["session_id"] != sessionId) throw new ContractException("Traffic is for another session");
                var frames = new List<TrafficFrame>();
                foreach (var item in root["frames"] as JArray ?? throw new ContractException("Traffic reply has no frames"))
                {
                    var vehicles = (item["vehicles"] as JArray ?? throw new ContractException("Traffic frame has no vehicles")).ToObject<int[]>();
                    if (vehicles.Length % TrafficFrame.Stride != 0) throw new ContractException("Traffic frame's vehicles are cut short");
                    var frame = new TrafficFrame { Tick = (long)item["tick"], SimulationTimeS = (double)item["simulation_time_s"], Vehicles = vehicles };
                    foreach (var shuttle in item["shuttles"] as JArray ?? new JArray())
                        frame.Shuttles.Add(new TrafficShuttlePose
                        {
                            VehicleId = (string)shuttle["vehicle_id"],
                            XM = (double)shuttle["x_m"],
                            YM = (double)shuttle["y_m"],
                            HeadingDeg = (double)shuttle["heading_deg"],
                            SpeedMps = (double)shuttle["speed_mps"],
                        });
                    frames.Add(frame);
                }
                return frames;
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidCastException
                                          || error is NullReferenceException || error is FormatException || error is OverflowException)
            {
                throw new ContractException("Invalid traffic reply: " + error.Message);
            }
        }
    }
}
