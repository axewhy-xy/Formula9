using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Formula9.Tests
{
    // Plays the Charging page in the real scene, through the UI: choose a shuttle, drag a draft slot,
    // book it, then see it locked once paused. Keeps a picture of each step. Needs a GPU and the
    // isolated backend: run without -nographics, with FORMULA9_BACKEND_URL at Tools/isolated_backend.py
    // and FORMULA9_WALKTHROUGH_OUT at a folder.
    [Category("Render")]
    public class ChargingWalkthrough
    {
        private string folder;
        private RenderTexture texture;
        private VisualElement root;
        private List<string> pageErrors;  // a field: locals captured here do not survive entering play mode

        [UnityTest, Timeout(600000)]
        public IEnumerator DragASlotThenBookIt()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORMULA9_WALKTHROUGH_OUT"))) Assert.Ignore("Set FORMULA9_WALKTHROUGH_OUT to a folder");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORMULA9_BACKEND_URL"))) Assert.Ignore("Set FORMULA9_BACKEND_URL to an isolated backend");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No GPU (-nographics)");
            EditorSceneManager.OpenScene("Assets/Scenes/Formula9.unity");
            yield return new EnterPlayMode();

            LogAssert.ignoreFailingMessages = true;
            folder = Environment.GetEnvironmentVariable("FORMULA9_WALKTHROUGH_OUT");
            Directory.CreateDirectory(folder);
            Application.runInBackground = true;
            pageErrors = new List<string>();
            Application.logMessageReceived += (text, stack, type) =>
            {
                if (type != LogType.Error && type != LogType.Exception) return;
                Note($"[{type}] {text}");
                if (stack != null && stack.Contains("ChargingPage")) pageErrors.Add(text);
            };
            var document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            texture = new RenderTexture(1600, 900, 24);
            document.panelSettings.targetTexture = texture;
            try
            {
                root = document.rootVisualElement;
                yield return Frames(5);
                Press("mode-switch");
                root.Q<TextField>("username").value = "charge" + Guid.NewGuid().ToString("N").Substring(0, 8);
                root.Q<TextField>("password").value = "Walkthrough-Test-1";
                root.Q<TextField>("confirm").value = "Walkthrough-Test-1";
                Press("submit");
                yield return Until(() => root.Q("training-host").resolvedStyle.display == DisplayStyle.Flex, "the training screen");
                yield return Seconds(1);
                Press("create");
                yield return Seconds(2);

                Nav("Route Planning");
                yield return Until(() => root.Q<Button>("route-suggest").enabledSelf, "the road network");
                Press("route-suggest");
                root.Q<TextField>("route-reason").value = "Avoid the closure";
                yield return Until(() => root.Q<Button>("route-approve").enabledSelf, "an approvable detour");
                Press("route-approve");
                yield return Until(() => root.Q<Button>("route-start").enabledSelf, "start");
                Press("route-start");
                yield return Seconds(3);

                Nav("Charging");
                yield return Until(() => root.Q("charge-lock").resolvedStyle.display == DisplayStyle.None, "a running session");
                yield return Seconds(1);
                Note("Queue: " + Rows());
                var page = root.Q("page-charging").Q<ScrollView>();
                yield return AssertFits(page, "the first view");
                yield return Shoot("1-queue");

                Choice("Charge later");
                yield return Until(() => Text("charge-verdict").Contains("can be booked") || Text("charge-verdict").Contains("can't"), "the slot check");
                yield return Frames(3);
                var before = Text("charge-draft-line");
                Note("Draft: " + before + " | " + Text("charge-verdict"));
                yield return AssertFits(page, "a draft with its controls");
                yield return Shoot("2-draft");

                var block = root.Q(className: "charge-block-movable");
                Assert.IsNotNull(block, "a draggable draft block");
                var from = block.worldBound.center;
                Send(block, PointerMoveEvent.GetPooled(Mouse(EventType.MouseMove, from)));  // the mouse passes over before any press
                var to = from + new Vector2(block.parent.worldBound.width * 0.03f, 44);
                Send(block, PointerDownEvent.GetPooled(Mouse(EventType.MouseDown, from)));
                Assert.IsTrue(block.HasPointerCapture(PointerId.mousePointerId), "the block takes the pointer");
                Send(block, PointerMoveEvent.GetPooled(Mouse(EventType.MouseDrag, to)));
                yield return Frames(3);
                Scroll(1);
                yield return Shoot("3-dragging");
                Scroll(0);
                Assert.IsNotNull(root.Q(className: "charge-block-ghost"), "the block being dragged stays visible");
                Send(block, PointerUpEvent.GetPooled(Mouse(EventType.MouseUp, to)));
                yield return Until(() => Text("charge-verdict").Contains("can be booked") || Text("charge-verdict").Contains("can't"), "the moved slot's check");
                yield return Frames(3);
                var after = Text("charge-draft-line");
                Note("Dragged: " + after + " | " + Text("charge-verdict"));
                Assert.AreNotEqual(before, after, "dragging moved the draft");
                yield return Shoot("4-dragged");

                StringAssert.Contains("can be booked", Text("charge-verdict"));
                Assert.IsFalse(root.Q<Button>("charge-apply").enabledSelf, "no apply without a reason");
                root.Q<TextField>("charge-reason").value = "Keep cover until the slot";
                yield return Until(() => root.Q<Button>("charge-apply").enabledSelf, "apply");
                Press("charge-apply");
                yield return Until(() => root.Query<Label>(className: "charge-chip").ToList().Any(l => l.text == "Scheduled"), "the booking");
                yield return Seconds(1);
                Note("Bookings: " + string.Join(" / ", root.Q("charge-bookings-host").Query<Label>().ToList().Select(l => l.text)));
                Scroll(1);
                yield return Shoot("5-booked");

                // The trip on the map: the slot starts, and the shuttle shows where it is going
                Scroll(0);
                yield return Until(() => root.Query<Label>(className: "charge-chip").ToList().Any(l => l.text == "Dropping riders" || l.text == "To charger"), "the trip to start", 120);
                yield return Seconds(1);
                Note("Trip: " + string.Join(" / ", root.Q("charge-bookings-host").Query<Label>().ToList().Select(l => l.text)));
                Note("Exceptions: " + string.Join(" / ", root.Q("charge-exceptions").Query<Label>().ToList().Select(l => l.text)));
                yield return Shoot("5b-trip-charging-page");
                Nav("Fleet Overview");
                yield return Seconds(2);
                var fleetMap = root.Query<Formula9.World.FleetMap>().ToList().First(m => m.resolvedStyle.display == DisplayStyle.Flex && m.contentRect.width > 1 && m.panel != null);
                yield return Shoot("5c-trip-map");
                Nav("Charging");
                yield return Frames(3);

                Press("charge-view-live");
                yield return Frames(3);
                yield return Shoot("6-live");

                Press("pause");
                yield return Until(() => root.Q("charge-lock").resolvedStyle.display == DisplayStyle.Flex, "the lock when paused");
                yield return Frames(3);
                Note("Locked: " + Text("charge-lock-text"));
                Scroll(0);
                yield return AssertFits(root.Q("page-charging").Q<ScrollView>(), "the locked view");
                Assert.IsFalse(root.Q<Button>("charge-apply").enabledSelf, "paused sessions take no decisions");
                yield return Shoot("7-locked");
                CollectionAssert.IsEmpty(pageErrors, "the Charging page logged no errors");
            }
            finally
            {
                document.panelSettings.targetTexture = null;
                UnityEngine.Object.Destroy(texture);
            }
            yield return new ExitPlayMode();
        }

        // The children's real bottom edge: the content box itself stretches to the viewport even when they overflow.
        private IEnumerator AssertFits(ScrollView page, string when)
        {
            yield return Frames(3);
            var bottom = page.contentContainer.Children().Max(c => c.layout.yMax);
            Note($"Fit ({when}): content ends at {bottom:0} in {page.contentViewport.layout.height:0}");
            Assert.LessOrEqual(bottom, page.contentViewport.layout.height + 1, "no scrolling at 1600x900: " + when);
            Assert.AreEqual(0f, page.scrollOffset.y, 0.5f);
        }

        private void Scroll(float amount)
        {
            var page = root.Q("page-charging").Q<ScrollView>();
            page.verticalScroller.value = amount * page.verticalScroller.highValue;
        }

        private static Event Mouse(EventType type, Vector2 at) => new Event { type = type, mousePosition = at, button = 0, clickCount = 1 };
        private static void Send(VisualElement target, EventBase e) { using (e) { e.target = target; target.SendEvent(e); } }

        private void Choice(string title)
        {
            var button = root.Query<Button>(className: "charge-choice").ToList().First(b => b.Query<Label>().ToList().Any(l => l.text.StartsWith(title)));
            Press(button);
        }

        private void Nav(string label) => Press(root.Q("nav-items").Query<Button>().ToList().First(b => b.Query<Label>().ToList().Any(l => l.text == label)));
        private void Press(string name) => Press(root.Q<Button>(name));
        private void Press(Button button)
        {
            Note("Press " + (string.IsNullOrEmpty(button.name) ? button.text : button.name) + (button.enabledInHierarchy ? "" : " (disabled)"));
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); }
        }

        private string Text(string name) => root.Q<Label>(name)?.text ?? "";
        private string Rows() => string.Join(" / ", root.Q("charge-queue-host").Query<Label>().ToList().Select(l => l.text));
        private void Note(string text) => File.AppendAllText(Path.Combine(folder, "log.txt"), $"{DateTime.Now:HH:mm:ss} {text}\n");

        private IEnumerator Shoot(string name)
        {
            yield return Frames(3);
            var previous = RenderTexture.active;
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
        }

        private static IEnumerator Frames(int count) { for (var i = 0; i < count; i++) yield return null; }
        private static IEnumerator Seconds(float seconds) { var end = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < end) yield return null; }
        private static IEnumerator Until(Func<bool> done, string what, float seconds = 60)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!done()) { if (Time.realtimeSinceStartup > end) throw new AssertionException("Timed out waiting for " + what); yield return null; }
        }
    }
}
