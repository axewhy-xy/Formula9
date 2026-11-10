/*
 * The Unity client's screens: login, the training shell and the fleet panel.
 *
 * Author: Choo Jian Wei
 * Contributors: Rishika (page layout, fleet cards, map/3D switch),
 *     Lau Xiang Yang (training workspace, session list and cleanup)
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's UI. Wires the UI Toolkit screens to the login and session layers,
 *     the 3D world, the training workspace, the alerts and the interface sounds. Decisions about
 *     what to show live in LoginMessages and TrainingPresenter; this class only moves values on and
 *     off screen. It polls the joined session every second while logged in.
 */

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
    // -----------------------------------------------------------------------------------
    // Class: Formula9App
    // -----------------------------------------------------------------------------------
    // Description:
    //     This component is the client application. It needs a UIDocument holding Formula9App.uxml.
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
        private const int SessionListPage = 50, MaxSessionListLimit = 500, KeptByPurge = 20;
        private int sessionListLimit = SessionListPage;
        private long liveSessions = -1;
        private bool cleanupBusy;
        private Button moreSessions, deleteSession, purgeSessions, reclaimDisk, dialogCancel, dialogConfirm;
        private VisualElement dialogOverlay;
        private Label dialogText;
        private Func<Task> dialogAction;

        private VisualElement loginScreen, trainingScreen, fleet, confirmRow, fleetPanel, sessionPlayback;
        private TextField fleetSearch;
        private VisualElement fleetEmpty;
        private Label fleetTitle;
        private Toggle remember;
        private Button viewMap, viewWorld;
        private const string RememberedUserKey = "login.rememberedUser";
        private TextField username, password, confirm, sessionId;
        private Label signUpHint, loginError, title, user, capacity, status, message, sessionBadge;
        private string sessionBadgeClass;
        private readonly SessionStatusPresenter sessionStatusPresenter = new SessionStatusPresenter();
        private Button forgot, submit, modeSwitch, start, pause, resume, archive, refreshSessions;
        private DropdownField scenario, sessions;

        private string incidentHistorySessionId;
        private WorldView world;
        private TrainingWorkspace workspace;

        // -----------------------------------------------------------------------------------
        // Function: OnEnable
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds the client: the backend clients and session controller, the
        //     screens, the 3D world, the training workspace, the alerts with their saved mute
        //     setting, and the interface sounds; then it shows the login screen and starts polling.
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
                // A newly joined session starts its live event cursor at the
                // snapshot sequence. Fetch older incident locations separately.
                if (snapshot != null && incidentHistorySessionId != snapshot.SessionId)
                {
                    incidentHistorySessionId = snapshot.SessionId;
                    Run(LoadIncidentMapHistory(snapshot.SessionId, snapshot.EventSequence));
                }
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
            session.SessionLeft += () =>
            {
                world.Show(null);
                incidentHistorySessionId = null;
                sessionId.SetValueWithoutNotify("");
                lastListedState = null;
                Render();
                Run(RefreshSessionList());
            };

            BindLogin(GetComponent<UIDocument>().rootVisualElement);
            BindTraining(GetComponent<UIDocument>().rootVisualElement);
            // A spatial session's Start waits until its mandatory geometry is verified and drawn.
            world = new GameObject("World View").AddComponent<WorldView>();
            world.transform.SetParent(transform, false);
            world.Attach(GetComponent<UIDocument>().rootVisualElement.Q("world"), new SceneLoader(transport),
                MapTiles.FromEnvironment(transport), new TransitClient(transport), new CityClient(transport));
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
            world.IncidentSelected += id => workspace.SelectIncident(id);
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
            session.EventsAccepted += _ => { if (session.Store.Snapshot != null) world.SetIncidentEvents(session.Store.Events); };
            session.RecoveryEventsReceived += _ => { if (session.Store.Snapshot != null) world.SetIncidentEvents(session.Store.Events); };
            session.RecoveryEventsReceived += events => alerts.OnEvents(events, true);
            // No event says a battery is low, so the snapshots are watched for it.
            var battery = new BatteryWatch(player.Play, () => alerts.Muted);
            session.SnapshotAccepted += (snapshot, _) => battery.OnSnapshot(snapshot);
            session.ConnectionChanged += connected =>
            {
                // The reconnect cue plays on the same snapshot; a battery that ran low meanwhile waits a poll.
                if (connected) battery.HoldNextCue();
                if (!alerts.Muted) player.Play(connected ? AlertPlayer.ReconnectCue : AlertPlayer.DisconnectCue);
            };

            // Interface sounds from amee's consultation with Vuk: only the major buttons click.
            var root = GetComponent<UIDocument>().rootVisualElement;
            var uiPlayer = new GameObject("Interface sounds").AddComponent<UiSoundPlayer>();
            uiPlayer.transform.SetParent(transform, false);
            var uiSounds = new UiSounds(uiPlayer.Play);
            foreach (var name in UiSounds.MajorButtons) root.Q<Button>(name).clicked += uiSounds.MajorPressed;
            world.RouteNodeSelected += _ => uiSounds.PointPicked();
            world.RoadClicked += (_, __) => uiSounds.PointPicked();
            world.ViewChanged += Render;

            ShowLogin(null);
            StartCoroutine(Poll());
        }

        // -----------------------------------------------------------------------------------
        // Function: OnDisable
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function stops the session controller and polling.
        private void OnDisable()
        {
            session?.Shutdown();
            StopAllCoroutines();
        }

        // -----------------------------------------------------------------------------------
        // Function: Poll
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function refreshes the session once per poll interval while logged in.
        //
        // Returns:
        //     IEnumerator: The coroutine.
        private IEnumerator Poll()
        {
            var wait = new WaitForSecondsRealtime((float)SessionController.PollInterval.TotalSeconds);
            while (true)
            {
                yield return wait;
                if (auth.IsLoggedIn) Run(session.RefreshAsync());
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: BindLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects the login page's controls. Enter submits from either field, as
        //     in the Qt login; the text field consumes Return itself, so it is heard on the way
        //     down the tree.
        //
        // Args:
        //     root (VisualElement): The document's root.
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

        // -----------------------------------------------------------------------------------
        // Function: BindTraining
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects the training shell's controls: session create, join and list,
        //     the session controls, the cleanup dialog, the fleet panel and the clock in the top
        //     bar.
        //
        // Args:
        //     root (VisualElement): The document's root.
        private void BindTraining(VisualElement root)
        {
            trainingScreen = root.Q("training-host");
            title = root.Q<Label>("title");
            user = root.Q<Label>("user");
            capacity = root.Q<Label>("capacity");
            status = root.Q<Label>("status");
            sessionBadge = root.Q<Label>("session-badge");
            message = root.Q<Label>("message");
            ShowMessage("");
            fleet = root.Q("fleet");
            sessionId = root.Q<TextField>("session-id");
            scenario = root.Q<DropdownField>("scenario");
            sessions = root.Q<DropdownField>("sessions");
            start = root.Q<Button>("start");
            pause = root.Q<Button>("pause");
            resume = root.Q<Button>("resume");
            archive = root.Q<Button>("archive");
            sessionPlayback = root.Q("session-playback");
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
                RenderCleanup();
                if (SelectedEntry != null) Run(Join(SelectedEntry.SessionId));
            });
            moreSessions = root.Q<Button>("more-sessions");
            moreSessions.clicked += () =>
            {
                sessionListLimit = Math.Min(sessionListLimit + SessionListPage, MaxSessionListLimit);
                Run(RefreshSessionList());
            };
            deleteSession = root.Q<Button>("delete-session");
            purgeSessions = root.Q<Button>("purge-sessions");
            reclaimDisk = root.Q<Button>("reclaim-disk");
            deleteSession.clicked += ConfirmDeleteSession;
            purgeSessions.clicked += ConfirmPurgeSessions;
            reclaimDisk.clicked += () => Run(Cleanup("Reclaiming disk space…", async () => TrainingPresenter.Reclaimed(await api.CompactSessionsAsync())));
            dialogOverlay = root.Q("dialog-overlay");
            dialogText = root.Q<Label>("dialog-text");
            dialogCancel = root.Q<Button>("dialog-cancel");
            dialogConfirm = root.Q<Button>("dialog-confirm");
            dialogCancel.clicked += CloseDialog;
            dialogConfirm.clicked += () =>
            {
                var confirmed = dialogAction;
                CloseDialog();
                if (confirmed != null) Run(confirmed());
            };
            dialogOverlay.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Escape) CloseDialog(); }, TrickleDown.TrickleDown);
            RenderCleanup();

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
            archive.clicked += ConfirmArchive;

            // The clock in the top bar, to the minute
            var clock = root.Q<Label>("clock");
            clock.schedule.Execute(() => clock.text = DateTime.Now.ToString("d MMM yyyy    HH:mm", CultureInfo.InvariantCulture)).Every(10000);
            clock.text = DateTime.Now.ToString("d MMM yyyy    HH:mm", CultureInfo.InvariantCulture);
            Render();
            var toggleBtn = root.Q<Button>("nav-toggle-btn");
            var navPanel = root.Q("nav-panel");
            bool isCollapsed = false;
            toggleBtn.clicked+=() =>
            {
                isCollapsed = !isCollapsed;
                navPanel?.EnableInClassList("collapsed", isCollapsed);
            };
        }

        // -----------------------------------------------------------------------------------
        // Function: SetSignUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function switches the login page between logging in and creating an account.
        //
        // Args:
        //     value (bool): True to create an account.
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

        // -----------------------------------------------------------------------------------
        // Function: SetBusy
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function disables the submit button and changes its text while a login is sent.
        //
        // Args:
        //     value (bool): True while sending.
        private void SetBusy(bool value)
        {
            busy = value;
            submit.SetEnabled(!value);
            submit.text = signUp ? (value ? "CREATING ACCOUNT..." : "CREATE ACCOUNT") : (value ? "SIGNING IN..." : "LOGIN");
        }

        // -----------------------------------------------------------------------------------
        // Function: SubmitLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks and sends the login or sign-up, remembers the username if asked,
        //     then shows the training screen, resumes a kept session (with any open command or
        //     creation) and loads the session list. A wrong password is cleared.
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

        // -----------------------------------------------------------------------------------
        // Function: OnLoginEnded
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function returns to the login screen when the login ends. A logout returns to a
        //     clean login screen; an expired login explains that the work is kept.
        private void OnLoginEnded()
        {
            ShowLogin(loggingOut ? null : LoginMessages.Expired);
            loggingOut = false;
        }

        // -----------------------------------------------------------------------------------
        // Function: ShowLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the login screen.
        //
        // Args:
        //     notice (string): A message to show on it, or null.
        private void ShowLogin(string notice)
        {
            CloseDialog();
            loginScreen.style.display = DisplayStyle.Flex;
            trainingScreen.style.display = DisplayStyle.None;
            loginError.text = notice ?? "";
        }

        // -----------------------------------------------------------------------------------
        // Function: ShowTraining
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the training screen for the logged-in user.
        private void ShowTraining()
        {
            loginScreen.style.display = DisplayStyle.None;
            trainingScreen.style.display = DisplayStyle.Flex;
            user.text = auth.Username;
            Render();
        }

        // -----------------------------------------------------------------------------------
        // Function: Create
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates a training session, showing progress meanwhile. The progress
        //     note goes once the attempt finishes; a failure has already replaced it with its own
        //     message, which stays.
        private async Task Create()
        {
            const string creating = "Creating a training session…";
            ShowMessage(creating);
            await session.CreateAsync();
            if (message.text == creating) ShowMessage("");
        }

        // -----------------------------------------------------------------------------------
        // Function: Join
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function joins a session by ID.
        //
        // Args:
        //     id (string): The session ID.
        //
        // Returns:
        //     Task: The join.
        private Task Join(string id)
        {
            ShowMessage("");
            return session.JoinAsync(id);
        }

        private SessionListEntry SelectedEntry =>
            sessions.index >= 0 && sessions.index < listedSessions.Count ? listedSessions[sessions.index] : null;

        // -----------------------------------------------------------------------------------
        // Function: RenderCleanup
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function enables the cleanup buttons that apply: delete for a finished session,
        //     purge, and reclaim disk space only when no session is live.
        private void RenderCleanup()
        {
            deleteSession.SetEnabled(!cleanupBusy && TrainingPresenter.CanDelete(SelectedEntry));
            purgeSessions.SetEnabled(!cleanupBusy);
            reclaimDisk.SetEnabled(!cleanupBusy && liveSessions == 0);
        }

        // -----------------------------------------------------------------------------------
        // Function: ConfirmDeleteSession
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function asks before permanently deleting the selected session, and leaves it if
        //     it is the joined one.
        private void ConfirmDeleteSession()
        {
            var id = SelectedEntry?.SessionId;
            if (id == null) return;
            Confirm($"Permanently delete session {id}? Its history is removed for everyone. This cannot be undone.", "Delete permanently", () => Cleanup(
                $"Deleting session {id}…",
                async () =>
                {
                    await api.DeleteSessionAsync(id);
                    if (session.Store.SelectedSessionId == id) session.Leave();
                    return $"Session {id} was permanently deleted.";
                }));
        }

        // -----------------------------------------------------------------------------------
        // Function: ConfirmPurgeSessions
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function asks before deleting every finished session except the most recent
        //     ones.
        private void ConfirmPurgeSessions() => Confirm(
            $"Permanently delete every archived, completed or failed session except the {KeptByPurge} most recent? Their history is removed for everyone. This cannot be undone.",
            "Delete old sessions", () => Cleanup("Deleting old sessions…", async () =>
            {
                var deleted = await api.PurgeSessionsAsync(KeptByPurge);
                return deleted == 0 ? "No old sessions to delete." : $"Deleted {deleted} old training sessions.";
            }));

        // -----------------------------------------------------------------------------------
        // Function: ConfirmArchive
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function asks before archiving the joined session. Archive sits next to Start,
        //     Pause and Resume, so a slip must not end the attempt. If the session can no longer
        //     be archived by the time the trainee confirms, nothing is sent.
        private void ConfirmArchive() => Confirm(
            "Archive this session? The attempt ends and its runtime slot is released. Its history remains available.",
            "Archive session", () => archive.enabledSelf ? session.SendControlAsync("ARCHIVE") : Task.CompletedTask);

        // -----------------------------------------------------------------------------------
        // Function: Confirm
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the confirmation dialog. Cancel holds the focus, so the default
        //     answer to a destructive question is no.
        //
        // Args:
        //     text (string): The question.
        //     action (string): The confirm button's text.
        //     confirmed (Func<Task>): What to do if confirmed.
        private void Confirm(string text, string action, Func<Task> confirmed)
        {
            dialogText.text = text;
            dialogConfirm.text = action;
            dialogAction = confirmed;
            dialogOverlay.style.display = DisplayStyle.Flex;
            dialogCancel.Focus();
        }

        // -----------------------------------------------------------------------------------
        // Function: CloseDialog
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function closes the confirmation dialog without acting.
        private void CloseDialog()
        {
            dialogAction = null;
            dialogOverlay.style.display = DisplayStyle.None;
        }

        // -----------------------------------------------------------------------------------
        // Function: Cleanup
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function runs one cleanup action with the buttons disabled, reports its result,
        //     and reloads the session list. If no reply arrived the action may still complete,
        //     which the message says.
        //
        // Args:
        //     progress (string): What to show meanwhile.
        //     work (Func<Task<string>>): The action; returns its result message.
        private async Task Cleanup(string progress, Func<Task<string>> work)
        {
            cleanupBusy = true;
            RenderCleanup();
            ShowMessage(progress);
            try { ShowMessage(await work()); }
            catch (Exception error) when (!(error is NotLoggedInException))
            {
                ShowMessage($"Cleanup did not finish: {error.Message} If no reply arrived it may still complete; refresh the list to check.");
            }
            finally
            {
                cleanupBusy = false;
                RenderCleanup();
            }
            await RefreshSessionList();
        }

        // -----------------------------------------------------------------------------------
        // Function: RefreshSessionList
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function loads the session list into the dropdown, keeping the joined session
        //     selected, and updates the capacity line.
        private async Task RefreshSessionList()
        {
            refreshSessions.text = "Loading…";
            try
            {
                var list = await api.ListSessionsAsync(sessionListLimit);
                listedSessions = list.Sessions;
                liveSessions = list.LiveCount;
                moreSessions.style.display = list.Sessions.Count < list.Total && sessionListLimit < MaxSessionListLimit ? DisplayStyle.Flex : DisplayStyle.None;
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
                RenderCleanup();
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: RefreshSessionListIfChanged
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reloads the session list when the joined session or its status changed,
        //     as in the Qt client.
        //
        // Returns:
        //     Task: The reload, or a completed task when nothing changed.
        private Task RefreshSessionListIfChanged()
        {
            var current = session.Session == null ? null : $"{session.Session.SessionId}/{session.Session.Status}";
            if (current == lastListedState) return Task.CompletedTask;
            lastListedState = current;
            return RefreshSessionList();
        }

        // -----------------------------------------------------------------------------------
        // Function: Render
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the current state: view switch, session
        //     controls, the workspace, the status line and the fleet panel.
        private void Render()
        {
            var snapshot = session.Store.Snapshot;
            var worldReady = world != null && world.Ready;
            var controls = TrainingPresenter.Controls(snapshot, session.Available, worldReady);
            start.style.display = controls.StartVisible ? DisplayStyle.Flex : DisplayStyle.None;
            pause.style.display = controls.PauseVisible ? DisplayStyle.Flex : DisplayStyle.None;
            resume.style.display = controls.ResumeVisible ? DisplayStyle.Flex : DisplayStyle.None;
            sessionPlayback.style.display = controls.PlaybackVisible ? DisplayStyle.Flex : DisplayStyle.None;
            if (world != null)
            {
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
            RenderSessionStatus(snapshot);
            if (snapshot == null)
            {
                title.text = TrainingPresenter.ScenarioTitle(session.Scenario);
                fleetTitle.text = "Fleet List (0)";
                fleet.Clear();
                fleet.style.display = DisplayStyle.None;
                fleetEmpty.style.display = DisplayStyle.Flex;
                return;
            }
            title.text = TrainingPresenter.Title(snapshot);
            sessionId.SetValueWithoutNotify(snapshot.SessionId);
            RenderFleet(snapshot);
        }

        // Keep the operator's state readable: badge, tooltip and one-line summary.
        private void RenderSessionStatus(FullSnapshot snapshot)
        {
            var view = sessionStatusPresenter.Present(snapshot, session.Available);
            if (sessionBadge.text != view.BadgeText) sessionBadge.text = view.BadgeText;
            if (sessionBadgeClass != view.BadgeClass)
            {
                if (sessionBadgeClass != null) sessionBadge.RemoveFromClassList(sessionBadgeClass);
                sessionBadge.AddToClassList(view.BadgeClass);
                sessionBadgeClass = view.BadgeClass;
            }
            if (sessionBadge.tooltip != view.Tooltip) sessionBadge.tooltip = view.Tooltip;
            if (status.text != view.Summary) status.text = view.Summary;
            status.EnableInClassList("session-summary-offline", view.Offline);
        }

        // -----------------------------------------------------------------------------------
        // Function: RenderFleet
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function draws the fleet cards matching the search: icon in the shuttle's map
        //     colour, battery gauge (amber below 50 %, red below 20 %), status, incident, route,
        //     next stop and ETA.
        //
        // Args:
        //     snapshot (FullSnapshot): The accepted snapshot.
        private void RenderFleet(FullSnapshot snapshot)
        {
            world.SetIncidentEvents(session.Store.Events);
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
                top.Add(new Label($"{row.BatteryPercent:0}%") { tooltip = row.Battery });
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

        // -----------------------------------------------------------------------------------
        // Function: CardLabel
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a styled label for a fleet card.
        //
        // Args:
        //     text (string): The text.
        //     className (string): The USS class.
        //
        // Returns:
        //     Label: The label.
        private static Label CardLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        // -----------------------------------------------------------------------------------
        // Function: StatusColor
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function picks the status dot colour: green when moving, red when stopped, blue
        //     at a stop, amber when charging, grey otherwise.
        //
        // Args:
        //     state (string): The service state.
        //
        // Returns:
        //     Color: The colour.
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

        // -----------------------------------------------------------------------------------
        // Function: ShowMessage
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows a message line that takes no room until there is something to
        //     say, so the map and fleet list get the space.
        //
        // Args:
        //     text (string): The message, or empty to hide it.
        private void ShowMessage(string text)
        {
            message.text = text;
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // Replays only for map placement, without altering the SessionController's
        // authoritative event cursor or the incident decision workflow.
        private async Task LoadIncidentMapHistory(string targetSessionId, long throughSequence)
        {
            long cursor = 0;
            int batches = 0;
            while (cursor < throughSequence && batches++ < 100)
            {
                var reply = await api.GetEventsAsync(targetSessionId, cursor, 100);
                if (incidentHistorySessionId != targetSessionId ||
                    session.Store.Snapshot?.SessionId != targetSessionId)
                    return;
                if (reply.Events != null && reply.Events.Count > 0)
                    world.SetIncidentEvents(reply.Events);
                if (reply.NextSequence <= cursor) break;
                cursor = reply.NextSequence;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Run
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function runs a UI action and reports anything unexpected instead of losing it
        //     in a dropped task. An expired login returns to the login screen.
        //
        // Args:
        //     task (Task): The action.
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
