using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // Where shuttle routes share a road on the 2D map, each gets its own lane (FleetMap.LaneShifts),
    // placed from the route's own direction of travel so it keeps its side from one piece to the next.
    public class RouteLaneTests
    {
        private static List<Contracts.Position> Path(params (double X, double Y)[] points) =>
            points.Select(p => new Contracts.Position { XM = p.X, YM = p.Y }).ToList();

        [Test]
        public void APieceHasOneNameWhicheverWayItIsDrivenComparingNumbersNotText()
        {
            var (key, reversed) = FleetMap.PieceKey(Path((99.2, 0), (100.5, 0)));
            var (back, backReversed) = FleetMap.PieceKey(Path((100.5, 0), (99.2, 0)));
            Assert.AreEqual(key, back);
            Assert.IsFalse(reversed, "99.2 is the lower end, though \"100.5\" sorts first as text");
            Assert.IsTrue(backReversed);
            Assert.AreEqual(("", false), FleetMap.PieceKey(Path()));
        }

        [Test]
        public void RoutesDrivingAPieceTheSameWayAreCentredInRouteIdOrderOnEveryPiece()
        {
            // Two shuttles down a corridor of two pieces, whose canonical ends point opposite ways:
            // route-a is on the left of travel on both, so the lanes never cross at the join
            var shifts = FleetMap.LaneShifts(new List<(string, bool, string, bool)>
            {
                ("p1", false, "route-b", false), ("p1", false, "route-a", false),
                ("p2", true, "route-a", false), ("p2", true, "route-b", false),
            });
            CollectionAssert.AreEqual(new[] { 3f, -3f, -3f, 3f }, shifts);
        }

        [Test]
        public void RoutesDrivingAPieceBothWaysEachKeepToTheirOwnLeft()
        {
            var shifts = FleetMap.LaneShifts(new List<(string, bool, string, bool)>
            {
                ("p", false, "route-a", false), ("p", true, "route-b", false), ("p", true, "route-c", false),
            });
            CollectionAssert.AreEqual(new[] { -3f, -9f, -3f }, shifts);
        }

        [Test]
        public void ARouteGoingTheOtherWayDoesNotSwapTheLanesBesideIt()
        {
            // route-a and route-b go one way; route-c joins on the next piece going the other way.
            // route-b is right of route-a on both pieces, so their lanes do not cross at the join
            var shifts = FleetMap.LaneShifts(new List<(string, bool, string, bool)>
            {
                ("p1", false, "route-a", false), ("p1", false, "route-b", false),
                ("p2", false, "route-a", false), ("p2", false, "route-b", false), ("p2", true, "route-c", false),
            });
            CollectionAssert.AreEqual(new[] { -3f, 3f, -9f, -3f, -3f }, shifts);
            Assert.Less(shifts[2], shifts[3]);
        }

        [Test]
        public void ALoneRouteStaysInTheMiddleAndADetourSpacesTheLanesWider()
        {
            var shifts = FleetMap.LaneShifts(new List<(string, bool, string, bool)>
            {
                ("alone", false, "route-a", false),
                ("shared", false, "route-a", true), ("shared", false, "route-b", false),
            });
            CollectionAssert.AreEqual(new[] { 0f, -4.5f, 4.5f }, shifts);
        }

        [Test]
        public void OffsetMovesALineToTheRightOfItsDirectionForAPositiveDistance()
        {
            // Panel points: y points down, so the right of a line heading east (+x) is +y
            var east = FleetMap.Offset(new[] { new Vector2(0, 0), new Vector2(10, 0) }, 3);
            Assert.AreEqual(new Vector2(0, 3), east[0]);
            Assert.AreEqual(new Vector2(10, 3), east[1]);
            var line = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            Assert.AreSame(line, FleetMap.Offset(line, 0), "no distance, the same line");
        }

        [Test]
        public void AtABendTheLaneKeepsItsWidth()
        {
            // East then south: the corner moves out along the diagonal, sqrt(2) times as far, so
            // both legs stay 3 points from the road
            var moved = FleetMap.Offset(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10) }, -3);
            Assert.AreEqual(-3, moved[0].y, 1e-4);
            Assert.AreEqual(13, moved[1].x, 1e-4);
            Assert.AreEqual(-3, moved[1].y, 1e-4);
            Assert.AreEqual(13, moved[2].x, 1e-4);
        }
    }
}
