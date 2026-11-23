using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.UI;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public class RouteEditingTests
    {
        private static RouteEditorEdge Edge(string id, bool closed = false, string fit = "PASS", params (double X, double Y)[] path) => new RouteEditorEdge
        {
            EdgeId = id, Source = 1, Target = 2, Closed = closed, Fit = fit, RoadName = id.StartsWith("main") ? "Lorong 4 Toa Payoh" : "Lorong 5 Toa Payoh",
            Path = path.Select(p => new RoutePolylinePoint { X = p.X, Y = p.Y, Z = 0 }).ToList(),
        };

        private static RouteEditorContext Context() => new RouteEditorContext
        {
            RouteId = "route-a", StartNodeId = 1, DestinationNodeId = 1,
            Nodes = new List<RouteEditorNode>
            {
                new RouteEditorNode { NodeId = 1, X = 0, Y = 0 }, new RouteEditorNode { NodeId = 2, X = 100, Y = 0 },
            },
            Edges = new List<RouteEditorEdge>
            {
                Edge("main:0:forward", true, "PASS", (0, 0), (100, 0)),
                Edge("side:0:forward", false, "PASS", (0, 50), (100, 50)),
                Edge("side:0:reverse", false, "PASS", (100, 50), (0, 50)),
                Edge("low:0:forward", false, "FAIL", (0, 90), (100, 90)),
            },
            Stops = new List<RouteEditorStop> { new RouteEditorStop { StopId = "52449-a", Name = "Toa Payoh Police Centre", NodeId = 2, X = 100, Y = 0 } },
            OriginalDirectedEdgeIds = new List<string> { "main:0:forward" },
            Assumptions = new List<string>(),
        };

        private static RouteCheck Check(string name, string status, string message, params string[] edges) => new RouteCheck
        {
            Check = name, Status = status, Message = message, Mandatory = true, EdgeIds = edges.ToList(),
        };

        private static RouteEditPreview Preview(string validation, double seconds, params RouteCheck[] checks) => new RouteEditPreview
        {
            Validation = validation, Approvable = validation == "PASS", Checks = checks.ToList(),
            RouteId = "route-a", DirectedEdgeIds = new List<string> { "side:0:forward" }, NodeIds = new List<long> { 1, 2 },
            Polyline = new List<RoutePolylinePoint> { new RoutePolylinePoint { X = 0, Y = 50 }, new RoutePolylinePoint { X = 100, Y = 50 } },
            EdgePaths = new List<List<RoutePolylinePoint>> { new List<RoutePolylinePoint> { new RoutePolylinePoint { X = 0, Y = 50 }, new RoutePolylinePoint { X = 50, Y = 60 }, new RoutePolylinePoint { X = 100, Y = 50 } } },
            Original = new RouteMetrics { DistanceM = 1000, DurationS = 100, EnergyKwh = 0.5, StopCoverage = new Dictionary<string, bool> { ["52449-a"] = true } },
            Edited = new RouteMetrics { DistanceM = 1500, DurationS = seconds, EnergyKwh = 0.7, StopCoverage = new Dictionary<string, bool> { ["52449-a"] = true } },
            ServiceImpact = new List<RouteServiceImpact> { new RouteServiceImpact { VehicleId = "shuttle-01", BatteryKwh = 28, LoopsOnOriginal = 40, LoopsOnEdited = 30 } },
        };

        [Test]
        public void AClickPicksTheNearestRoadOnceAndReachIsRespected()
        {
            var view = new RouteDraftView(Context());
            System.Func<Vector2, Vector2> project = m => m;
            var road = RouteDraftView.Nearest(view.Roads, project, new Vector2(50, 53), 12, out var at);
            Assert.AreEqual("side:0:forward", road.EdgeId, "a two-way road is picked once, by its forward side");
            Assert.That(at.x, Is.EqualTo(50).Within(0.01f));
            Assert.That(at.y, Is.EqualTo(50).Within(0.01f));
            Assert.IsNull(RouteDraftView.Nearest(view.Roads, project, new Vector2(50, 25), 12, out _), "too far from any road");
        }

        [Test]
        public void TheProposalFollowsEachRoadsShape()
        {
            var view = new RouteDraftView(Context()) { Preview = Preview("PASS", 150) };
            var path = view.ProposalPaths().Single();
            Assert.AreEqual("side:0:forward", path.EdgeId);
            Assert.AreEqual(3, path.Points.Length, "the road's bend, not a straight line between junctions");
            Assert.IsTrue(view.PicksRoads);
        }

        [Test]
        public void TheProblemIsSaidInNamesNotIds()
        {
            var snapshot = new FullSnapshot { Vehicles = new List<FleetVehicle> { new FleetVehicle { VehicleId = "shuttle-01", RouteId = "route-a" } } };
            var text = RoutePresenter.Problem(Context(), snapshot, "shuttle-01");
            StringAssert.Contains("Lorong 4 Toa Payoh is closed", text);
            StringAssert.Contains("Shuttle 01", text);
            StringAssert.Contains("Toa Payoh Police Centre", text);
            StringAssert.DoesNotContain("main:0", text);
        }

        [TestCase("52179-braddell-station-blk-106", "Braddell Station Blk 106")]
        [TestCase("52209-blk-101c-carpark", "Blk 101c Carpark")]
        [TestCase("depot-stop", "Depot Stop")]
        public void AStopIsNamedForPeople(string id, string name) => Assert.AreEqual(name, RouteDraftView.StopName(id));

        [Test]
        public void AnApprovedRouteStillSaysTheRoadIsClosed()
        {
            var snapshot = new FullSnapshot { Vehicles = new List<FleetVehicle> { new FleetVehicle { VehicleId = "shuttle-01", RouteId = "route-a" } } };
            var context = Context();
            context.OriginalDirectedEdgeIds = new List<string> { "side:0:forward" };
            Assert.AreEqual("Lorong 4 Toa Payoh is closed. Shuttle 01's route goes round it.", RoutePresenter.Problem(context, snapshot, "shuttle-01"));
        }

        [Test]
        public void ABadProposalNamesItsProblemAndItsRoads()
        {
            var preview = Preview("FAIL", 150, Check("VEHICLE_FIT", "FAIL", "The shuttle doesn't fit on Lorong 5.", "low:0:forward"), Check("CLOSURE", "PASS", "Active closure avoided"));
            Assert.AreEqual("This route can't be approved: 1 problem.", RoutePresenter.Verdict(preview, false, true));
            var issue = RoutePresenter.Issues(preview).Single();
            Assert.AreEqual("The shuttle doesn't fit on Lorong 5.", issue.Text);
            CollectionAssert.AreEqual(new[] { "low:0:forward" }, RoutePresenter.Highlighted(preview));
            Assert.AreEqual("Fix the problems above to approve this route.", RoutePresenter.ApprovalNote(null, preview, "reason"));
        }

        [Test]
        public void AGoodProposalShowsItsCostsAgainstTheClosedRoute()
        {
            var preview = Preview("PASS", 160, Check("CLOSURE", "PASS", "Active closure avoided"));
            Assert.AreEqual("This route works.", RoutePresenter.Verdict(preview, false, true));
            Assert.IsEmpty(RoutePresenter.Issues(preview));
            var rows = RoutePresenter.Metrics(preview);
            var time = rows.Single(r => r.Label == "Travel time per loop");
            Assert.AreEqual("2 min 40 s", time.Value);
            Assert.AreEqual("+1 min 0 s", time.Change);
            Assert.IsTrue(time.Worse);
            Assert.AreEqual("1 of 1", rows.Single(r => r.Label == "Required stops served").Value);
            Assert.AreEqual("−10", rows.Single(r => r.Label.StartsWith("Loops")).Change);
        }

        [TestCase("READY", true, false, false, 1)]
        [TestCase("READY", true, true, false, 2)]
        [TestCase("READY", true, true, true, 3)]
        [TestCase("READY", false, false, false, 3)]
        [TestCase("RUNNING", false, false, false, 4)]
        public void TheStagesFollowTheExercise(string status, bool closed, bool hasDraft, bool valid, int stage)
        {
            var snapshot = new FullSnapshot { Session = new TrainingSession { Status = status } };
            Assert.AreEqual(stage, RoutePresenter.Stage(snapshot, "shuttle-01", closed, hasDraft, valid ? Preview("PASS", 150) : null));
        }

        // Only the shuttle the detour is offered to goes back to it; another is at its outcome.
        [Test]
        public void AShuttleHeldAtAClosureSendsTheExerciseBackToItsDetour()
        {
            var snapshot = new FullSnapshot
            {
                Session = new TrainingSession { Status = "RUNNING" },
                Incidents = new List<Incident> { new Incident { IncidentId = "closure", VehicleId = "shuttle-01", State = "OPTIONS_READY" } },
                Candidates = new List<Candidate> { new Candidate { IncidentId = "closure", Action = "ROAD_DETOUR" } },
            };
            Assert.AreEqual(1, RoutePresenter.Stage(snapshot, "shuttle-01", true, false, null));
            Assert.AreEqual(3, RoutePresenter.Stage(snapshot, "shuttle-01", true, true, Preview("PASS", 150)));
            Assert.AreEqual(4, RoutePresenter.Stage(snapshot, "shuttle-02", true, false, null), "no detour for this shuttle");
            snapshot.Incidents[0].State = "EXECUTING";
            Assert.AreEqual(4, RoutePresenter.Stage(snapshot, "shuttle-01", true, false, null));
        }

        [Test]
        public void ThePickerAndStripNameShuttlesNotRoutes()
        {
            Assert.AreEqual("Shuttle 03", RoutePresenter.ShuttleName("shuttle-03"));
            Assert.AreEqual("bus-7", RoutePresenter.ShuttleName("bus-7"), "another ID is shown as it is");
            Assert.AreEqual("Shuttle 03 · route-peer-c", RoutePresenter.ShuttleChoice(new FleetVehicle { VehicleId = "shuttle-03", RouteId = "route-peer-c" }, false));
            // A shuttle given a detour drives that route now, and the picker says so
            StringAssert.StartsWith("Shuttle 01 · closure-detour-shuttle-01", RoutePresenter.ShuttleChoice(new FleetVehicle { VehicleId = "shuttle-01", RouteId = "closure-detour-shuttle-01" }, true));
            StringAssert.EndsWith("● needs a decision", RoutePresenter.ShuttleChoice(new FleetVehicle { VehicleId = "shuttle-01", RouteId = "route-a" }, true));
            Assert.AreEqual("Needs a decision (2)", RoutePresenter.PendingTitle(2));

            PendingDecision Pending(double? eta) => new PendingDecision("shuttle-03", new Incident { IncidentId = "collision" },
                eta == null ? null : new IncidentExposure { VehicleId = "shuttle-03", EtaS = eta.Value, DistanceM = 320 },
                new Dictionary<string, Candidate>());
            Assert.AreEqual("● Shuttle 03 · ~40 s", RoutePresenter.PendingChip(Pending(40)));
            Assert.AreEqual("● Shuttle 03 · ~2 min 0 s", RoutePresenter.PendingChip(Pending(120)));
            Assert.AreEqual("● Shuttle 03 · now", RoutePresenter.PendingChip(Pending(0.2)));
            Assert.AreEqual("● Shuttle 03", RoutePresenter.PendingChip(Pending(null)), "no figure without an exposure");
        }

        [Test]
        public void TheOutcomeSaysWhetherServiceWasPreservedAndAtWhatCost()
        {
            var outcome = new RouteOutcome
            {
                State = "LOOP_COMPLETED", VehicleId = "shuttle-01", ServicePreserved = true, DelayVsOriginalS = 74, ExtraEnergyVsOriginalKwh = 0.2, BatteryKwh = 27, LoopsLeftOnCharge = 30,
                Loops = new List<RouteOutcomeLoop> { new RouteOutcomeLoop { DurationS = 174, EnergyKwh = 0.7, AllStopsServed = true } },
                Stops = new List<RouteOutcomeStop> { new RouteOutcomeStop { StopId = "52449-a", Name = "Toa Payoh Police Centre", TimesServed = 1 } },
            };
            StringAssert.StartsWith("Service preserved", RoutePresenter.OutcomeHeadline(outcome));
            var rows = RoutePresenter.OutcomeRows(outcome);
            Assert.AreEqual("2 min 54 s", rows[0].Value);
            StringAssert.StartsWith("+1 min 14 s", rows[0].Change);
            Assert.AreEqual("Served once", rows.Single(r => r.Label == "Toa Payoh Police Centre").Value);
            outcome.ServicePreserved = false;
            StringAssert.StartsWith("Service not preserved", RoutePresenter.OutcomeHeadline(outcome));
        }

        [Test]
        public void AShuttlePointsAlongTheRoadThroughAJunction()
        {
            var motion = new ShuttleMotion();
            var route = new List<Vector2> { new Vector2(0, 0), new Vector2(50, 0), new Vector2(50, 50) };
            var east = Quaternion.LookRotation(Vector3.right);
            var north = Quaternion.LookRotation(Vector3.forward);
            motion.SetTarget("s", new Vector3(30, 0, 0), east, 0);
            motion.SetTarget("s", new Vector3(50, 0, 20), north, 0, route);
            Assert.IsTrue(motion.Sample("s", motion.Interval * 1.1f * 0.25f, out var early, out var earlyFacing));
            Assert.That(early.z, Is.EqualTo(0).Within(0.01f), "still on the first road");
            Assert.That(Quaternion.Angle(earlyFacing, east), Is.LessThan(5));
            Assert.IsTrue(motion.Sample("s", motion.Interval * 1.1f * 0.5f, out _, out var turning));
            Assert.That(Quaternion.Angle(turning, east), Is.GreaterThan(30), "swinging round at the corner");
            Assert.That(Quaternion.Angle(turning, north), Is.GreaterThan(30));
            Assert.IsTrue(motion.Sample("s", motion.Interval * 1.1f, out var end, out var endFacing));
            Assert.AreEqual(new Vector3(50, 0, 20), end);
            Assert.That(Quaternion.Angle(endFacing, north), Is.LessThan(0.01f));
        }
    }
}
