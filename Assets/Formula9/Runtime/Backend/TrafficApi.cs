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
        // How the backend sends each vehicle: those numbers packed little-endian as uint16 index, int32 x,
        // int32 y, int16 heading and uint16 speed (src/traffic/city_traffic.py FRAME_RECORD).
        public const int RecordBytes = 14;

        // Each walker the backend walks takes this many numbers in Pedestrians: its index in the walkways
        // file's seeds, x and y in centimetres, its heading in tenths of a degree, its speed in units of
        // 2 cm/s and its flags (PedestrianWaiting and the others).
        public const int PedestrianStride = 6;
        // How the backend sends each walker: those numbers packed little-endian as uint16 index, int32 x,
        // int32 y, int16 heading, uint8 speed and uint8 flags (src/traffic/city_traffic.py PED_RECORD).
        public const int PedestrianRecordBytes = 14;
        // A walker's flags: waiting at a kerb, on a crossing's road part (at the road's height), on a kerb
        // ramp, standing a while
        public const int PedestrianWaiting = 1, PedestrianOnRoad = 2, PedestrianOnRamp = 4, PedestrianPaused = 8;

        public long Tick;
        public double SimulationTimeS;
        public int[] Vehicles = Array.Empty<int>();
        // The walkers, PedestrianStride numbers each; none from a backend that walks none
        public int[] Pedestrians = Array.Empty<int>();
        public int PedestrianCount => Pedestrians.Length / PedestrianStride;
        public List<TrafficShuttlePose> Shuttles = new List<TrafficShuttlePose>();
        // The junctions a signal fault has stuck on red, and the simulation second each clears: the
        // backend's traffic and shuttles stop at every approach until then, so its lights are drawn red.
        public Dictionary<string, double> SignalFaults = new Dictionary<string, double>();

        // -----------------------------------------------------------------------------------
        // Function: UnpackVehicles
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a frame's packed vehicles into Stride numbers each.
        //
        // Args:
        //     packed (byte[]): RecordBytes a vehicle.
        //
        // Returns:
        //     int[]: Index, x cm, y cm, heading in tenths of a degree and speed cm/s, vehicle after vehicle.
        //
        // Throws:
        //     ContractException: If the last vehicle is cut short.
        public static int[] UnpackVehicles(byte[] packed)
        {
            if (packed.Length % RecordBytes != 0) throw new ContractException("Traffic frame's vehicles are cut short");
            var count = packed.Length / RecordBytes;
            var vehicles = new int[count * Stride];
            for (int i = 0, at = 0, to = 0; i < count; i++, at += RecordBytes, to += Stride)
            {
                vehicles[to] = packed[at] | packed[at + 1] << 8;
                vehicles[to + 1] = packed[at + 2] | packed[at + 3] << 8 | packed[at + 4] << 16 | packed[at + 5] << 24;
                vehicles[to + 2] = packed[at + 6] | packed[at + 7] << 8 | packed[at + 8] << 16 | packed[at + 9] << 24;
                vehicles[to + 3] = (short)(packed[at + 10] | packed[at + 11] << 8);
                vehicles[to + 4] = packed[at + 12] | packed[at + 13] << 8;
            }
            return vehicles;
        }

        // -----------------------------------------------------------------------------------
        // Function: PackVehicles
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function packs vehicles as the backend sends them, the reverse of UnpackVehicles,
        //     for a stand-in backend.
        //
        // Args:
        //     vehicles (int[]): Stride numbers a vehicle.
        //
        // Returns:
        //     string: The packed vehicles in base64, as a frame's "vehicles".
        public static string PackVehicles(params int[] vehicles)
        {
            var count = vehicles.Length / Stride;
            var packed = new byte[count * RecordBytes];
            for (int i = 0, at = 0, from = 0; i < count; i++, at += RecordBytes, from += Stride)
            {
                Write(packed, at, vehicles[from], 2);
                Write(packed, at + 2, vehicles[from + 1], 4);
                Write(packed, at + 6, vehicles[from + 2], 4);
                Write(packed, at + 10, vehicles[from + 3], 2);
                Write(packed, at + 12, vehicles[from + 4], 2);
            }
            return Convert.ToBase64String(packed);
        }

        // -----------------------------------------------------------------------------------
        // Function: UnpackPedestrians
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a frame's packed walkers into PedestrianStride numbers each.
        //
        // Args:
        //     packed (byte[]): PedestrianRecordBytes a walker.
        //
        // Returns:
        //     int[]: Index, x cm, y cm, heading in tenths of a degree, speed in 2 cm/s and flags, walker
        //     after walker.
        //
        // Throws:
        //     ContractException: If the last walker is cut short.
        public static int[] UnpackPedestrians(byte[] packed)
        {
            if (packed.Length % PedestrianRecordBytes != 0) throw new ContractException("Traffic frame's pedestrians are cut short");
            var count = packed.Length / PedestrianRecordBytes;
            var walkers = new int[count * PedestrianStride];
            for (int i = 0, at = 0, to = 0; i < count; i++, at += PedestrianRecordBytes, to += PedestrianStride)
            {
                walkers[to] = packed[at] | packed[at + 1] << 8;
                walkers[to + 1] = packed[at + 2] | packed[at + 3] << 8 | packed[at + 4] << 16 | packed[at + 5] << 24;
                walkers[to + 2] = packed[at + 6] | packed[at + 7] << 8 | packed[at + 8] << 16 | packed[at + 9] << 24;
                walkers[to + 3] = (short)(packed[at + 10] | packed[at + 11] << 8);
                walkers[to + 4] = packed[at + 12];
                walkers[to + 5] = packed[at + 13];
            }
            return walkers;
        }

        // -----------------------------------------------------------------------------------
        // Function: PackPedestrians
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function packs walkers as the backend sends them, the reverse of
        //     UnpackPedestrians, for a stand-in backend.
        //
        // Args:
        //     walkers (int[]): PedestrianStride numbers a walker.
        //
        // Returns:
        //     string: The packed walkers in base64, as a frame's "pedestrians".
        public static string PackPedestrians(params int[] walkers)
        {
            var count = walkers.Length / PedestrianStride;
            var packed = new byte[count * PedestrianRecordBytes];
            for (int i = 0, at = 0, from = 0; i < count; i++, at += PedestrianRecordBytes, from += PedestrianStride)
            {
                Write(packed, at, walkers[from], 2);
                Write(packed, at + 2, walkers[from + 1], 4);
                Write(packed, at + 6, walkers[from + 2], 4);
                Write(packed, at + 10, walkers[from + 3], 2);
                Write(packed, at + 12, walkers[from + 4], 1);
                Write(packed, at + 13, walkers[from + 5], 1);
            }
            return Convert.ToBase64String(packed);
        }

        private static void Write(byte[] into, int at, int value, int bytes)
        {
            for (var b = 0; b < bytes; b++) into[at + b] = (byte)(value >> (8 * b));
        }
    }

    public sealed partial class TrainingApi
    {
        // -----------------------------------------------------------------------------------
        // Function: GetTrafficFramesAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function fetches the backend's latest traffic frames after a tick, oldest first.
        //     They are presentation only, not part of the durable snapshot. Given a wait, a reply
        //     with no newer frame yet waits up to that long for the next one, so a caller that asks
        //     again at once hears of each frame as the backend makes it.
        //
        // Args:
        //     id (string): The session.
        //     afterTick (long): Only frames after this tick; -1 for the newest the backend keeps.
        //     waitMs (int): How long the backend may wait for a frame, at most 5000; 0 to answer at once.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<List<TrafficFrame>>: The frames; none when the wait ran out first.
        //
        // Throws:
        //     BackendErrorException: With INVALID_REFERENCE when the session has no traffic.
        //     ContractException: If the reply is malformed or for another session.
        public async Task<List<TrafficFrame>> GetTrafficFramesAsync(string id, long afterTick, int waitMs = 0, CancellationToken cancellation = default)
        {
            waitMs = Math.Max(0, Math.Min(5000, waitMs));
            var reply = await SendTrainingAsync("GET", $"/{Segment(id)}/traffic?after_tick={Math.Max(-1, afterTick)}&wait_ms={waitMs}", null, cancellation,
                http => http.TimeoutSeconds += (waitMs + 999) / 1000);
            // Base64 decoding and unpacking of every vehicle, ten times a second: off the main thread
            return await ParseAsync(() => ParseTrafficFrames(reply, id));
        }

        public static List<TrafficFrame> ParseTrafficFrames(string json, string sessionId)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException error) { throw new ContractException("Invalid traffic reply: " + error.Message); }
            return ParseTrafficFrames(root, sessionId);
        }

        // -----------------------------------------------------------------------------------
        // Function: ParseTrafficFrames (object)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads the frames of a traffic reply, or of a session stream's traffic
        //     message, which has the same session_id and frames.
        //
        // Args:
        //     root (JObject): The reply or message.
        //     sessionId (string): The session it must be for.
        //
        // Returns:
        //     List<TrafficFrame>: The frames, oldest first.
        //
        // Throws:
        //     ContractException: If it is malformed or for another session.
        public static List<TrafficFrame> ParseTrafficFrames(JObject root, string sessionId)
        {
            try
            {
                if ((string)root["session_id"] != sessionId) throw new ContractException("Traffic is for another session");
                var frames = new List<TrafficFrame>();
                foreach (var item in root["frames"] as JArray ?? throw new ContractException("Traffic reply has no frames"))
                {
                    var packed = item["vehicles"] as JValue;
                    if (packed?.Type != JTokenType.String) throw new ContractException("Traffic frame has no vehicles");
                    var vehicles = TrafficFrame.UnpackVehicles(Convert.FromBase64String((string)packed));
                    if (vehicles.Length / TrafficFrame.Stride != (int)item["vehicle_count"])
                        throw new ContractException("Traffic frame's vehicles do not match its count");
                    var frame = new TrafficFrame { Tick = (long)item["tick"], SimulationTimeS = (double)item["simulation_time_s"], Vehicles = vehicles };
                    // A backend that walks no pedestrians sends neither
                    var walkers = item["pedestrians"] as JValue;
                    if (walkers != null && walkers.Type != JTokenType.Null)
                    {
                        if (walkers.Type != JTokenType.String) throw new ContractException("Traffic frame's pedestrians are not packed");
                        frame.Pedestrians = TrafficFrame.UnpackPedestrians(Convert.FromBase64String((string)walkers));
                    }
                    if (frame.PedestrianCount != ((int?)item["pedestrian_count"] ?? 0))
                        throw new ContractException("Traffic frame's pedestrians do not match its count");
                    foreach (var shuttle in item["shuttles"] as JArray ?? new JArray())
                        frame.Shuttles.Add(new TrafficShuttlePose
                        {
                            VehicleId = (string)shuttle["vehicle_id"],
                            XM = (double)shuttle["x_m"],
                            YM = (double)shuttle["y_m"],
                            HeadingDeg = (double)shuttle["heading_deg"],
                            SpeedMps = (double)shuttle["speed_mps"],
                        });
                    // A backend from before signal faults sends none
                    foreach (var fault in item["signal_faults"] as JArray ?? new JArray())
                        frame.SignalFaults[(string)fault["junction_id"] ?? throw new ContractException("Signal fault names no junction")] =
                            (double)fault["until_s"];
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
