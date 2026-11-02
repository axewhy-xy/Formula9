using System;
using System.IO;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.UI;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public sealed class ChargingTests
    {
        private static FullSnapshot Low()
        {
            var text = File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/low_reserve_snapshot.json"));
            var s = ContractParser.Parse<FullSnapshot>(text);
            s.Session.Status = "RUNNING";
            return s;
        }

        private static ChargeBooking Booking(string vehicle, string charger, string status, long start, long end) => new ChargeBooking
        {
            BookingId = $"booking-{vehicle}", Revision = 1, VehicleId = vehicle, ChargerId = charger, Status = status, Origin = "OPERATOR", StartTick = start, EndTick = end,
        };

        [Test]
        public void TheQueuePutsTheShuttleNeedingADecisionFirst()
        {
            var queue = ChargingPresenter.Queue(Low());
            Assert.AreEqual("shuttle-05", queue[0].VehicleId);
            Assert.AreEqual(5, queue.Count);
            Assert.IsTrue(queue.Zip(queue.Skip(1), (a, b) => a.Rank <= b.Rank).All(x => x));
        }

        [Test]
        public void AStrandedShuttleComesFirstAndCannotBookASlot()
        {
            var s = Low();
            var stranded = s.Vehicles.First(v => v.VehicleId == "shuttle-02");
            stranded.ServiceState = "OUT_OF_SERVICE";
            Assert.AreEqual("shuttle-02", ChargingPresenter.Queue(s)[0].VehicleId);
            Assert.IsFalse(ChargingPresenter.Choices(s, "shuttle-02").Single(c => c.Kind == "SCHEDULE").Available);
            StringAssert.Contains("stranded", ChargingPresenter.Exceptions(s).Single(e => e.VehicleId == "shuttle-02").Text);
        }

        [TestCase("RETURNING_TO_DEPOT", "Depot trip")]
        [TestCase("QUEUED_FOR_CHARGE", "At charger")]
        [TestCase("CHARGING", "Charging")]
        public void AShuttleHoldingAPortIsOnlyCalledChargingWhenItIs(string state, string label)
        {
            var s = Low();
            var vehicle = s.Vehicles.First(v => v.VehicleId == "shuttle-05");
            vehicle.ServiceState = state;
            s.Chargers[0].OccupiedVehicleIds.Add("shuttle-05");
            Assert.AreEqual(label, ChargingPresenter.Queue(s).Single(r => r.VehicleId == "shuttle-05").Urgency);
        }

        [TestCase("DROPPING_PASSENGERS", "Dropping riders")]
        [TestCase("DRIVING_TO_CHARGER", "To charger")]
        [TestCase("CHARGING", "Charging")]
        [TestCase("LEAVING_BAY", "Leaving bay")]
        [TestCase("RETURNING_TO_ROUTE", "Back to route")]
        public void TheBackendsPhaseSaysWhatTheShuttleIsDoing(string phase, string label)
        {
            var s = Low();
            s.Vehicles.First(v => v.VehicleId == "shuttle-05").ServiceState = "RETURNING_TO_DEPOT";
            s.Chargers[0].OccupiedVehicleIds.Add("shuttle-05");
            var booking = Booking("shuttle-05", s.Chargers[0].ChargerId, "ACTIVE", s.Tick, s.Tick + 100);
            booking.Phase = phase; booking.SetDownStopId = "52509-after-hdb-hub";
            s.ChargeBookings.Add(booking);
            Assert.AreEqual(label, ChargingPresenter.Queue(s).Single(r => r.VehicleId == "shuttle-05").Urgency);
            Assert.AreEqual(label, ChargingPresenter.Bookings(s).Single().Status);
            Assert.AreEqual(phase == "DROPPING_PASSENGERS", ChargingPresenter.Exceptions(s).Any(e => e.Text.Contains("dropping its passengers")));
        }

        [Test]
        public void ChoicesAreTheBackendsOffersPlusASlot()
        {
            var kinds = ChargingPresenter.Choices(Low(), "shuttle-05").Select(c => c.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "CHARGE_NOW", "CONTINUE", "SCHEDULE" }, kinds);
            CollectionAssert.AreEqual(new[] { "SCHEDULE" }, ChargingPresenter.Choices(Low(), "shuttle-01").Select(c => c.Kind).ToArray());
        }

        [TestCase("RUNNING", false)]
        [TestCase("READY", true)]
        [TestCase("PAUSED", true)]
        [TestCase("FAILED", true)]
        [TestCase("ARCHIVED", true)]
        public void OnlyARunningSessionTakesDecisions(string status, bool locked)
        {
            var s = Low(); s.Session.Status = status;
            Assert.AreEqual(locked, ChargingPresenter.Locked(s) != null);
        }

        [Test]
        public void TheLiveScheduleShowsOnlyLiveBookings()
        {
            var s = Low();
            s.ChargeBookings.Add(Booking("shuttle-01", "charger-1", "SCHEDULED", s.Tick + 100, s.Tick + 500));
            s.ChargeBookings.Add(Booking("shuttle-02", "charger-2", "ACTIVE", s.Tick, s.Tick + 500));
            s.ChargeBookings.Add(Booking("shuttle-03", "charger-1", "CANCELLED", s.Tick + 100, s.Tick + 500));
            var blocks = ChargingPresenter.Blocks(s);
            CollectionAssert.AreEquivalent(new[] { "shuttle-01", "shuttle-02" }, blocks.Select(b => b.Label).ToArray());
            Assert.AreEqual("ACTIVE", blocks.Single(b => b.Label == "shuttle-02").Kind);
        }

        [Test]
        public void TheSuggestedSlotAvoidsBookingsAndFutureTicksSnap()
        {
            var s = Low();
            foreach (var c in s.Chargers) s.ChargeBookings.Add(Booking("shuttle-0" + (s.ChargeBookings.Count + 1), c.ChargerId, "SCHEDULED", s.Tick, s.Tick + 4000));
            var (_, start) = ChargingPresenter.SuggestSlot(s);
            Assert.GreaterOrEqual(start, s.Tick + 4000);
            Assert.AreEqual(0, start % ChargingPresenter.SnapTicks);
        }

        [Test]
        public void ALateSlotAndAMissedSlotAreExceptions()
        {
            var s = Low();
            s.ChargeBookings.Add(Booking("shuttle-01", "charger-1", "SCHEDULED", s.Tick - 1, s.Tick + 500));
            var missed = Booking("shuttle-02", "charger-2", "MISSED", 0, 10); missed.StatusReason = "PORT_BUSY";
            s.ChargeBookings.Add(missed);
            var exceptions = ChargingPresenter.Exceptions(s);
            StringAssert.Contains("waiting", exceptions.Single(e => e.VehicleId == "shuttle-01").Text);
            StringAssert.Contains("port stayed busy", exceptions.Single(e => e.VehicleId == "shuttle-02").Text);
            Assert.AreEqual("Waiting", ChargingPresenter.Bookings(s).Single(r => r.Shuttle == "shuttle-01").Status);
        }

        [Test]
        public void ScheduleAndCancelCommandsCarryTheChargerRevision()
        {
            var s = Low();
            var charger = s.Chargers[0];
            var command = DecisionRules.ScheduleChargeCommand(s, "shuttle-05", charger.ChargerId, s.Tick + 400, null, "  Keep cover  ");
            Assert.AreEqual("SCHEDULE_CHARGE", command.CommandType); Assert.AreEqual(charger.AggregateRevision, command.ExpectedRevision);
            Assert.AreEqual("Keep cover", command.Reason); Assert.IsNull((string)command.Payload["replaces_booking_id"]);
            ContractParser.Serialize(command);
            s.ChargeBookings.Add(Booking("shuttle-05", charger.ChargerId, "SCHEDULED", s.Tick + 400, s.Tick + 900));
            var cancel = DecisionRules.CancelChargeBookingCommand(s, "booking-shuttle-05", "Not needed");
            Assert.AreEqual(charger.ChargerId, cancel.TargetId);
            ContractParser.Serialize(cancel);
            s.ChargeBookings[0].Status = "ACTIVE";
            Assert.Throws<ArgumentException>(() => DecisionRules.CancelChargeBookingCommand(s, "booking-shuttle-05", "Too late"));
            s.Session.Status = "FAILED";
            Assert.Throws<ArgumentException>(() => DecisionRules.ScheduleChargeCommand(s, "shuttle-05", charger.ChargerId, s.Tick + 400, null, "No"));
        }
    }
}
