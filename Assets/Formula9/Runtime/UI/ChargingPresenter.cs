using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Audio;
using Formula9.Contracts;

namespace Formula9.UI
{
    public sealed class ChargingQueueRow
    {
        public string VehicleId, Battery, Loops, State, Eta, Urgency, UrgencyClass, Booking;
        public double BatteryPercent;
        public int Rank;
    }

    public sealed class ChargingKpi
    {
        public string Title, Value, Note, Tone;  // Tone: teal, blue, violet, green, amber, red
    }

    public sealed class ChargingChoice
    {
        public string Kind, Title, Detail;
        public ChargeOption Option;  // null for a time-slot booking, which the backend checks by preview
        public bool Available;
    }

    public sealed class ChargingBlock
    {
        public string Label, ChargerId, Kind, BookingId;  // Kind: LIVE, ACTIVE, DRAFT
        public long StartTick, EndTick;
    }

    public sealed class ChargingException
    {
        public string Text, VehicleId;
        public bool Severe;
    }

    public sealed class ChargingBookingRow
    {
        public ChargeBooking Booking;
        public string Shuttle, Charger, Slot, Status, Source;
        public bool Movable;
    }

    public sealed class ChargingImpactRow
    {
        public string Label, Value;
        public bool Worse;
    }

    // The Charging page in the operator's words. Every battery, loop and service number comes from
    // the backend; this only sorts and words them, and lays bookings out on a timeline.
    public static class ChargingPresenter
    {
        public const long TicksPerSecond = 20, SnapTicks = 200;
        public const long WindowTicks = 20 * 60 * TicksPerSecond;
        private const long LeadTicks = 60 * TicksPerSecond;
        private static readonly HashSet<string> Live = new HashSet<string> { "SCHEDULED", "ACTIVE" };

        public static string SimClock(long tick)
        {
            var seconds = tick / TicksPerSecond;
            return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        }

        // Null when decisions can be made, otherwise why not.
        public static string Locked(FullSnapshot s)
        {
            if (s == null) return "Join a session to manage charging.";
            switch (s.Session.Status)
            {
                case "RUNNING": return null;
                case "READY": return "Not started · start the session to make charging decisions.";
                case "PAUSED": return "Paused · resume the session to make charging decisions.";
                case "RECOVERING": return "Recovering · charging decisions return when recovery finishes.";
                default: return $"Session {s.Session.Status.ToLowerInvariant()} · read-only.";
            }
        }

        public static List<ChargingQueueRow> Queue(FullSnapshot s) => s.Vehicles.Select(v =>
        {
            var f = s.EnergyForecasts.FirstOrDefault(e => e.VehicleId == v.VehicleId);
            var charging = s.Chargers.Any(c => c.OccupiedVehicleIds.Contains(v.VehicleId));
            var booking = s.ChargeBookings.FirstOrDefault(b => b.VehicleId == v.VehicleId && Live.Contains(b.Status));
            var (rank, urgency, css) = Urgency(v, f, charging);
            if (charging && booking?.Phase != null) urgency = PhaseName(booking.Phase);
            return new ChargingQueueRow
            {
                VehicleId = v.VehicleId, Rank = rank, Urgency = urgency, UrgencyClass = css,
                BatteryPercent = v.BatteryCapacityKwh > 0 ? Math.Max(0, Math.Min(100, 100 * v.BatteryKwh / v.BatteryCapacityKwh)) : 0,
                Battery = $"{v.BatteryKwh:0.#} kWh", Loops = f?.LoopsRemaining?.ToString() ?? "—", State = StateName(v.ServiceState),
                Eta = f?.NextDepotEtaS == null ? "—" : Duration(f.NextDepotEtaS.Value),
                Booking = booking == null ? "—" : $"{booking.ChargerId} · {SimClock(booking.StartTick)}",
            };
        }).OrderBy(r => r.Rank).ThenBy(r => r.BatteryPercent).ThenBy(r => r.VehicleId, StringComparer.Ordinal).ToList();

