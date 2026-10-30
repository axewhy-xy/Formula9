using System;
using System.Collections;
using System.Collections.Generic;
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
        private static readonly string[] FleetColumns = { "Shuttle", "State", "Route / revision", "Battery", "Provenance" };

        private AuthSession auth;
        private AuthClient authClient;
        private TrainingApi api;
        private SessionController session;
        private bool signUp, busy, loggingOut;
        private List<SessionListEntry> listedSessions = new List<SessionListEntry>();
        private string lastListedState;

        private VisualElement loginScreen, trainingScreen, fleet;
        private TextField username, password, confirm, sessionId;
        private Label signUpHint, loginError, title, user, capacity, status, message, worldStatus;
        private Button submit, modeSwitch, start, pause, resume, archive, refreshSessions;
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
                message.text = TrainingPresenter.Outcome(outcome);
                if (outcome.Status == "APPLIED") Run(session.RefreshAsync());
            };
            session.ErrorOccurred += error => message.text = $"Training session: {error}";

            BindLogin(GetComponent<UIDocument>().rootVisualElement);
            BindTraining(GetComponent<UIDocument>().rootVisualElement);
            // A spatial session's Start waits until its mandatory geometry is verified and drawn.
            world = new GameObject("World View").AddComponent<WorldView>();
            world.transform.SetParent(transform, false);
            world.Attach(GetComponent<UIDocument>().rootVisualElement.Q("world"), new SceneLoader(transport));
            world.ReadinessChanged += (ready, diagnostic) => Render();
            workspace = new TrainingWorkspace(GetComponent<UIDocument>().rootVisualElement, api, session, world, Run, text => message.text = text);

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

            // Interface sounds from amee's review, attached once every screen and control exists.
            var root = GetComponent<UIDocument>().rootVisualElement;
            var uiPlayer = new GameObject("Interface sounds").AddComponent<UiSoundPlayer>();
            uiPlayer.transform.SetParent(transform, false);
            var uiSounds = new UiSounds(uiPlayer.Play);
            uiSounds.Attach(root);
            start.clicked += uiSounds.StartClicked;
            root.Q<FleetMap>("route-map").NodeSelected += _ => uiSounds.PointPicked();

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
            loginScreen = root.Q("login");
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
            SetSignUp(false);
        }

        private void BindTraining(VisualElement root)
        {
            trainingScreen = root.Q("training");
            title = root.Q<Label>("title");
            user = root.Q<Label>("user");
            capacity = root.Q<Label>("capacity");
            status = root.Q<Label>("status");
            message = root.Q<Label>("message");
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

            scenario.choices = TrainingPresenter.Scenarios.Select(s => s.Label).ToList();
            scenario.index = 0;
            scenario.tooltip = string.Join(" ", TrainingPresenter.Scenarios.Select(s => $"{s.Label}: {s.Description}"));
            scenario.RegisterValueChangedCallback(_ => session.Scenario = TrainingPresenter.Scenarios[scenario.index].Id);
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

            var header = root.Q("fleet-header");
            for (var column = 0; column < FleetColumns.Length; column++) header.Add(Cell(FleetColumns[column], column));
            Render();
        }

        private void SetSignUp(bool value)
        {
            signUp = value;
            signUpHint.text = value ? LoginMessages.SignUpHint : "";
            signUpHint.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            confirm.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
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
            message.text = creating;
            await session.CreateAsync();
            if (message.text == creating) message.text = "";
        }

        private Task Join(string id)
        {
            message.text = "";
            return session.JoinAsync(id);
        }

        private async Task RefreshSessionList()
        {
            refreshSessions.text = "Loading…";
            try
            {
                var list = await api.ListSessionsAsync();
                listedSessions = list.Sessions;
                sessions.SetValueWithoutNotify(null);
                sessions.choices = listedSessions.Select(TrainingPresenter.SessionLabel).ToList();
                var selected = listedSessions.FindIndex(s => s.SessionId == session.SessionId);
                if (selected >= 0) sessions.SetValueWithoutNotify(sessions.choices[selected]);
                capacity.text = TrainingPresenter.Capacity(list);
            }
            catch (Exception error) when (!(error is NotLoggedInException))
            {
                capacity.text = "Session list unavailable";
                message.text = $"Training sessions: {error.Message}";
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
                // The diagnostic covers the view until the world is drawn, then gets out of the way.
                worldStatus.text = world.Diagnostic;
                worldStatus.style.display = worldReady ? DisplayStyle.None : DisplayStyle.Flex;
            }
            start.SetEnabled(controls.Start);
            start.tooltip = controls.StartHint ?? "";
            pause.SetEnabled(controls.Pause);
            resume.SetEnabled(controls.Resume);
            archive.SetEnabled(controls.Archive);
            workspace?.Render();
            if (snapshot == null)
            {
                status.text = TrainingPresenter.NoSession;
                return;
            }
            title.text = TrainingPresenter.Title(snapshot);
            status.text = TrainingPresenter.StatusLine(snapshot, session.Available);
            sessionId.SetValueWithoutNotify(snapshot.SessionId);
            fleet.Clear();
            foreach (var row in TrainingPresenter.Fleet(snapshot))
            {
                var line = new VisualElement();
                line.AddToClassList("fleet-row");
                var id = row.ShuttleId;
                line.RegisterCallback<ClickEvent>(_ => workspace?.SelectVehicle(id));
                var values = new[] { row.ShuttleId, row.State, row.Route, row.Battery, row.Provenance };
                for (var column = 0; column < values.Length; column++) line.Add(Cell(values[column], column));
                fleet.Add(line);
            }
        }

        // Each column has its own class so header and rows share widths; long values end in "…"
        // and the full text is in the tooltip.
        private static Label Cell(string text, int column)
        {
            var cell = new Label(text) { tooltip = text };
            cell.AddToClassList("fleet-cell");
            cell.AddToClassList($"fleet-col-{column}");
            return cell;
        }

        // Runs a UI action and reports anything unexpected instead of losing it in a dropped task.
        private async void Run(Task task)
        {
            try { await task; }
            catch (NotLoggedInException) { ShowLogin(LoginMessages.Expired); }
            catch (Exception error)
            {
                Debug.LogException(error);
                if (message != null) message.text = $"Unexpected error: {error.Message}";
            }
        }
    }
}
