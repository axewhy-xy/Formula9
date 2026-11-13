using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Contracts;
using Formula9.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Event = Formula9.Contracts.Event;

namespace Formula9.Tests
{
    public class IncidentSimulationTests
    {
        private static FullSnapshot Snapshot(string incidentId = "random-road_block-200", string state = "DETECTED", long detected = 200, long tick = 300)
        {
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
            snapshot.Tick = tick;
            snapshot.SimulationTimeS = tick * 0.05;
            snapshot.Disruptions.Clear();
            snapshot.Candidates.Clear();
            snapshot.Incidents = new List<Incident> { new Incident { IncidentId = incidentId, VehicleId = snapshot.Vehicles[0].VehicleId, State = state, DetectedTick = detected, CandidateIds = new List<string>() } };
            return snapshot;
        }

        private static IncidentRecord Record(string kind, string edge = "here:forward", string id = "random-road_block-200", params string[] impact) => new IncidentRecord
        {
            IncidentId = id, EdgeId = edge, Kind = kind, Impact = "Potential traffic delay", ImpactEdgeIds = impact.ToList(),
            Start = new Vector2(0, 0), End = new Vector2(0, 100), Position = new Vector2(0, 50), ActiveUntilTick = 800,
        };

        private static DraftRoad Road(string id, long source, long target, bool closed, params float[] xy)
        {
            var points = new Vector2[xy.Length / 2];
            for (var i = 0; i < points.Length; i++) points[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            return new DraftRoad { EdgeId = id, Name = "Lorong " + id, Source = source, Target = target, Closed = closed, Points = points };
        }

        // Node 2 at (0,0) leads to node 3 at (0,100). Two roads feed node 2, one leaves it the other way
        // and the road's own reverse direction comes back from node 3.
        private static List<DraftRoad> Network() => new List<DraftRoad>
        {
            Road("here:forward", 2, 3, false, 0, 0, 0, 100),
            Road("here:reverse", 3, 2, false, 0, 100, 0, 0),
            Road("west", 1, 2, false, -60, 0, 0, 0),
            Road("east", 4, 2, false, 60, 0, 0, 0),
            Road("away", 2, 5, false, 0, 0, 0, -60),
            Road("far-west", 6, 1, false, -200, 0, -60, 0),
            Road("shut", 7, 2, true, 0, -60, 0, 0),
            Road("beyond", 8, 6, false, -400, 0, -200, 0),
        };

        [Test]
        public void TrafficKeepsOffAClosureAndOnlyAsMuchOfTheQueueAsHasFormed()
        {
            var plan = IncidentSimulation.Plan(Record(IncidentSimulation.Block), Network(), Snapshot());
            var early = IncidentSimulation.Hazards(plan, 0);
            Assert.AreEqual(1, early.Count, "only the closed road, as no queue has formed");
            Assert.IsTrue(early[0].BothWays);
            CollectionAssert.AreEqual(plan.Road, early[0].Path);

            var hazards = IncidentSimulation.Hazards(plan, 4);
            Assert.AreEqual(1 + plan.Lanes.Count(l => l.StartM < 20), hazards.Count);
            var queue = hazards[1];
            Assert.IsFalse(queue.BothWays);
            Assert.AreEqual(20, IncidentSimulation.Length(queue.Path), 0.01f, "as far as it has grown");
            var run = queue.Path[queue.Path.Length - 1] - queue.Path[0];
            var lane = plan.Lanes[0];
            Assert.Less(Vector2.Dot(run, lane.Points[1] - lane.Points[0]), 0, "running the way its traffic goes, in towards the incident");
        }

        [Test]
        public void TrafficKeepsOffAnAccidentsSceneOnly()
        {
            var plan = IncidentSimulation.Plan(Record(IncidentSimulation.Accident), Network(), Snapshot());
            var scene = IncidentSimulation.Hazards(plan, 0)[0];
            Assert.IsTrue(scene.BothWays);
            Assert.AreEqual(28, IncidentSimulation.Length(scene.Path), 0.01f);
            Assert.AreEqual(36, scene.Path[0].y, 0.01f);
            Assert.AreEqual(64, scene.Path[scene.Path.Length - 1].y, 0.01f);
        }

        private static Event MarkerEvent(string eventType, JObject extra)
        {
            var data = new JObject
            {
                ["incident_type"] = "ROAD_ACCIDENT",
                ["map_marker"] = new JObject
                {
                    ["edge_id"] = "here:forward",
                    ["start"] = new JObject { ["x_m"] = 1.0, ["y_m"] = 2.0 },
                    ["end"] = new JObject { ["x_m"] = 3.0, ["y_m"] = 6.0 },
                    ["center"] = new JObject { ["x_m"] = 2.0, ["y_m"] = 4.0 },
                },
            };
            data.Merge(extra);
            return new Event { EntityType = "INCIDENT", EntityId = "inc", EventType = eventType, EventSequence = 1, Data = data };
        }

        [Test]
        public void AnIncidentEventBecomesARecordAndALaterMarkerlessOneChangesNothing()
        {
            var records = new Dictionary<string, IncidentRecord>();
            var refresh = new Event { EntityType = "INCIDENT", EntityId = "inc", EventType = "OPTIONS_READY", EventSequence = 2, Data = new JObject { ["reason"] = "Refresh" } };
            IncidentRecord.Merge(new[] { refresh, MarkerEvent("INCIDENT_DETECTED", new JObject { ["requires_guidance"] = false, ["active_until_tick"] = 800, ["impact_edge_ids"] = new JArray("a", "b") }) }, records);
            var record = records["inc"];
            Assert.AreEqual("ROAD_ACCIDENT", record.Kind);
            Assert.AreEqual(new Vector2(2, 4), record.Position);
            Assert.AreEqual(new Vector2(3, 6), record.End);
            CollectionAssert.AreEqual(new[] { "a", "b" }, record.ImpactEdgeIds);
            Assert.IsFalse(record.RequiresGuidance);
            Assert.AreEqual(800, record.ActiveUntilTick);
        }

        [Test]
        public void AnOptionsReadyIncidentNeedsTheOperator()
        {
            Assert.IsTrue(IncidentRecord.TryRead(MarkerEvent("OPTIONS_READY", new JObject()), out var record));
            Assert.IsTrue(record.RequiresGuidance);
        }

        [Test]
        public void AWorldEventSaysWhatItIsWhereAndWhoReachesIt()
        {
            var exposures = new JArray(
                new JObject { ["vehicle_id"] = "shuttle-03", ["distance_m"] = 850.0, ["eta_s"] = 84.6, ["is_estimate"] = true },
                new JObject { ["vehicle_id"] = "shuttle-04", ["distance_m"] = 1100.0, ["eta_s"] = 110.0, ["is_estimate"] = true },
                new JObject { ["eta_s"] = 1.0 });
            Assert.IsTrue(IncidentRecord.TryRead(MarkerEvent("INCIDENT_DETECTED", new JObject
            {
                ["requires_guidance"] = false, ["event_type"] = "COLLISION", ["exposures"] = exposures,
                ["location"] = new JObject { ["edge_id"] = "here:forward", ["road_name"] = "Lorong 6 Toa Payoh" },
                ["headline"] = "Shuttle 03 reaches a collision on Lorong 6 Toa Payoh in about 85 s (and 1 more shuttle)",
            }), out var record));
            Assert.AreEqual("COLLISION", record.EventType);
            Assert.AreEqual("Lorong 6 Toa Payoh", record.RoadName);
            StringAssert.StartsWith("Shuttle 03 reaches", record.Headline);
            CollectionAssert.AreEqual(new[] { "shuttle-03", "shuttle-04" }, record.Exposures.Select(e => e.VehicleId), "an exposure without a shuttle is skipped");
            Assert.AreEqual("shuttle-03 in ~85 s, shuttle-04 in ~110 s", IncidentRecord.Reach(record.Exposures));
            Assert.AreEqual("shuttle-03 in ~85 s (+1 more)", IncidentRecord.Reach(record.Exposures, 1));
            Assert.IsNull(IncidentRecord.Reach(new List<IncidentExposure>()));
            Assert.AreEqual("Collision", IncidentRecord.Name(record.EventType, record.Kind));
            Assert.AreEqual("Road accident", IncidentRecord.Name(null, IncidentSimulation.Accident), "a backend without world events");
            // An older backend's event says none of it
            Assert.IsTrue(IncidentRecord.TryRead(MarkerEvent("INCIDENT_DETECTED", new JObject()), out var old));
            Assert.IsNull(old.EventType);
            Assert.IsEmpty(old.Exposures);
        }

        [Test]
        public void TheStatusLineSaysWhoReachesTheIncidentAndAnyShuttleIsReportedSlow()
        {
            var snapshot = Snapshot();
            snapshot.Incidents[0].VehicleId = null;
            snapshot.Incidents[0].ExposedVehicleIds = new List<string> { "shuttle-02" };
            snapshot.Incidents[0].Exposures = new List<IncidentExposure> { new IncidentExposure { VehicleId = "shuttle-02", DistanceM = 300, EtaS = 30 } };
            var vehicle = snapshot.Vehicles[3];
            vehicle.SpeedMps = 5;
            vehicle.Position.XM = 0; vehicle.Position.YM = 30;
            var record = Record(IncidentSimulation.Accident, impact: "here:forward");
            record.EventType = "BREAKDOWN";
            var plan = IncidentSimulation.Plan(record, Network(), snapshot);
            // A world event slows every shuttle on the roads it congests, not only the one it names
            Assert.IsTrue(IncidentSimulation.IsSlowing(plan, vehicle, snapshot));
            var line = IncidentSimulation.Line(plan, snapshot);
            StringAssert.StartsWith("Breakdown · Lorong here:forward", line);
            StringAssert.Contains("reaches it: shuttle-02 in ~30 s", line);
        }

        [Test]
        public void TheQueueBacksUpTheRoadsLeadingInAndNotTheOnesLeaving()
        {
            var plan = IncidentSimulation.Plan(Record(IncidentSimulation.Block), Network(), Snapshot());
            var lanes = plan.Lanes.Select(l => l.EdgeId).ToList();
            CollectionAssert.AreEquivalent(new[] { "west", "east", "far-west" }, lanes, "not away, not its own reverse, not a closed road, and beyond is out of reach");
            Assert.AreEqual(60, plan.Lanes.Single(l => l.EdgeId == "far-west").StartM, 1e-3f, "the next road starts where the first ends");
            Assert.AreEqual(IncidentSimulation.ReachM - 60, plan.Lanes.Single(l => l.EdgeId == "far-west").LengthM, 1e-2f, "cut off at the queue's reach");
            var west = plan.Lanes.Single(l => l.EdgeId == "west");
            Assert.AreEqual(Vector2.zero, west.Points[0], "from the end nearest the incident");
        }

        [Test]
        public void AnAccidentQueuesBehindTheWreckOnItsOwnRoadFirst()
        {
            var plan = IncidentSimulation.Plan(Record(IncidentSimulation.Accident), Network(), Snapshot());
            var own = plan.Lanes[0];
            Assert.AreEqual("here:forward", own.EdgeId);
            Assert.AreEqual(new Vector2(0, 38), own.Points[0], "stopped short of the wreck halfway along");
            Assert.AreEqual(38, plan.Lanes.Single(l => l.EdgeId == "west").StartM, 1e-3f);
            Assert.AreEqual(new Vector2(0, 50), plan.Site);
        }

        [Test]
        public void WithoutTheRoadNetworkAnIncidentStillStandsOnItsOwnRoad()
        {
            var plan = IncidentSimulation.Plan(Record(IncidentSimulation.Accident), null, Snapshot());
            Assert.AreEqual(new Vector2(0, 50), plan.Site);
            Assert.AreEqual(1, plan.Lanes.Count);
        }

        [Test]
        public void TheCarsArriveInOrderAndWaitOnTheLeftOfTheirWay()
        {
            var plan = IncidentSimulation.Plan(Record(IncidentSimulation.Block), Network(), Snapshot());
            var cars = plan.Cars.Where(c => plan.Lanes[c.Lane].EdgeId == "west").OrderBy(c => c.StopM).ToList();
            Assert.Greater(cars.Count, 3);
            var lane = plan.Lanes.Single(l => l.EdgeId == "west");
            Assert.IsTrue(IncidentSimulation.CarPose(cars[0], lane, 5, out var at, out var travel), "the first car has arrived");
            Assert.IsFalse(IncidentSimulation.CarPose(cars[cars.Count - 1], lane, 2, out _, out _), "the last has not come yet");
            Assert.AreEqual(1, travel.x, 1e-3f, "the west road runs east into the junction");
            Assert.AreEqual(IncidentSimulation.LaneOffsetM, at.y, 1e-3f, "north of the centre line is its left");
            Assert.Less(at.x, 0);
            Assert.AreEqual(0, IncidentSimulation.QueueM(0));
            Assert.AreEqual(IncidentSimulation.ReachM, IncidentSimulation.QueueM(1000));
        }

        [Test]
        public void TheSameIncidentAlwaysDrawsTheSameTraffic()
        {
            var first = IncidentSimulation.Plan(Record(IncidentSimulation.Block), Network(), Snapshot());
            var again = IncidentSimulation.Plan(Record(IncidentSimulation.Block), Network(), Snapshot());
            CollectionAssert.AreEqual(first.Cars, again.Cars);
        }

        [Test]
        public void AClosureLastsAsLongAsItsDisruptionNotTheOperatorsCase()
        {
            var record = Record(IncidentSimulation.Block);
            var snapshot = Snapshot(state: "RESOLVED");
            snapshot.Disruptions.Add(new Disruption { DisruptionId = "d", AffectedEdgeIds = new List<string> { "here:forward" }, ActiveFromTick = 200, ActiveUntilTick = 400 });
            Assert.IsTrue(IncidentSimulation.IsLive(record, snapshot), "the detour finished; the road is still shut");
            snapshot.Tick = 400;
            Assert.IsFalse(IncidentSimulation.IsLive(record, snapshot));
        }

        [Test]
        public void AnAccidentEndsWhenSettledOrWhenItsTimeIsUp()
        {
            var record = Record(IncidentSimulation.Accident);
            Assert.IsTrue(IncidentSimulation.IsLive(record, Snapshot()));
            Assert.IsFalse(IncidentSimulation.IsLive(record, Snapshot(state: "RESOLVED")));
            Assert.IsFalse(IncidentSimulation.IsLive(record, Snapshot(tick: 800)));
            Assert.IsFalse(IncidentSimulation.IsLive(Record(IncidentSimulation.Accident, id: "another"), Snapshot()), "not this session's");
        }

        [Test]
        public void TheShuttleIsReportedSlowOnlyOnARoadTheBackendSlows()
        {
            var snapshot = Snapshot();
            var vehicle = snapshot.Vehicles[0];
            vehicle.SpeedMps = 5;
            vehicle.Position.XM = 0; vehicle.Position.YM = 30;
            var record = Record(IncidentSimulation.Accident, impact: "here:forward");
            var plan = IncidentSimulation.Plan(record, Network(), snapshot);
            Assert.IsTrue(IncidentSimulation.IsSlowing(plan, vehicle, snapshot));
            StringAssert.Contains("Lorong here:forward", IncidentSimulation.Line(plan, snapshot));
            StringAssert.Contains("clears in 25 s", IncidentSimulation.Line(plan, snapshot));
            StringAssert.Contains("slowed to 5 m/s", IncidentSimulation.Line(plan, snapshot));
            vehicle.Position.XM = 500;
            Assert.IsFalse(IncidentSimulation.IsSlowing(plan, vehicle, snapshot), "elsewhere");
            vehicle.Position.XM = 0; vehicle.SpeedMps = 0;
            Assert.IsFalse(IncidentSimulation.IsSlowing(plan, vehicle, snapshot), "stopped, not slowed");
            record.RequiresGuidance = true;
            Assert.AreEqual(0, IncidentSimulation.Plan(record, Network(), snapshot).Slowed.Count, "a closure holds the shuttle rather than slowing it");
        }

        [Test]
        public void TheCityBuildsTheSceneGrowsItsQueueAndClearsIt()
        {
            var rig = new GameObject("City incident rig");
            var view = new CityView(rig.transform, null);
            try
            {
                var record = Record(IncidentSimulation.Accident);
                var records = new[] { record };
                var snapshot = Snapshot(tick: 200);
                view.ShowIncidents(records, snapshot, Network(), 0);
                Assert.AreEqual(3, rig.GetComponentsInChildren<MeshRenderer>().Count(r => r.name == "Incident vehicle"), "two wrecks and the response van");
                var opening = rig.GetComponentsInChildren<MeshRenderer>().Count(r => r.name == "Queued car");
                Assert.LessOrEqual(opening, 2, "only the first cars rolling up at the first moment");
                StringAssert.Contains("Road accident", view.IncidentStatus);

                snapshot = Snapshot(tick: 500);
                view.ShowIncidents(records, snapshot, Network(), 1);
                view.Tick(new ShuttleMotion(), 1);
                var queued = rig.GetComponentsInChildren<MeshRenderer>().Count(r => r.name == "Queued car");
                Assert.Greater(queued, opening + 2, "15 s in, traffic has backed up");

                view.ShowIncidents(records, Snapshot(state: "RESOLVED", tick: 501), Network(), 2);
                Assert.IsNull(view.IncidentStatus);
                Assert.IsFalse(view.TryGetIncidentSite(record.IncidentId, out _));
            }
            finally
            {
                view.Dispose();
                Object.DestroyImmediate(rig);
            }
        }
    }
}