        private static (int, string, string) Urgency(FleetVehicle v, EnergyForecast f, bool charging)
        {
            if (v.ServiceState == "OUT_OF_SERVICE") return (0, "Stranded", "charge-urgency-critical");
            // Holding a port starts at dispatch, so the shuttle may still be driving: say what it is doing.
            if (charging) return (5, ChargerStage(v.ServiceState), "charge-urgency-charging");
            if (f?.ReachesDepotAboveReserve == false) return (1, "Critical", "charge-urgency-critical");
            if (f?.LoopsRemaining == null) return (4, "Unknown", "charge-urgency-none");
            if (f.LoopsRemaining <= 1) return (2, "High", "charge-urgency-high");
            if (f.LoopsRemaining <= 3) return (3, "Medium", "charge-urgency-medium");
            return (4, "Low", "charge-urgency-low");
        }

        // What a shuttle holding a port is doing, from the backend.
        public static string PhaseName(string phase)
        {
            switch (phase)
            {
                case "DROPPING_PASSENGERS": return "Dropping riders";
                case "DRIVING_TO_CHARGER": return "To charger";
                case "CHARGING": return "Charging";
                case "LEAVING_BAY": return "Leaving bay";
                case "RETURNING_TO_ROUTE": return "Back to route";
                default: return "Charger held";
            }
        }

        // Older snapshots have no phase. RETURNING_TO_DEPOT covers both the drive in and clearing the bay.
        public static string ChargerStage(string state)
        {
            switch (state)
            {
                case "CHARGING": return "Charging";
                case "QUEUED_FOR_CHARGE": return "At charger";
                case "RETURNING_TO_DEPOT": return "Depot trip";
                default: return "Charger held";
            }
        }

        public static List<ChargingKpi> Kpis(FullSnapshot s)
        {
            var queue = Queue(s);
            var free = s.Chargers.Count(c => c.OccupiedVehicleIds.Count < c.PortCount);
            var risk = queue.Count(r => r.Rank <= 2);
            var active = s.ChargeBookings.Count(b => b.Status == "ACTIVE");
            var scheduled = s.ChargeBookings.Count(b => b.Status == "SCHEDULED");
            var capacity = s.Vehicles.Sum(v => v.BatteryCapacityKwh);
            return new List<ChargingKpi>
            {
                new ChargingKpi { Title = "Average fleet battery", Value = capacity > 0 ? $"{100 * s.Vehicles.Sum(v => v.BatteryKwh) / capacity:0}%" : "—", Note = $"{s.Vehicles.Count} shuttles", Tone = "teal" },
                new ChargingKpi { Title = "Chargers free", Value = $"{free} / {s.Chargers.Count}", Note = "right now", Tone = free == 0 ? "amber" : "green" },
                new ChargingKpi { Title = "Bookings", Value = (active + scheduled).ToString(), Note = $"{active} active · {scheduled} scheduled", Tone = "blue" },
                new ChargingKpi { Title = "Shuttles at risk", Value = risk.ToString(), Note = "stranded, critical or last loop", Tone = risk > 0 ? "red" : "green" },
                new ChargingKpi { Title = "Decisions waiting", Value = s.ChargeOptions.Select(o => o.VehicleId).Distinct().Count().ToString(), Note = "backend offers open", Tone = "violet" },
            };
        }

        // What can be done for this shuttle: the backend's current offers, and a time slot unless it is on a charger or stranded.
        public static List<ChargingChoice> Choices(FullSnapshot s, string vehicleId)
        {
            var choices = new List<ChargingChoice>();
            var vehicle = s.Vehicles.FirstOrDefault(v => v.VehicleId == vehicleId);
            if (vehicle == null) return choices;
            foreach (var o in s.ChargeOptions.Where(o => o.VehicleId == vehicleId).OrderBy(o => o.OptionId, StringComparer.Ordinal))
            {
                if (o.Action == "CHARGE_NOW")
                    choices.Add(new ChargingChoice { Kind = "CHARGE_NOW", Option = o, Title = "Charge now", Available = o.Validation == "PASS",
                        Detail = o.Validation == "PASS" ? $"Go to {o.ChargerId} now · out of service {Duration(o.OutOfServiceS)}" : "Cannot reach the depot above its reserve" });
                else if (o.Action == "CONTINUE")
                    choices.Add(new ChargingChoice { Kind = "CONTINUE", Option = o, Title = "Continue in service", Available = o.Validation == "PASS",
                        Detail = o.LoopsAfter == null ? "Keep serving, then reassess" : $"Keep serving · {o.LoopsAfter} more loops before it must return" });
            }
            var canBook = vehicle.ServiceState != "OUT_OF_SERVICE" && !s.Chargers.Any(c => c.OccupiedVehicleIds.Contains(vehicleId));
            choices.Add(new ChargingChoice { Kind = "SCHEDULE", Title = "Charge later (book a slot)", Available = canBook,
                Detail = canBook ? "Pick a charger and time on the schedule" : vehicle.ServiceState == "OUT_OF_SERVICE" ? "Stranded shuttles cannot drive to a charger" : "It already holds a charger" });
            return choices;
        }

