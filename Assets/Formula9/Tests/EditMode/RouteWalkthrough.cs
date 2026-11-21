using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Formula9.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Formula9.Tests
{
    // Plays the routing exercise in the real scene, through the UI, and keeps a picture of each step
    // to look at. Needs a GPU and the isolated backend with its seeded incidents: run without
    // -nographics, with FORMULA9_BACKEND_URL at `Tools/isolated_backend.py --seed-incidents` and
    // FORMULA9_WALKTHROUGH_OUT at a folder.
    [Category("Render")]
    public class RouteWalkthrough
    {
        private const string ClosedRoad = "main:2:forward", LowRoad = "road-722434670:0:forward", UnknownRoad = "road-546928437:0:forward";
        private static readonly string[] Detour = { "bypass-b:11:forward", "bypass-b:5:forward" };

        private string folder;
        private RenderTexture texture;
        private VisualElement root;
        private FleetMap map;
        private int shot;

        [UnityTest, Timeout(1500000)]
        public IEnumerator TheExerciseFromClosureToOutcome()
        {
            RequireSetup();
            EditorSceneManager.OpenScene("Assets/Scenes/Formula9.unity");
            yield return new EnterPlayMode();

            LogAssert.ignoreFailingMessages = true;
            folder = Environment.GetEnvironmentVariable("FORMULA9_WALKTHROUGH_OUT");
            Directory.CreateDirectory(folder);
            Application.runInBackground = true;
            Application.logMessageReceived += (text, _, type) => { if (type != LogType.Log) Note($"[{type}] {text}"); };
            var document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            texture = new RenderTexture(1600, 900, 24);
            document.panelSettings.targetTexture = texture;
            try
            {
                root = document.rootVisualElement;
                yield return Frames(5);
                yield return Shoot("login");

                Press("mode-switch");
                root.Q<TextField>("username").value = "walk" + Guid.NewGuid().ToString("N").Substring(0, 8);
                root.Q<TextField>("password").value = "Walkthrough-Test-1";
                root.Q<TextField>("confirm").value = "Walkthrough-Test-1";
                Press("submit");
                yield return Until(() => root.Q("training-host").resolvedStyle.display == DisplayStyle.Flex, "the training screen");
                yield return Seconds(1);
                yield return Shoot("signed-in");

                Press("create");
                yield return Until(() => !string.IsNullOrEmpty(Text("status")) && root.Q<Button>("route-tab-editor") != null && root.Q("nav-items") != null && Text("title").Length > 0, "the session");
                yield return Seconds(2);
                var nav = root.Q("nav-items").Query<Button>().ToList().First(b => b.Query<Label>().ToList().Any(l => l.text == "Route Planning"));
                Press(nav);
                map = root.Query<FleetMap>().ToList().First(m => m.resolvedStyle.display == DisplayStyle.Flex && m.contentRect.width > 1 && m.panel != null);
                yield return Until(() => root.Q<Button>("route-suggest").enabledSelf, "the road network");
                yield return Seconds(1);
                Note("Problem: " + Text("route-problem"));
                yield return Shoot("problem");

                Press("route-show-problem");
                yield return Seconds(1);
                yield return Shoot("closure-focused");

                yield return ProposeAndReport(ClosedRoad, "closed-road");
                Press("route-cancel");
                yield return ProposeAndReport(LowRoad, "low-clearance");
                Press("route-cancel");
                yield return ProposeAndReport(UnknownRoad, "unknown-metadata");
                Press("route-cancel");

                // The proposal, one road at a time; the line must stay up while the second is checked
                yield return ProposeAndReport(Detour[0], "detour-first-road");
                map.Focus(new[] { Detour[0], Detour[1] });
                yield return Frames(3);
                Road(Detour[1], out var second);
                ClickMap(second);
                yield return Frames(2);
                Note("Verdict right after the second click: " + Text("route-verdict"));
                yield return Shoot("detour-while-checking");
                yield return Until(() => !Text("route-verdict").StartsWith("Checking") && !Text("route-verdict").StartsWith("Waiting"), "the verdict");
                Report("detour");
                yield return Shoot("detour-checked");

                root.Q<TextField>("route-reason").value = "Lorong 4 is closed; Lorong 7 keeps every stop";
                yield return Until(() => root.Q<Button>("route-approve").enabledSelf, "approve enabled", 10);
                yield return Shoot("ready-to-approve");
                Press("route-approve");
                yield return Until(() => root.Q<Button>("route-start").resolvedStyle.display == DisplayStyle.Flex, "start offered");
                yield return Seconds(2);
                Note("After approval: " + Text("route-outcome-headline"));
                yield return Shoot("approved");

                Press("route-start");
                yield return Until(() => root.Q("world-hud")?.resolvedStyle.display == DisplayStyle.Flex, "the 3D status line");
                yield return Seconds(8);
                Note("HUD: " + Text("world-hud"));
                yield return Shoot("3d-driving");

                // The line follows the view straight away, with no snapshot in between
                Press("view-map");
                Note("HUD on map: " + root.Q("world-hud").style.display.value);
                yield return Frames(3);
                yield return Shoot("map-running");
                Press("view-world");
                Note("HUD back in 3D: " + root.Q("world-hud").style.display.value);

                // The seeded signal fault and collision each ask for a decision within seconds of the start
                yield return Until(() => DecideButtons().Count > 0, "an incident to decide", 60);
                Note("HUD: " + Text("world-hud"));
                Note("Outcome during hold: " + Text("route-outcome-headline") + " | " + Text("route-outcome-notes"));
                yield return Seconds(3);
                Note("Outcome 3 s into the hold: " + Text("route-outcome-headline") + " | " + Text("route-outcome-notes"));
                // The incident leaves the view alone; a notification asks for a decision
                yield return Shoot("3d-incident-hold");
                yield return Until(() => DecideButtons().Count > 0, "the incident notification");
                Press(DecideButtons()[0]);
                yield return Until(() => root.Q("route-options").resolvedStyle.display == DisplayStyle.Flex, "the options");
                Note("Problem: " + Text("route-problem"));
                Note("Options: " + string.Join(", ", root.Q("route-options").Query<Button>().ToList().Where(b => b.name.StartsWith("option-") && b.resolvedStyle.display == DisplayStyle.Flex).Select(b => b.text)));
                yield return Shoot("decision-card");
                // Hold where the shuttle has a stop before the incident, else keep its route: the first shown and usable
                var option = new[] { "option-hold", "option-keep" }.Select(n => root.Q<Button>(n))
                    .FirstOrDefault(b => b.resolvedStyle.display == DisplayStyle.Flex && b.enabledInHierarchy);
                Assert.IsNotNull(option, "Hold or Keep route offered");
                // "Shuttle 04", from the picker's "Shuttle 04 · route-peer-d   ● needs a decision"
                var decided = root.Q<DropdownField>("shuttles").value.Split('·')[0].Trim();
                Press(option);
                root.Q<TextField>("route-confirm-reason").value = "Hold until the obstruction clears";
                yield return Until(() => root.Q<Button>("route-confirm-send").enabledSelf, "confirm enabled", 10);
                Press("route-confirm-send");
                // Applied, not just closed: the shuttle leaves the Needs a decision strip. (The panel may then
                // move on by itself to another shuttle still waiting.)
                yield return Until(() => !root.Q("route-pending-items").Query<Button>().ToList().Any(b => b.text.Contains(decided)), "the decision applied");
                Note($"{decided} decided; still waiting: " + string.Join(", ", root.Q("route-pending-items").Query<Button>().ToList().Select(b => b.text)));
                yield return Seconds(3);
                Press(nav);

                var began = Time.realtimeSinceStartup;
                var pictures = 0;
                while (!Text("route-outcome-headline").StartsWith("Service") && Time.realtimeSinceStartup - began < 1100)
                {
                    // The decision leaves the map up, and a later closure opens it again
                    if (UnityEngine.Object.FindFirstObjectByType<WorldView>().ShowingMap) Press("view-world");
                    yield return Seconds(30);
                    Note($"{Time.realtimeSinceStartup - began:0}s HUD: {Text("world-hud")}");
                    if (pictures < 12) yield return Shoot($"3d-run-{++pictures}");
                }
                Note("Outcome: " + Text("route-outcome-headline"));
                Note("Rows: " + Rows("route-outcome-rows"));
                Note("Notes: " + Text("route-outcome-notes"));
                if (UnityEngine.Object.FindFirstObjectByType<WorldView>().ShowingMap) Press("view-world");
                yield return Shoot("outcome-3d");
                Press("view-map");
                yield return Frames(3);
                yield return Shoot("outcome");
            }
            finally
            {
                document.panelSettings.targetTexture = null;
                LogAssert.ignoreFailingMessages = false;
            }
            yield return new ExitPlayMode();
        }

        // Plays the randomized incidents in the real 3D city and keeps pictures of each kind as its
        // traffic backs up. Same needs as the walkthrough above.
        [UnityTest, Timeout(1500000)]
        public IEnumerator TheIncidentsPlayOutInTheCity()
        {
            RequireSetup();
            EditorSceneManager.OpenScene("Assets/Scenes/Formula9.unity");
            yield return new EnterPlayMode();

            LogAssert.ignoreFailingMessages = true;
            folder = Environment.GetEnvironmentVariable("FORMULA9_WALKTHROUGH_OUT");
            Directory.CreateDirectory(folder);
            Application.runInBackground = true;
            Application.logMessageReceived += (text, _, type) => { if (type != LogType.Log) Note($"[{type}] {text}"); };
            var document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            texture = new RenderTexture(1600, 900, 24);
            document.panelSettings.targetTexture = texture;
            try
            {
                root = document.rootVisualElement;
                yield return Frames(5);
                Press("mode-switch");
                root.Q<TextField>("username").value = "walk" + Guid.NewGuid().ToString("N").Substring(0, 8);
                root.Q<TextField>("password").value = "Walkthrough-Test-1";
                root.Q<TextField>("confirm").value = "Walkthrough-Test-1";
                Press("submit");
                yield return Until(() => root.Q("training-host").resolvedStyle.display == DisplayStyle.Flex, "the training screen");
                yield return Seconds(1);
                Press("create");
                yield return Until(() => !string.IsNullOrEmpty(Text("status")) && root.Q<Button>("route-tab-editor") != null && root.Q("nav-items") != null && Text("title").Length > 0, "the session");
                yield return Seconds(2);
                var nav = root.Q("nav-items").Query<Button>().ToList().First(b => b.Query<Label>().ToList().Any(l => l.text == "Route Planning"));
                Press(nav);
                map = root.Query<FleetMap>().ToList().First(m => m.resolvedStyle.display == DisplayStyle.Flex && m.contentRect.width > 1 && m.panel != null);
                yield return Until(() => root.Q<Button>("route-suggest").enabledSelf, "the road network");
                yield return Seconds(1);
                yield return ProposeAndReport(Detour[0], "detour-first-road");
                map.Focus(new[] { Detour[0], Detour[1] });
                yield return Frames(3);
                Road(Detour[1], out var second);
                ClickMap(second);
                yield return Until(() => !Text("route-verdict").StartsWith("Checking") && !Text("route-verdict").StartsWith("Waiting"), "the verdict");
                root.Q<TextField>("route-reason").value = "Lorong 4 is closed; Lorong 7 keeps every stop";
                yield return Until(() => root.Q<Button>("route-approve").enabledSelf, "approve enabled", 10);
                Press("route-approve");
                yield return Until(() => root.Q<Button>("route-start").resolvedStyle.display == DisplayStyle.Flex, "start offered");
                yield return Seconds(2);
                Press("route-start");
                yield return Until(() => root.Q("world-hud")?.resolvedStyle.display == DisplayStyle.Flex, "the 3D status line");

                var world = UnityEngine.Object.FindFirstObjectByType<WorldView>();
                var workspaceIncident = root.Q<DropdownField>("reports-incidents");
                // The seeded collision ahead of Shuttles 02 and 04 shows a notification for each; the view
                // stays in 3D. Shuttle 04 has a way round it: re-route it before it gets there.
                yield return Until(() => DecideButton("Shuttle 04") != null, "Shuttle 04's notification", 60);
                Press(DecideButton("Shuttle 04"));
                yield return Until(() => root.Q<Button>("option-reroute").resolvedStyle.display == DisplayStyle.Flex && root.Q<Button>("option-reroute").enabledInHierarchy, "Re-route offered");
                Press("option-reroute");
                yield return Until(() => root.Q<Button>("route-suggest").enabledSelf, "the editor for the re-route");
                Press("route-suggest");
                yield return Until(() => root.Q<Button>("route-approve").enabledSelf, "approve enabled", 20);
                Note("Verdict: " + Text("route-verdict"));
                yield return Shoot("reroute-drawn");
                Press("route-approve");
                yield return Until(() => Text("route-problem").Contains("Decided for"), "the re-route applied");
                Note("Problem: " + Text("route-problem"));
                Press(nav);
                Press("view-world");
                yield return Until(() => Text("world-hud").Contains("Road") || Text("world-hud").Contains("Collision") || Text("world-hud").Contains("incident"), "the first incident on the status line");
                Note("HUD: " + Text("world-hud"));
                Note("Focus: " + world.FocusIncident(workspaceIncident.choices.Last()));
                yield return Seconds(1);
                yield return Shoot("incident-start");
                yield return Seconds(12);
                yield return Shoot("incident-12s");
                yield return Seconds(18);
                Note("HUD: " + Text("world-hud"));
                yield return Shoot("incident-30s");


                var began = Time.realtimeSinceStartup;
                var shownAccident = false; var sawSlow = false;
                while ((!shownAccident || !sawSlow) && Time.realtimeSinceStartup - began < 1100)
                {
                    // The decision leaves the map up, and a later closure opens it again
                    if (world.ShowingMap) Press("view-world");
                    yield return Seconds(2);
                    // The seeded collision, or a random accident after it
                    if (!shownAccident && (Text("world-hud").Contains("Road accident") || Text("world-hud").Contains("Collision")))
                    {
                        shownAccident = true;
                        Note("HUD: " + Text("world-hud"));
                        var latest = workspaceIncident.choices.LastOrDefault(c => c.Contains("accident")) ?? workspaceIncident.choices.First(c => c.Contains("two-shuttles"));
                        Note("Focus: " + world.FocusIncident(latest));
                        yield return Seconds(1);
                        yield return Shoot("accident-start");
                        yield return Seconds(10);
                        yield return Shoot("accident-10s");
                        yield return Seconds(14);
                        Note("HUD: " + Text("world-hud"));
                        yield return Shoot("accident-24s");
                    }
                    if (!sawSlow && Text("world-hud").Contains("slowed to"))
                    {
                        sawSlow = true;
                        Note("HUD: " + Text("world-hud"));
                        world.WatchShuttle("shuttle-01");
                        yield return Seconds(1);
                        yield return Shoot("shuttle-slowed");
                    }
                }
                Note($"Saw accident: {shownAccident}; saw shuttle slowed: {sawSlow}");
                Assert.IsTrue(shownAccident, "a road accident came up");
            }
            finally
            {
                document.panelSettings.targetTexture = null;
                LogAssert.ignoreFailingMessages = false;
            }
            yield return new ExitPlayMode();
        }

        // These sign up throwaway accounts, so an unset backend must not fall through to the shared default.
        private static void RequireSetup()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORMULA9_WALKTHROUGH_OUT"))) Assert.Ignore("Set FORMULA9_WALKTHROUGH_OUT to a folder");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORMULA9_BACKEND_URL"))) Assert.Ignore("Set FORMULA9_BACKEND_URL to an isolated test backend");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No GPU (-nographics)");
        }

        private IEnumerator ProposeAndReport(string edge, string name)
        {
            Road(edge, out var at);
            map.Focus(new[] { edge });
            yield return Frames(3);
            Road(edge, out at);
            ClickMap(at);
            yield return Frames(2);
            Note($"{name}: waypoints {root.Q("route-waypoints").childCount}");
            yield return Until(() => !Text("route-verdict").StartsWith("Checking") && !Text("route-verdict").StartsWith("Waiting") && !Text("route-verdict").StartsWith("Click"), "the verdict for " + name);
            yield return Frames(3);
            Report(name);
            yield return Shoot(name);
        }

        private void Report(string name)
        {
            Note($"{name}: {Text("route-verdict")}");
            foreach (var label in root.Q("route-issues").Query<Label>().ToList()) Note("  issue: " + label.text);
            Note("  metrics: " + Rows("route-metrics"));
            Note("  roads: " + string.Join(", ", root.Q("route-waypoints").Query<Label>(className: "route-waypoint-name").ToList().Select(l => l.text)));
        }

        // A road's middle, on screen. The draft's own shapes are the ones the click is tested against.
        private void Road(string edge, out Vector2 at)
        {
            var draft = (RouteDraftView)typeof(FleetMap).GetField("draft", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(map);
            if (draft == null || !draft.TryRoad(edge, out var road)) throw new AssertionException("No road " + edge);
            var middle = road.Points[road.Points.Length / 2];
            var project = typeof(FleetMap).GetMethod("Project", BindingFlags.NonPublic | BindingFlags.Instance);
            at = (Vector2)project.Invoke(map, new object[] { (double)middle.x, (double)middle.y });
        }

        private void ClickMap(Vector2 local)
        {
            var world = map.LocalToWorld(local);
            void Send(EventBase e) { using (e) { e.target = map; map.SendEvent(e); } }
            Send(PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = world, button = 0, clickCount = 1 }));
            Send(PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = world, button = 0, clickCount = 1 }));
        }

        private void Press(string name) => Press(root.Q<Button>(name));

        // The Decide buttons of the incident notifications showing now
        private List<Button> DecideButtons() =>
            root.Q("notifications").Query<Button>(className: "notification-decide").ToList();

        // The Decide button for one shuttle ("Shuttle 04"), looked up when it is about to be pressed
        private Button DecideButton(string shuttle) => DecideButtons().FirstOrDefault(b => b.text.EndsWith(shuttle));

        private void Press(Button button)
        {
            Note("Press " + (string.IsNullOrEmpty(button.name) ? button.text : button.name) + (button.enabledInHierarchy ? "" : " (disabled)"));
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); }
        }

        private string Text(string name) => root.Q<Label>(name)?.text ?? "";

        private string Rows(string name) => string.Join("; ", root.Q(name).Children().Select(row => string.Join(" ", row.Query<Label>().ToList().Select(l => l.text))));

        private void Note(string text)
        {
            File.AppendAllText(Path.Combine(folder, "log.txt"), $"{DateTime.Now:HH:mm:ss} {text}\n");
        }

        private IEnumerator Shoot(string name)
        {
            yield return Frames(3);
            var previous = RenderTexture.active;
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(Path.Combine(folder, $"{++shot:00}-{name}.png"), image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
            Note("Picture " + name);
        }

        private static IEnumerator Frames(int count) { for (var i = 0; i < count; i++) yield return null; }

        private static IEnumerator Seconds(float seconds)
        {
            var until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        private IEnumerator Until(Func<bool> done, string what, float timeout = 45)
        {
            var until = Time.realtimeSinceStartup + timeout;
            while (!done())
            {
                if (Time.realtimeSinceStartup > until)
                {
                    Note("TIMED OUT waiting for " + what + "; message: " + Text("message") + "; login: " + Text("login-error"));
                    yield return Shoot("timeout");
                    Assert.Fail("Timed out waiting for " + what);
                }
                yield return null;
            }
        }
    }
}
