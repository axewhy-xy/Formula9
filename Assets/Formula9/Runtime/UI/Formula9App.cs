using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
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

        // 3D readiness arrives from the world view in a later step; until then 3D sessions wait.
        public bool SpatialReady { get; set; }

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
            password.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return) Run(SubmitLogin()); });
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
            root.Q<Button>("create").clicked += () => { message.text = "Creating a training session…"; Run(session.CreateAsync()); };
            root.Q<Button>("join").clicked += () => Run(Join(sessionId.value.Trim()));
            root.Q<Button>("refresh").clicked += () => Run(session.RefreshAsync());
            refreshSessions.clicked += () => Run(RefreshSessionList());
            start.clicked += () => Run(session.SendControlAsync("START"));
            pause.clicked += () => Run(session.SendControlAsync("PAUSE"));
            resume.clicked += () => Run(session.SendControlAsync("RESUME"));
            archive.clicked += () => Run(session.SendControlAsync("ARCHIVE"));

            var header = root.Q("fleet-header");
            foreach (var column in FleetColumns) header.Add(Cell(column));
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
            var controls = TrainingPresenter.Controls(snapshot, session.Available, SpatialReady);
            start.SetEnabled(controls.Start);
            start.tooltip = controls.StartHint ?? "";
            pause.SetEnabled(controls.Pause);
            resume.SetEnabled(controls.Resume);
            archive.SetEnabled(controls.Archive);
            if (snapshot == null)
            {
                status.text = TrainingPresenter.NoSession;
                return;
            }
            title.text = TrainingPresenter.Title(snapshot);
            status.text = TrainingPresenter.StatusLine(snapshot, session.Available);
            sessionId.SetValueWithoutNotify(snapshot.SessionId);
            worldStatus.text = snapshot.Spatial != null
                ? "3D world view arrives in the next step of the Unity port."
                : "This 2D exercise has no 3D world; the fleet list shows its shuttles.";
            fleet.Clear();
            foreach (var row in TrainingPresenter.Fleet(snapshot))
            {
                var line = new VisualElement();
                line.AddToClassList("fleet-row");
                foreach (var value in new[] { row.ShuttleId, row.State, row.Route, row.Battery, row.Provenance }) line.Add(Cell(value));
                fleet.Add(line);
            }
        }

        private static Label Cell(string text)
        {
            var cell = new Label(text);
            cell.AddToClassList("fleet-cell");
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