        // The first time any charger has a clear stretch, as a starting point; the backend's preview decides.
        public static (string ChargerId, long StartTick) SuggestSlot(FullSnapshot s)
        {
            var best = (ChargerId: (string)null, StartTick: long.MaxValue);
            foreach (var c in s.Chargers.OrderBy(c => c.ChargerId, StringComparer.Ordinal))
            {
                var start = Snap(s.Tick + SnapTicks);
                foreach (var b in s.ChargeBookings.Where(b => b.ChargerId == c.ChargerId && Live.Contains(b.Status)).OrderBy(b => b.StartTick))
                    if (b.EndTick > start) start = Snap(b.EndTick + SnapTicks);
                if (start < best.StartTick) best = (c.ChargerId, start);
            }
            return best;
        }

        public static long Snap(long tick) => (tick + SnapTicks - 1) / SnapTicks * SnapTicks;

        public static long WindowStart(long tick) => Math.Max(0, (tick - LeadTicks) / LeadTicks * LeadTicks);

        public static List<ChargingBlock> Blocks(FullSnapshot s) => s.ChargeBookings.Where(b => Live.Contains(b.Status))
            .Select(b => new ChargingBlock { BookingId = b.BookingId, ChargerId = b.ChargerId, StartTick = b.StartTick, EndTick = b.EndTick,
                Label = b.VehicleId, Kind = b.Status == "ACTIVE" ? "ACTIVE" : "LIVE" }).ToList();

        public static List<ChargingException> Exceptions(FullSnapshot s)
        {
            var result = new List<ChargingException>();
            foreach (var v in s.Vehicles.OrderBy(v => v.VehicleId, StringComparer.Ordinal))
            {
                var f = s.EnergyForecasts.FirstOrDefault(e => e.VehicleId == v.VehicleId);
                if (v.ServiceState == "OUT_OF_SERVICE")
                    result.Add(new ChargingException { VehicleId = v.VehicleId, Severe = true, Text = $"{v.VehicleId} is stranded. Charging can't help until it is recovered." });
                else if (f?.ReachesDepotAboveReserve == false && !s.Chargers.Any(c => c.OccupiedVehicleIds.Contains(v.VehicleId)))
                    result.Add(new ChargingException { VehicleId = v.VehicleId, Severe = true, Text = $"{v.VehicleId} can't reach the depot above its reserve." });
            }
            foreach (var b in s.ChargeBookings)
            {
                if (b.Phase == "DROPPING_PASSENGERS")
                    result.Add(new ChargingException { VehicleId = b.VehicleId, Text = $"{b.VehicleId} is dropping its passengers"
                        + (b.SetDownStopId == null ? "" : $" at {World.RouteDraftView.StopName(b.SetDownStopId)}") + $", then drives to {b.ChargerId}." });
                else if (b.Status == "SCHEDULED" && b.StartTick < s.Tick)
                    result.Add(new ChargingException { VehicleId = b.VehicleId, Text = $"{b.VehicleId}'s slot on {b.ChargerId} is waiting: the port is busy or the shuttle can't leave yet." });
                else if (b.Status == "MISSED")
                    result.Add(new ChargingException { VehicleId = b.VehicleId, Severe = true, Text = $"{b.VehicleId} missed its slot on {b.ChargerId}: {MissReason(b.StatusReason)}" });
            }
            return result;
        }

        private static string MissReason(string reason)
        {
            switch (reason)
            {
                case "PORT_BUSY": return "the port stayed busy.";
                case "SHUTTLE_NOT_READY": return "the shuttle could not leave in time.";
                case "CHARGER_OUT_OF_REACH": return "it no longer had the charge to get there.";
                case "BATTERY_EXHAUSTED": return "its battery ran out.";
                default: return "the slot passed.";
            }
        }

