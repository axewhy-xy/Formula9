using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.EditorTools
{
    // The app's pages as a list at the side of the editor, like the Qt client's debug panel. Clicking
    // one shows it in the Game view; Edit opens its file in UI Builder. Outside Play mode the choice
    // stays on screen, so you are not sent back to Login. Play mode still starts at Login.
    public sealed class PagesWindow : EditorWindow
    {
        private readonly struct Page
        {
            public readonly string Label, File, Host, Nav;
            public Page(string label, string file, string host, string nav) { Label = label; File = file; Host = host; Nav = nav; }
        }

        private const string Folder = "Assets/Formula9/UI/Pages/";
        private static readonly Page[] Pages =
        {
            new Page("Login", "LoginPage", null, null),
            new Page("Main shell (sidebar, top bar, session controls)", "MainShell", "page-fleet", "nav-fleet"),
            new Page("Fleet Overview", "FleetOverview", "page-fleet", "nav-fleet"),
            new Page("Route Planning", "RoutePlanning", "page-route", "nav-route"),
            new Page("Incident Monitor", "IncidentMonitor", "page-incident", "nav-incident"),
            new Page("Charging", "Charging", "page-charging", "nav-charging"),
            new Page("Reports", "Reports", "page-reports", "nav-reports"),
            new Page("Scenarios", "Scenarios", "page-scenarios", null),
        };

        private static readonly string[] Hosts = { "page-fleet", "page-route", "page-charging", "page-incident", "page-reports", "page-scenarios" };
        private static readonly string[] NavItems = { "nav-fleet", "nav-route", "nav-charging", "nav-incident", "nav-reports" };

        private const string OpenedKey = "Formula9.PagesWindowOpened", SelectedKey = "Formula9.SelectedPage";
        private const double CheckEverySeconds = 0.5;
        private static double nextCheck;
        private readonly List<(Button Button, string File)> rows = new List<(Button, string)>();

        private static string Selected => EditorUserSettings.GetConfigValue(SelectedKey) ?? "FleetOverview";

        [MenuItem("Formula 9/Pages")]
        private static void Open() => GetWindow<PagesWindow>("Pages", true, InspectorWindowType());

        private static System.Type InspectorWindowType() => typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");

        // Docked beside the Inspector the first time, so nobody has to go looking for it.
        [InitializeOnLoadMethod]
        private static void Startup()
        {
            if (Application.isBatchMode) return;
            EditorApplication.update += KeepSelectedPageShown;
            EditorApplication.delayCall += () =>
            {
                if (EditorUserSettings.GetConfigValue(OpenedKey) == "1") return;
                EditorUserSettings.SetConfigValue(OpenedKey, "1");
                Open();
            };
        }

        // Unity rebuilds the page tree after a script reload, a file edit or leaving Play mode.
        private static void KeepSelectedPageShown()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + CheckEverySeconds;
            var page = Pages.FirstOrDefault(p => p.File == Selected);
            if (page.File != null) Show(page);
        }

        private void CreateGUI()
        {
            foreach (var page in Pages)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row; row.style.marginBottom = 2;
                var selected = page;
                var show = new Button(() => Select(selected)) { text = page.Label, tooltip = "Show this page in the Game view" };
                show.style.flexGrow = 1; show.style.flexShrink = 1; show.style.unityTextAlign = TextAnchor.MiddleLeft; show.style.height = 26;
                row.Add(show);
                row.Add(new Button(() => Edit(selected)) { text = "Edit", tooltip = "Open this page in UI Builder" });
                rootVisualElement.Add(row);
                rows.Add((show, page.File));
            }
            Highlight();
        }

        private void Select(Page page)
        {
            EditorUserSettings.SetConfigValue(SelectedKey, page.File);
            Show(page);
            Highlight();
        }

        private void Highlight()
        {
            foreach (var (button, file) in rows)
                button.style.backgroundColor = file == Selected ? new StyleColor(new Color(0.12f, 0.48f, 0.48f)) : new StyleColor(StyleKeyword.Null);
        }

        private static void Edit(Page page)
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Folder + page.File + ".uxml");
            if (asset == null) { Debug.LogError($"Page file not found: {Folder}{page.File}.uxml"); return; }
            EditorGUIUtility.PingObject(asset);
            AssetDatabase.OpenAsset(asset);
        }

        private static void Show(Page page)
        {
            var document = FindObjectsByType<UIDocument>(FindObjectsSortMode.None).FirstOrDefault();
            var root = document != null ? document.rootVisualElement : null;
            if (root?.Q("login-host") == null) return;
            var changed = Set(root.Q("login-host"), page.Host == null);
            changed |= Set(root.Q("training-host"), page.Host != null);
            foreach (var host in Hosts) changed |= Set(root.Q(host), host == page.Host);
            // Only Fleet Overview has the map beside it; the other pages take the full width.
            changed |= Set(root.Q("view-bar"), page.Host == "page-fleet");
            changed |= Set(root.Q("world"), page.Host == "page-fleet");
            foreach (var item in NavItems) root.Q(item)?.EnableInClassList("nav-item-selected", item == page.Nav);
            if (!changed) return;
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static bool Set(VisualElement element, bool visible)
        {
            var display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (element == null || element.style.display.value == display) return false;
            element.style.display = display;
            return true;
        }
    }
}
