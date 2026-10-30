using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Audio;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    // Wires the UI Toolkit screens to the login and session layers. Decisions about what to show
    // live in LoginMessages and TrainingPresenter; this class only moves values on and off screen.
    [RequireComponent(typeof(UIDocument))]
    public sealed class Formula9App : MonoBehaviour
    {

        private AuthSession auth;
        private AuthClient authClient;
        private TrainingApi api;
        private SessionController session;
        private bool signUp, busy, loggingOut;
        private List<SessionListEntry> listedSessions = new List<SessionListEntry>();
        private string lastListedState;

        private VisualElement loginScreen, trainingScreen, fleet, confirmRow, fleetPanel;
        private TextField fleetSearch;
        private VisualElement fleetEmpty;
        private Label fleetTitle;
        private Toggle remember;
        private Button viewMap, viewWorld;
        private const string RememberedUserKey = "login.rememberedUser";
        private TextField username, password, confirm, sessionId;
        private Label signUpHint, loginError, title, user, capacity, status, message, worldStatus;
        private Button forgot, submit, modeSwitch, start, pause, resume, archive, refreshSessions;
        private DropdownField scenario, sessions;

        private WorldView world;
        private TrainingWorkspace workspace;

        private void OnEnable()
        {
            var transport = new UnityHttpTransport();
            auth = new AuthSession();
            authClient = new AuthClient(transport, auth);
            api = new TrainingApi(transport, auth);
            session = new SessionController(api, auth);
            auth.Ended += OnLoginEnded;
            session.SnapshotAccepted += (snapshot, reset) =>
            {
                world.Show(snapshot);
                Render();
                Run(RefreshSessionListIfChanged());
            };
            session.AvailabilityChanged += available => Render();
            session.CommandFinished += outcome =>
            {
                ShowMessage(TrainingPresenter.Outcome(outcome));
                if (outcome.Status == "APPLIED") Run(session.RefreshAsync());
                // The closed road has to be routed around first, which happens on that page
                if (outcome.Status == "REJECTED" && outcome.TargetType == "SESSION" && outcome.FailureCode == "UNKNOWN_CONSTRAINT") workspace?.ShowRoutePlanning();
            };
            session.ErrorOccurred += error => ShowMessage($"Training session: {error}");

            BindLogin(GetComponent<UIDocument>().rootVisualElement);
            BindTraining(GetComponent<UIDocument>().rootVisualElement);
            // A spatial session's Start waits until its mandatory geometry is verified and drawn.
            world = new GameObject("World View").AddComponent<WorldView>();
            world.transform.SetParent(transform, false);
            world.Attach(GetComponent<UIDocument>().rootVisualElement.Q("world"), new SceneLoader(transport),
                MapTiles.FromEnvironment(transport), new TransitClient(transport));
            world.ReadinessChanged += (ready, diagnostic) => Render();
            world.EntitySelected += _ => { if (session.Store.Snapshot != null) RenderFleet(session.Store.Snapshot); };
            workspace = new TrainingWorkspace(GetComponent<UIDocument>().rootVisualElement, api, session, world, Run, text => ShowMessage(text));
            var rootElement = GetComponent<UIDocument>().rootVisualElement;
            workspace.SectionChanged += section =>
            {
                foreach (var name in new[] { "operations", "scenarios", "reports" })
                    rootElement.Q<Button>("section-" + name).EnableInClassList("section-item-selected", name == section);
            };
            foreach (var name in new[] { "operations", "scenarios", "reports" })
                rootElement.Q<Button>("section-" + name).clicked += () => workspace.ShowSection(name);
            workspace.ShowSection("operations");
            workspace.IncidentSelected += () => { if (session.Store.Snapshot != null) RenderFleet(session.Store.Snapshot); };

            // Alerts play for live events the store accepted, and for events a recovery appended.
            var player = new GameObject("Alerts").AddComponent<AlertPlayer>();
            player.transform.SetParent(transform, false);
            var alerts = new TrainingAlerts(player.Play);
            var mute = new MuteSetting(new PlayerPreferences(), alerts);
            var muteToggle = GetComponent<UIDocument>().rootVisualElement.Q<Toggle>("mute");
            muteToggle.SetValueWithoutNotify(mute.Muted);
            muteToggle.RegisterValueChangedCallback(change => mute.Muted = change.newValue);
            session.EventsAccepted += events => alerts.OnEvents(events, true);
            session.RecoveryEventsReceived += events => alerts.OnEvents(events, true);

            // Interface sounds from amee's review.
            var root = GetComponent<UIDocument>().rootVisualElement;
            var uiPlayer = new GameObject("Interface sounds").AddComponent<UiSoundPlayer>();
            uiPlayer.transform.SetParent(transform, false);
            var uiSounds = new UiSounds(uiPlayer.Play);
            uiSounds.Attach(root);
            start.clicked += uiSounds.StartClicked;
            world.RouteNodeSelected += _ => uiSounds.PointPicked();

            ShowLogin(null);
            StartCoroutine(Poll());
        }

        private void OnDisable()
        {
            session?.Shutdown();
            StopAllCoroutines();
        }

        private IEnumerator Poll()
        {
            var wait = new WaitForSecondsRealtime((float)SessionController.PollInterval.TotalSeconds);
            while (true)
            {
                yield return wait;
                if (auth.IsLoggedIn) Run(session.RefreshAsync());
            }
        }

        private void BindLogin(VisualElement root)
        {
            loginScreen = root.Q("login-host");
            username = root.Q<TextField>("username");
            password = root.Q<TextField>("password");
            confirm = root.Q<TextField>("confirm");
            signUpHint = root.Q<Label>("signup-hint");
            loginError = root.Q<Label>("login-error");
            submit = root.Q<Button>("submit");
            modeSwitch = root.Q<Button>("mode-switch");
            submit.clicked += () => Run(SubmitLogin());
            modeSwitch.clicked += () => SetSignUp(!signUp);
            // Enter submits from either field, as in the Qt login. The text field consumes Return
            // itself, so listen on the way down (trickle-down) rather than as the event bubbles up.
            EventCallback<KeyDownEvent> submitOnEnter = e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation();
                Run(SubmitLogin());
            };
            foreach (var field in new[] { username, password, confirm })
                field.RegisterCallback(submitOnEnter, TrickleDown.TrickleDown);
            confirmRow = root.Q("confirm-row");
            username.textEdition.placeholder = "Username";
            password.textEdition.placeholder = "Password";
            confirm.textEdition.placeholder = "Confirm password";
            var showPassword = root.Q<Button>("show-password");
            showPassword.clicked += () =>
            {
                password.isPasswordField = !password.isPasswordField;
                confirm.isPasswordField = password.isPasswordField;
                showPassword.tooltip = password.isPasswordField ? "Show password" : "Hide password";
            };
            forgot = root.Q<Button>("forgot");
            forgot.clicked += () => loginError.text = "Password reset isn't set up yet. Ask your trainer to reset your password.";
            remember = root.Q<Toggle>("remember");
            var remembered = PlayerPrefs.GetString(RememberedUserKey, "");
            remember.SetValueWithoutNotify(remembered.Length > 0);
            username.SetValueWithoutNotify(remembered);
            SetSignUp(false);
        }

        private void BindTraining(VisualElement root)
        {
            trainingScreen = root.Q("training-host");
            title = root.Q<Label>("title");
            user = root.Q<Label>("user");
            capacity = root.Q<Label>("capacity");
            status = root.Q<Label>("status");
            message = root.Q<Label>("message");
            ShowMessage("");
            worldStatus = root.Q<Label>("world-status");
            fleet = root.Q("fleet");
            sessionId = root.Q<TextField>("session-id");
            scenario = root.Q<DropdownField>("scenario");
            sessions = root.Q<DropdownField>("sessions");
            start = root.Q<Button>("start");
            pause = root.Q<Button>("pause");
            resume = root.Q<Button>("resume");
            archive = root.Q<Button>("archive");
            refreshSessions = root.Q<Button>("refresh-sessions");
            fleetPanel = root.Q("fleet-panel");
            fleetTitle = root.Q<Label>("fleet-title");
            fleetEmpty = root.Q("fleet-empty");
            fleet.style.display = DisplayStyle.None;
            fleetSearch = root.Q<TextField>("fleet-search");
            fleetSearch.textEdition.placeholder = "Search shuttle…";
            fleetSearch.RegisterValueChangedCallback(_ => { if (session.Store.Snapshot != null) RenderFleet(session.Store.Snapshot); });
            sessionId.textEdition.placeholder = "Session ID";
            viewMap = root.Q<Button>("view-map");
            viewWorld = root.Q<Button>("view-world");
            viewMap.clicked += () => { world.ShowMap(true); Render(); };
            viewWorld.clicked += () => { world.ShowMap(false); Render(); };
            root.Q<Button>("toggle-fleet").clicked += () =>
                fleetPanel.style.display = fleetPanel.resolvedStyle.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None;

            scenario.choices = TrainingPresenter.Scenarios.Select(s => s.Label).ToList();
            scenario.index = 0;
            scenario.tooltip = string.Join(" ", TrainingPresenter.Scenarios.Select(s => $"{s.Label}: {s.Description}"));
            session.Scenario = TrainingPresenter.Scenarios[0].Id;
            scenario.RegisterValueChangedCallback(_ => { session.Scenario = TrainingPresenter.Scenarios[scenario.index].Id; Render(); });
            sessions.RegisterValueChangedCallback(_ =>
            {
                if (sessions.index >= 0 && sessions.index < listedSessions.Count) Run(Join(listedSessions[sessions.index].SessionId));
            });

            root.Q<Button>("logout").clicked += () =>
            {
                loggingOut = true;
                Run(authClient.LogOutAsync());
            };
            root.Q<Button>("create").clicked += () => Run(Create());
            root.Q<Button>("join").clicked += () => Run(Join(sessionId.value.Trim()));
            root.Q<Button>("refresh").clicked += () => Run(session.RefreshAsync());
            refreshSessions.clicked += () => Run(RefreshSessionList());
            start.clicked += () => Run(session.SendControlAsync("START"));
            pause.clicked += () => Run(session.SendControlAsync("PAUSE"));
            resume.clicked += () => Run(session.SendControlAsync("RESUME"));
            archive.clicked += () => Run(session.SendControlAsync("ARCHIVE"));

            // The clock in the top bar, to the minute
            var clock = root.Q<Label>("clock");
            clock.schedule.Execute(() => clock.text = DateTime.Now.ToString("d MMM yyyy    HH:mm", CultureInfo.InvariantCulture)).Every(10000);
            clock.text = DateTime.Now.ToString("d MMM yyyy    HH:mm", CultureInfo.InvariantCulture);
            Render();
        }

        private void SetSignUp(bool value)
        {
            signUp = value;
            signUpHint.text = value ? LoginMessages.SignUpHint : "";
            signUpHint.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            confirmRow.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            forgot.style.display = value ? DisplayStyle.None : DisplayStyle.Flex;
            modeSwitch.text = value ? "Back to login" : "Create account";
            loginError.text = "";
            SetBusy(false);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            submit.SetEnabled(!value);
            submit.text = signUp ? (value ? "CREATING ACCOUNT..." : "CREATE ACCOUNT") : (value ? "SIGNING IN..." : "LOGIN");
        }

        private async Task SubmitLogin()
        {
            if (busy) return;
            var problem = LoginMessages.Validate(username.value, password.value, signUp, confirm.value);
            if (problem != null)
            {
                loginError.text = problem;
                return;
            }
            SetBusy(true);
            try
            {
                if (signUp) await authClient.SignUpAsync(username.value, password.value);
                else await authClient.LogInAsync(username.value, password.value);
            }
            catch (Exception error)
            {
                loginError.text = LoginMessages.For(error);
                if (error is BackendErrorException backend && backend.Error.Status == 401) password.value = "";
                return;
            }
            finally
            {
                SetBusy(false);
            }
            password.value = confirm.value = "";
            if (remember.value) PlayerPrefs.SetString(RememberedUserKey, username.value.Trim());
            else PlayerPrefs.DeleteKey(RememberedUserKey);
            ShowTraining();
            await session.RefreshAsync(); // resumes a kept session and any open command or creation
            await RefreshSessionList();
        }

        // A logout returns to a clean login screen; an expired login explains that work is kept.
        private void OnLoginEnded()
        {
            ShowLogin(loggingOut ? null : LoginMessages.Expired);
            loggingOut = false;
        }

        private void ShowLogin(string notice)
        {
            loginScreen.style.display = DisplayStyle.Flex;
            trainingScreen.style.display = DisplayStyle.None;
            loginError.text = notice ?? "";
        }

        private void ShowTraining()
        {
            loginScreen.style.display = DisplayStyle.None;
            trainingScreen.style.display = DisplayStyle.Flex;
            user.text = auth.Username;
            Render();
        }

        // The progress note goes once the attempt finishes; a failure has already replaced it
        // with its own message, which stays.
        private async Task Create()
        {
            const string creating = "Creating a training session…";
            ShowMessage(creating);
            await session.CreateAsync();
            if (message.text == creating) ShowMessage("");
        }

        private Task Join(string id)
        {
            ShowMessage("");
            return session.JoinAsync(id);
        }

        private async Task RefreshSessionList()
        {
            refreshSessions.text = "Loading…";
            try
            {
                var list = await api.ListSessionsAsync();
                listedSessions = list.Sessions;
                sessions.SetValueWithoutNotify(listedSessions.Count == 0 ? "No training sessions found" : "Select a session to join");
                sessions.choices = listedSessions.Select(TrainingPresenter.SessionLabel).ToList();
                var selected = listedSessions.FindIndex(s => s.SessionId == session.SessionId);
                if (selected >= 0) sessions.SetValueWithoutNotify(sessions.choices[selected]);
                capacity.text = TrainingPresenter.Capacity(list);
            }
            catch (Exception error) when (!(error is NotLoggedInException))
            {
                capacity.text = "Session list unavailable";
                ShowMessage($"Training sessions: {error.Message}");
            }
            finally
            {
                refreshSessions.text = "Refresh list";
            }
        }

        // The list changes when the joined session or its status does, as in the Qt client.
        private Task RefreshSessionListIfChanged()
        {
            var current = session.Session == null ? null : $"{session.Session.SessionId}/{session.Session.Status}";
            if (current == lastListedState) return Task.CompletedTask;
            lastListedState = current;
            return RefreshSessionList();
        }

        private void Render()
        {
            var snapshot = session.Store.Snapshot;
            var worldReady = world != null && world.Ready;
            var controls = TrainingPresenter.Controls(snapshot, session.Available, worldReady);
            if (world != null)
            {
                worldStatus.text = world.Diagnostic;
                viewMap.EnableInClassList("segment-selected", world.ShowingMap);
                viewWorld.EnableInClassList("segment-selected", !world.ShowingMap);
            }
            start.SetEnabled(controls.Start);
            start.tooltip = controls.StartHint ?? "";
            pause.SetEnabled(controls.Pause);
            resume.SetEnabled(controls.Resume);
            archive.SetEnabled(controls.Archive);
            workspace?.Render();
            world?.SetOffline(snapshot != null && !session.Available);
            if (snapshot == null)
            {
                status.text = TrainingPresenter.NoSession;
                title.text = TrainingPresenter.ScenarioTitle(session.Scenario);
                fleetTitle.text = "Fleet List (0)";
                fleet.Clear();
                fleet.style.display = DisplayStyle.None;
                fleetEmpty.style.display = DisplayStyle.Flex;
                return;
            }
            title.text = TrainingPresenter.Title(snapshot);
            status.text = TrainingPresenter.StatusLine(snapshot, session.Available);
            sessionId.SetValueWithoutNotify(snapshot.SessionId);
            RenderFleet(snapshot);
        }

        private void RenderFleet(FullSnapshot snapshot)
        {
            fleet.Clear();
            var rows = TrainingPresenter.Fleet(snapshot, workspace?.SelectedIncident);
            fleetEmpty.style.display = rows.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            fleet.style.display = rows.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            fleetTitle.text = $"Fleet List ({rows.Count})";
            var search = fleetSearch.value.Trim();
            foreach (var row in rows)
            {
                if (search.Length > 0 && row.ShuttleId.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var id = row.ShuttleId;
                var card = new VisualElement { tooltip = row.Provenance };
                card.AddToClassList("fleet-card");
                card.EnableInClassList("fleet-card-selected", id == world?.SelectedId);
                card.EnableInClassList("fleet-card-incident", row.Incident != null);
                card.RegisterCallback<ClickEvent>(_ => workspace?.SelectVehicle(id));

                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("bus-icon");
                var color = world != null && world.Offline ? FleetMap.FrozenColor : FleetMap.ShuttleColor(row.Index);
                icon.generateVisualContent += mesh => Glyph.DrawBus(mesh.painter2D, new Vector2(18, 18), 36, color);
                card.Add(icon);

                var text = new VisualElement();
                text.AddToClassList("fleet-card-text");
                var top = new VisualElement();
                top.AddToClassList("fleet-card-top");
                top.Add(CardLabel(id, "fleet-card-id"));
                var gauge = new VisualElement();
                gauge.AddToClassList("battery-gauge");
                var fill = new VisualElement();
                fill.AddToClassList("battery-fill");
                fill.style.width = Length.Percent((float)row.BatteryPercent);
                // Amber below 50 %, red below 20 %
                fill.style.backgroundColor = row.BatteryPercent < 20 ? new Color(0.839f, 0.188f, 0.192f)
                    : row.BatteryPercent < 50 ? new Color(0.949f, 0.6f, 0.29f) : new Color(0.153f, 0.682f, 0.376f);
                gauge.Add(fill);
                top.Add(gauge);
                top.Add(new Label($"{Mathf.RoundToInt((float)row.BatteryPercent)}%") { tooltip = row.Battery });
                text.Add(top);

                var status = new VisualElement();
                status.AddToClassList("fleet-card-status");
                var dot = new VisualElement();
                dot.AddToClassList("status-dot");
                dot.style.backgroundColor = StatusColor(row.State);
                status.Add(dot);
                status.Add(new Label(row.Status));
                text.Add(status);

                if (row.Incident != null) text.Add(CardLabel(row.Incident, "fleet-incident"));
                text.Add(CardLabel(row.RouteLine, "fleet-card-line"));
                text.Add(CardLabel(row.NextStop, "fleet-card-line"));
                text.Add(CardLabel(row.Eta, "fleet-card-line"));
                card.Add(text);
                fleet.Add(card);
            }
        }

        private static Label CardLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        private static Color StatusColor(string state)
        {
            switch (state)
            {
                case "IN_SERVICE": case "DETOURING": case "RETURNING_TO_DEPOT": return new Color(0.153f, 0.682f, 0.376f);
                case "BLOCKED": case "AWAITING_GUIDANCE": return new Color(0.839f, 0.188f, 0.192f);
                case "DWELLING": return new Color(0.184f, 0.502f, 0.929f);
                case "QUEUED_FOR_CHARGE": case "CHARGING": return new Color(0.949f, 0.6f, 0.29f);
                case "OUT_OF_SERVICE": return new Color(0.353f, 0.353f, 0.353f);
                default: return new Color(0.549f, 0.549f, 0.549f);
            }
        }

        // Takes no room until there is something to say, so the map and fleet list get the space.
        private void ShowMessage(string text)
        {
            message.text = text;
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // Runs a UI action and reports anything unexpected instead of losing it in a dropped task.
        private async void Run(Task task)
        {
            try { await task; }
            catch (NotLoggedInException) { ShowLogin(LoginMessages.Expired); }
            catch (Exception error)
            {
                Debug.LogException(error);
                if (message != null) ShowMessage($"Unexpected error: {error.Message}");
            }
        }
    }
}