        public static List<ChargingBookingRow> Bookings(FullSnapshot s) => s.ChargeBookings
            .OrderBy(b => Live.Contains(b.Status) ? 0 : 1).ThenBy(b => b.StartTick).ThenBy(b => b.BookingId, StringComparer.Ordinal)
            .Select(b => new ChargingBookingRow
            {
                Booking = b, Shuttle = b.VehicleId, Charger = b.ChargerId, Movable = b.Status == "SCHEDULED",
                Slot = $"{SimClock(b.StartTick)}–{SimClock(b.EndTick)}",
                Status = b.Status == "ACTIVE" && b.Phase != null ? PhaseName(b.Phase) : b.Status == "SCHEDULED" && b.StartTick < s.Tick ? "Waiting" : Title(b.Status),
                Source = b.Origin == "OPERATOR" ? "Booked" : b.Origin == "CHARGE_NOW" ? "Charge now" : "Safety return",
            }).ToList();

        public static List<ChargingImpactRow> Impact(FullSnapshot s, string vehicleId, ChargeOption option, ChargeSchedulePreview preview)
        {
            var vehicle = s.Vehicles.FirstOrDefault(v => v.VehicleId == vehicleId);
            if (vehicle == null || (option == null && preview == null)) return new List<ChargingImpactRow>();
            var forecast = s.EnergyForecasts.FirstOrDefault(f => f.VehicleId == vehicleId);
            var serving = s.Vehicles.Count(v => v.ServiceState == "IN_SERVICE" || v.ServiceState == "DWELLING" || v.ServiceState == "DETOURING");
            var outOfService = option?.OutOfServiceS ?? preview.OutOfServiceS;
            var after = option?.BatteryAfterKwh ?? preview.BatteryAfterKwh;
            var loops = option != null ? option.LoopsAfter : preview.LoopsAfter;
            var minimum = option != null ? option.MinInServiceVehicles : preview.MinInServiceVehicles;
            var rows = new List<ChargingImpactRow>
            {
                new ChargingImpactRow { Label = "Out of service", Value = outOfService > 0 ? Duration(outOfService) : "None", Worse = outOfService > 0 },
                new ChargingImpactRow { Label = "Battery after", Value = $"{vehicle.BatteryKwh:0.#} → {after:0.#} kWh", Worse = after < vehicle.BatteryKwh },
                new ChargingImpactRow { Label = "Loops after", Value = $"{forecast?.LoopsRemaining?.ToString() ?? "—"} → {loops?.ToString() ?? "—"}" },
                new ChargingImpactRow { Label = "Shuttles still serving", Value = minimum == null ? "Unknown" : $"{serving} → {minimum}", Worse = minimum < serving },
            };
            return rows;
        }

        public static int Stage(string vehicleId, string choice, bool checkedOk) => vehicleId == null ? 0 : choice == null ? 1 : checkedOk ? 3 : 2;

        // Battery colours, the same thresholds as the Fleet Overview gauge.
        public static string BatteryClass(double percent) => BatteryWatch.IsRed(percent) ? "charge-battery-low" : percent < 50 ? "charge-battery-mid" : "charge-battery-good";

        public static string StatusClass(string status)
        {
            switch (status)
            {
                case "Active": case "Charging": return "charge-chip-green";
                case "To charger": case "Leaving bay": case "Back to route": return "charge-chip-pink";
                case "Dropping riders": return "charge-chip-amber";
                case "Scheduled": return "charge-chip-blue";
                case "Waiting": return "charge-chip-amber";
                case "Missed": return "charge-chip-red";
                case "Completed": return "charge-chip-teal";
                default: return "charge-chip-grey";
            }
        }

        public static string StateName(string state) => TrainingPresenter.StatusText(state);

        public static string Duration(double seconds)
        {
            var whole = (long)Math.Round(seconds);
            return whole < 60 ? $"{whole} s" : $"{whole / 60} min {whole % 60:00} s";
        }

        private static string Title(string status) => status.Length == 0 ? status : status.Substring(0, 1) + status.Substring(1).ToLowerInvariant();
    }
}
