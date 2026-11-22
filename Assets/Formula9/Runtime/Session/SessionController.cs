/*
 * Creates, joins and polls a training session, and sends its commands.
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (leaving a deleted session, the session stream)
 * Date Created: 30/10/2026
 * Last Modified: 18/11/2026
 *
 * Description:
 *     Part of the Unity client's session layer. A port of the training half of the Qt client's
 *     BackendClient. It creates sessions with a retry key kept per owner, joins and polls the
 *     joined session every second, replays events from a committed cursor, fetches events a
 *     recovery appended after an epoch change, and resynchronises from a fresh snapshot when an
 *     event batch does not fit. It holds at most one open command: a lost reply is confirmed by
 *     resending the same command, or by asking for its status after a recovery, backing off to at
 *     most 5 s, and the command waits for its own account across logins. Call it from Unity's main
 *     thread.
 *
 *     With UseStream on, PumpStream keeps a WebSocket stream of the joined session open
 *     (Backend/SessionStream.cs): the backend pushes its newer snapshots and its traffic frames as
 *     they are made, and the poll fetches no snapshot while the stream delivers. Anything the stream
 *     cannot say ends it, and the poll takes over until a new stream delivers again.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;

namespace Formula9.Session
{
    // -----------------------------------------------------------------------------------
    // Class: CommandOutcome
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class says what happened to a submitted command.
    //
    //     Attributes: the command's IDs, Status, target, AppliedTick and FailureCode; Confirmed is
    //     false for outcomes the client had to conclude itself (NEEDS_RECONFIRMATION) because the
    //     backend could no longer confirm them.
    public sealed class CommandOutcome
    {
        public string CommandId, IdempotencyKey, Status, TargetType, TargetId, FailureCode, CommandType;
        public long TargetRevision;
        public long? AppliedTick;
        public bool Confirmed;

        // -----------------------------------------------------------------------------------
        // Function: CommandOutcome.From
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes an outcome from the backend's command status.
        //
        // Args:
        //     status (CommandStatus): The journaled status.
        //
        // Returns:
        //     CommandOutcome: The confirmed outcome.
        internal static CommandOutcome From(CommandStatus status) => new CommandOutcome
        {
            CommandId = status.CommandId, IdempotencyKey = status.IdempotencyKey, Status = status.Status,
            TargetType = status.TargetType, TargetId = status.TargetId, TargetRevision = status.TargetRevision,
            FailureCode = status.FailureCode, AppliedTick = status.AppliedTick, Confirmed = true,
        };
    }

    // -----------------------------------------------------------------------------------
    // Class: SessionController
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class owns the joined training session: creation, join, the 1 s poll, event catch-up
    //     and the one command whose outcome is still open. Drive RefreshAsync from a 1 s timer.
    //
    //     Attributes: Store, SessionId, Session and Available describe the joined session;
    //     HasPendingCommand and CreationPending tell whether a command or creation is unresolved;
    //     Scenario is used for the next creation (an unresolved creation keeps the one it started
    //     with). Events report accepted snapshots and events, recovery events, availability, the
    //     backend dropping and coming back while a session is joined, command outcomes, the command ledger (including submissions with an unknown outcome and
    //     complete refusals, so a report never silently omits a command), errors, and leaving a
    //     session.
    public sealed class SessionController
    {
        public const string IntegratedScenario = "scenario-m2-integrated-r1";
        public const string ScoredScenario = "scenario-toa-payoh-scored-r1";
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        // The backend's traffic moves every tenth of a second. The 3D city asks for it again as soon as
        // each reply comes, and the backend holds a request this long for the next frame, so each frame
        // arrives as it is made instead of up to a polling interval late.
        public static readonly TimeSpan TrafficWait = TimeSpan.FromSeconds(1);
        // After a traffic request fails, or while there is nothing to ask, the next waits this long
        public static readonly TimeSpan TrafficRetryInterval = TimeSpan.FromSeconds(0.25);
        // A session and epoch the backend has no traffic for is asked again only after this long: it may
        // have had none for the moment only, as while the backend was still recovering it
        public static readonly TimeSpan NoTrafficRetryInterval = TimeSpan.FromSeconds(5);
        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9_.:-]*$");
        private static readonly HashSet<string> SessionControls = new HashSet<string> { "START", "PAUSE", "RESUME", "ARCHIVE" };
        // Archive all lists one page: live sessions come first, and at most the capacity (16) are live
        private const int ArchiveListPage = 50;
        // A queued ARCHIVE is applied within a tick or two, so Archive all looks again this many times,
        // this far apart, before counting a session as still live
        private const int ArchiveChecks = 5;
        public static readonly TimeSpan ArchiveCheckInterval = TimeSpan.FromSeconds(0.5);
        // The backend pushes the joined session's newer snapshots at most this often over its stream,
        // instead of the 1 s poll: a quarter of a second keeps the panels current without redrawing
        // them at the backend's ten commits a second
        public static readonly TimeSpan StreamSnapshotInterval = TimeSpan.FromMilliseconds(250);
        // After a stream ends, the next is tried this long later, doubling while none delivers up to
        // MaxStreamRetryInterval: a backend without streams is polled, as before, at no extra cost
        public static readonly TimeSpan StreamRetryInterval = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan MaxStreamRetryInterval = TimeSpan.FromSeconds(60);
        // A stream heard nothing from for this long (the backend sends a heartbeat every 5 s) is taken
        // as lost, so a connection that died silently does not stop the updates
        public static readonly TimeSpan StreamSilenceLimit = TimeSpan.FromSeconds(15);

        // -----------------------------------------------------------------------------------
        // Class: PendingCommand
        // -----------------------------------------------------------------------------------
        // Description:
        //     This class is the one command whose outcome is still open.
        //
        //     Attributes: Command; Approval for a route approval, which uses its own endpoint;
        //     StatusOnly once the command may have been received, so it is only confirmed, never
        //     resent; Owner, the account it belongs to.
        private sealed class PendingCommand
        {
            public DispatcherCommand Command;
            public RouteApprovalRequest Approval;
            public bool StatusOnly;
            public string Owner;
        }

        private readonly TrainingApi api;
        private readonly AuthSession auth;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Func<DateTime> now;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        private long generation;
        private long epoch = -1;
        private long eventCursor = -1;
        private bool resyncPending;
        // True after a poll of the joined session failed, until one succeeds again.
        private bool connectionLost;
        private string joiningSessionId;
        private object snapshotRequest, eventsRequest, commandRequest, createRequest, trafficRequest;
        // The newest traffic frame taken, for the session and epoch named by trafficKey; and a session
        // and epoch the backend had no traffic for, which is not asked again until noTrafficUntil
        private long trafficCursor = -1;
        private string trafficKey, noTrafficKey;
        private DateTime noTrafficUntil;
        private string creationKey, creationOwner, creationScenario;
        private PendingCommand pending;
        // The joined session's stream, the join it belongs to, whether it has delivered a snapshot and
        // whether it was told to send traffic; when it was last heard from; when to try the next, and
        // how long to wait after that one ends without delivering
        private SessionStream stream;
        private long streamGeneration;
        private bool streamDelivered, streamTraffic;
        private DateTime streamHeard, streamRetryAt;
        private TimeSpan streamBackoff = StreamRetryInterval;
        // Snapshots the stream delivered, so a poll reply that crossed one is not taken for a stale one
        private long snapshotsPushed;

        public SessionStore Store { get; }
        public string SessionId { get; private set; }
        public TrainingSession Session { get; private set; }
        public bool Available { get; private set; }
        public bool HasPendingCommand => pending != null;
        public bool CreationPending => creationKey != null;
        internal string PendingCreationScenario => creationScenario;
        // Assessment results belong to the account even when the joined session stays selected after logout.
        internal string SignedInUsername => auth.IsLoggedIn ? auth.Username : null;
        // Scenario for the next creation; an unresolved creation keeps the one it started with.
        public string Scenario { get; set; } = IntegratedScenario;
        // Whether PumpStream keeps a stream of the joined session open. Off by default, so a caller
        // that never pumps polls exactly as before; the app turns it on.
        public bool UseStream { get; set; }
        // True while the joined session's stream delivers its snapshots (and traffic, if asked for):
        // RefreshAsync then fetches no snapshot and the traffic is not polled.
        public bool Streaming => stream != null && streamDelivered && !stream.Closed && stream.SessionId == SessionId;

        public event Action<FullSnapshot, bool> SnapshotAccepted;
        // The joined session's real-time factor, from the snapshots received while it runs.
        public BackendPace Pace { get; } = new BackendPace();
        // The backend's newest traffic frames for the joined session, oldest first: presentation only.
        public event Action<IReadOnlyList<TrafficFrame>> TrafficFramesAccepted;
        public event Action<IReadOnlyList<Event>> EventsAccepted;
        // Durable events a recovery appended before the new epoch's snapshot.
        public event Action<IReadOnlyList<Event>> RecoveryEventsReceived;
        public event Action<bool> AvailabilityChanged;
        // False when a joined session's poll first fails, true when one succeeds again. Unlike
        // AvailabilityChanged it ignores joining, leaving and resyncing, which are not the
        // backend dropping.
        public event Action<bool> ConnectionChanged;
        public event Action<CommandOutcome> CommandFinished;
        // Includes submissions with an unknown outcome and complete HTTP refusals, so the
        // report does not silently omit commands that never produced a journaled receipt.
        public event Action<CommandOutcome> CommandLedgerChanged;
        public event Action<string> ErrorOccurred;
        public event Action SessionLeft;

        // -----------------------------------------------------------------------------------
        // Function: SessionController (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the controller.
        //
        // Args:
        //     api (TrainingApi): The training API.
        //     auth (AuthSession): The login.
        //     store (SessionStore): Where state is kept; a new store when null.
        //     delay (Func<TimeSpan, CancellationToken, Task>): Waits between retries; Task.Delay
        //         when null. Tests pass an instant delay.
        //     now (Func<DateTime>): The time, for how long a session without traffic is left alone;
        //         DateTime.UtcNow when null. Tests pass their own clock.
        //
        // Throws:
        //     ArgumentNullException: If api or auth is null.
        public SessionController(TrainingApi api, AuthSession auth, SessionStore store = null,
            Func<TimeSpan, CancellationToken, Task> delay = null, Func<DateTime> now = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.auth = auth ?? throw new ArgumentNullException(nameof(auth));
            Store = store ?? new SessionStore();
            this.delay = delay ?? Task.Delay;
            this.now = now ?? (() => DateTime.UtcNow);
        }

        // -----------------------------------------------------------------------------------
        // Function: Shutdown
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function stops retries and ignores replies still in flight, for example when the
        //     scene closes.
        public void Shutdown()
        {
            generation++;
            CloseStream();
            lifetime.Cancel();
        }

        // -----------------------------------------------------------------------------------
        // Function: CreateAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates a training session and joins it. The creation key is kept until
        //     the backend confirms the attempt, so a retry after a lost reply returns the same
        //     session; only the account that started it can confirm it. A late success for another
        //     login does not select its session or drop its retry key.
        public async Task CreateAsync()
        {
            if (createRequest != null) return;
            if (!auth.IsLoggedIn)
            {
                Error("Log in before creating a training session.");
                return;
            }
            var owner = auth.Username;
            if (creationKey != null && creationOwner != owner)
            {
                Error($"Log in as {creationOwner} to confirm the pending training session creation.");
                return;
            }
            if (creationKey == null)
            {
                creationKey = Guid.NewGuid().ToString();
                creationOwner = owner;
                creationScenario = Scenario;
            }
            var token = createRequest = new object();
            var started = generation;
            TrainingSession created;
            try
            {
                created = await api.CreateSessionAsync(new CreateSessionRequest { ScenarioRevisionId = creationScenario }, creationKey, lifetime.Token);
            }
            catch (NotLoggedInException)
            {
                // The attempt may already exist: keep its key for the owner's next login. A new
                // login by the same owner may have arrived while this request was in flight.
                if (createRequest == token) createRequest = null;
                if (started == generation && auth.IsLoggedIn && auth.Username == owner) _ = CreateAsync();
                return;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                if (createRequest == token) createRequest = null;
                if (started == generation && auth.Username == owner) Error($"Create failed: {error.Message}. Retry to confirm the same attempt.");
                return;
            }
            catch (OperationCanceledException) { return; }
            if (createRequest == token) createRequest = null;
            // A late success for another login must not select its session or drop its retry key.
            if (started != generation || !auth.IsLoggedIn || auth.Username != owner) return;
            creationKey = creationOwner = creationScenario = null;
            await JoinAsync(created.SessionId);
        }

        // -----------------------------------------------------------------------------------
        // Function: JoinAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function joins a session and loads its snapshot. Requests for the previous join
        //     are abandoned and their replies ignored. A command still open for another session can
        //     no longer be confirmed, so it finishes as NEEDS_RECONFIRMATION. Joining the same ID
        //     again keeps the store and cursor, replaying from where it left off.
        //
        // Args:
        //     sessionId (string): The session ID, checked against the backend's ID rules.
        public Task JoinAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || sessionId.Length > 128 || !IdPattern.IsMatch(sessionId))
            {
                Error("Enter a valid backend session ID.");
                return Task.CompletedTask;
            }
            generation++;
            if (pending != null && pending.Command.SessionId != sessionId)
                FinishPending(Unconfirmed("SESSION_RECOVERING"));
            joiningSessionId = sessionId;
            // A drop belongs to the session it happened in; rejoining that one can still report its return.
            if (sessionId != SessionId) connectionLost = false;
            SetAvailable(false);
            // Requests for the previous join are abandoned; their replies are ignored by generation.
            snapshotRequest = eventsRequest = commandRequest = trafficRequest = null;
            // A same-ID join keeps the store and cursor, replaying from where it left off.
            Store.Select(sessionId);
            return RequestSnapshotAsync(sessionId);
        }

        // -----------------------------------------------------------------------------------
        // Function: Leave
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function forgets the selected session and stops polling it, once it has been
        //     permanently deleted.
        public void Leave()
        {
            generation++;
            if (pending != null) FinishPending(Unconfirmed("INVALID_REFERENCE"));
            joiningSessionId = null;
            snapshotRequest = eventsRequest = commandRequest = trafficRequest = null;
            CloseStream();
            trafficKey = null;
            SessionId = null;
            Session = null;
            epoch = eventCursor = -1;
            resyncPending = false;
            connectionLost = false;
            Pace.Reset();
            Store.Select(null);
            SetAvailable(false);
            SessionLeft?.Invoke();
        }

        // -----------------------------------------------------------------------------------
        // Function: RefreshAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function is one poll: it confirms a pending creation, refreshes the snapshot
        //     (which catches up events) and reconciles a pending command. While the session's
        //     stream delivers its snapshots, none is fetched; only events the stream's snapshots
        //     announced and that are still missing are.
        public async Task RefreshAsync()
        {
            if (!auth.IsLoggedIn) return;
            if (creationKey != null && creationOwner == auth.Username) await CreateAsync();
            if (SessionId != null && !Streaming) await RequestSnapshotAsync(SessionId);
            else if (SessionId != null && eventCursor >= 0 && Store.Snapshot != null && Store.Snapshot.EventSequence > eventCursor) await RequestEventsAsync();
            if (pending != null && pending.Command.SessionId == SessionId) await RunPendingAsync(0);
        }

        // -----------------------------------------------------------------------------------
        // Function: PumpStream
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function keeps the joined session's stream: it opens one once a session is
        //     joined (again after one ends, backing off while none delivers), closes it on leaving
        //     or switching, tells it whether to send traffic, and takes what it delivered.
        //     Snapshots go through the same checks as polled ones and are reported by
        //     SnapshotAccepted; traffic frames by TrafficFramesAccepted. A stream that ends or goes
        //     silent is dropped, and the 1 s poll, which never stopped confirming commands, takes
        //     over the snapshot at once. Call it from the main thread every frame. Nothing happens
        //     unless UseStream is on.
        //
        // Args:
        //     wantTraffic (bool): Whether the traffic frames are wanted now.
        public void PumpStream(bool wantTraffic)
        {
            if (!UseStream) return;
            var joined = auth.IsLoggedIn && SessionId != null && joiningSessionId == null && Store.Snapshot != null;
            if (stream != null && (!joined || stream.SessionId != SessionId || streamGeneration != generation))
            {
                // Left, switched or rejoined: the next session's stream may open at once
                CloseStream();
                streamRetryAt = default;
                streamBackoff = StreamRetryInterval;
            }
            if (stream == null)
            {
                if (!joined || now() < streamRetryAt) return;
                OpenStream(wantTraffic);
                if (stream == null) return;
            }
            while (stream != null && stream.TryTake(out var message)) Take(message);
            if (stream == null) return;
            if (now() - streamHeard > StreamSilenceLimit)
            {
                EndStream();
                return;
            }
            if (wantTraffic != streamTraffic)
            {
                stream.SetTraffic(wantTraffic, trafficKey == TrafficKey() ? trafficCursor : -1);
                streamTraffic = wantTraffic;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: OpenStream
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function opens a stream of the joined session, its traffic from the newest
        //     frame taken.
        //
        // Args:
        //     wantTraffic (bool): Whether the traffic frames are wanted now.
        private void OpenStream(bool wantTraffic)
        {
            var cursor = trafficKey == TrafficKey() ? trafficCursor : -1;
            stream = api.OpenSessionStream(SessionId, wantTraffic, cursor, (int)StreamSnapshotInterval.TotalMilliseconds);
            if (stream == null) return;
            streamGeneration = generation;
            streamTraffic = wantTraffic;
            streamDelivered = false;
            streamHeard = now();
        }

        // -----------------------------------------------------------------------------------
        // Function: Take
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function acts on one message of the stream. An error says why the stream is
        //     about to end; its Closed message ends it.
        //
        // Args:
        //     message (StreamMessage): The message.
        private void Take(StreamMessage message)
        {
            streamHeard = now();
            switch (message.Kind)
            {
                case StreamMessageKind.Snapshot:
                    streamDelivered = true;
                    streamBackoff = StreamRetryInterval;
                    _ = AcceptStreamedSnapshotAsync(message.Snapshot);
                    break;
                case StreamMessageKind.Traffic:
                    AcceptStreamedTraffic(message.Frames);
                    break;
                case StreamMessageKind.Closed:
                    EndStream();
                    break;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: EndStream
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function drops a stream that ended or went silent. The next is tried after the
        //     backoff, which doubles while streams end without delivering. One that was
        //     delivering is followed by a poll at once, which reports what the stream could not
        //     (the session recovering or deleted, the login gone) as polling always has.
        private void EndStream()
        {
            var delivered = streamDelivered;
            CloseStream();
            if (!delivered) streamBackoff = TimeSpan.FromTicks(Math.Min(streamBackoff.Ticks * 2, MaxStreamRetryInterval.Ticks));
            streamRetryAt = now() + streamBackoff;
            if (delivered) _ = RefreshAsync();
        }

        // -----------------------------------------------------------------------------------
        // Function: CloseStream
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function closes the stream, if any, dropping what it has not delivered.
        private void CloseStream()
        {
            stream?.Dispose();
            stream = null;
            streamDelivered = false;
        }

        // -----------------------------------------------------------------------------------
        // Function: AcceptStreamedSnapshotAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes a snapshot the stream delivered, as a polled one. One no newer
        //     than the snapshot held (the stream's first, or one a poll already brought) is passed
        //     over without complaint; events are fetched only when it has new ones.
        //
        // Args:
        //     snapshot (FullSnapshot): The snapshot.
        private Task AcceptStreamedSnapshotAsync(FullSnapshot snapshot)
        {
            if (SessionId == null || snapshot.SessionId != SessionId || joiningSessionId != null || NotNewer(snapshot)) return Task.CompletedTask;
            snapshotsPushed++;
            return AcceptSnapshotAsync(SessionId, snapshot, streamed: true);
        }

        // -----------------------------------------------------------------------------------
        // Function: AcceptStreamedTraffic
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reports the traffic frames the stream delivered that are newer than
        //     the newest taken. A new session or epoch starts over.
        //
        // Args:
        //     frames (List<TrafficFrame>): The frames, oldest first.
        private void AcceptStreamedTraffic(List<TrafficFrame> frames)
        {
            if (SessionId == null || frames == null) return;
            var key = TrafficKey();
            if (key != trafficKey) { trafficKey = key; trafficCursor = -1; }
            var fresh = frames.Where(f => f.Tick > trafficCursor).ToList();
            if (fresh.Count == 0) return;
            trafficCursor = fresh[fresh.Count - 1].Tick;
            TrafficFramesAccepted?.Invoke(fresh);
        }

        // -----------------------------------------------------------------------------------
        // Function: NotNewer
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a snapshot of the joined session adds nothing to the one
        //     held: an older epoch, or the same epoch at no later point. Such a snapshot is not an
        //     error when it crossed a newer one on its way.
        //
        // Args:
        //     snapshot (FullSnapshot): The snapshot.
        //
        // Returns:
        //     bool: True when it is no newer.
        private bool NotNewer(FullSnapshot snapshot)
        {
            var current = Store.Snapshot;
            if (current == null || current.SessionId != snapshot.SessionId) return false;
            if (snapshot.SessionEpoch != current.SessionEpoch) return snapshot.SessionEpoch < current.SessionEpoch;
            return snapshot.SnapshotId == current.SnapshotId || snapshot.EventSequence < current.EventSequence || snapshot.Tick < current.Tick
                   || (snapshot.EventSequence == current.EventSequence && snapshot.Tick == current.Tick);
        }

        private string TrafficKey() => $"{SessionId}/{epoch}";

        // -----------------------------------------------------------------------------------
        // Function: RefreshTrafficAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function is one request for the joined session's traffic: the backend's frames
        //     since the newest taken, which TrafficFramesAccepted reports. Given a wait, the backend
        //     holds the request that long for a frame newer than those taken. A new session or epoch
        //     starts over. A session the backend has no traffic for is not asked again for
        //     NoTrafficRetryInterval, since that may be passing (as while the backend recovers); any
        //     other failure is left to the snapshot poll to report, and the next request tries again.
        //
        // Args:
        //     wait (TimeSpan): How long the backend may hold the request for the next frame; none by default.
        //
        // Returns:
        //     Task<int>: How many frames the backend sent, which may be none, when it answered: asking
        //         again at once is then right. -1 when nothing was asked, or the request failed.
        public async Task<int> RefreshTrafficAsync(TimeSpan wait = default)
        {
            var sessionId = SessionId;
            // The stream delivers the traffic it was asked for; a poll as well would only repeat it
            if (!auth.IsLoggedIn || sessionId == null || !Available || trafficRequest != null || (Streaming && streamTraffic)) return -1;
            var key = $"{sessionId}/{epoch}";
            if (key == noTrafficKey && now() < noTrafficUntil) return -1;
            if (key != trafficKey) { trafficKey = key; trafficCursor = -1; }
            var token = trafficRequest = new object();
            var started = generation;
            List<TrafficFrame> frames;
            try
            {
                frames = await api.GetTrafficFramesAsync(sessionId, trafficCursor, (int)Math.Max(0, wait.TotalMilliseconds), lifetime.Token);
            }
            catch (BackendErrorException error) when (error.Error.Code == "INVALID_REFERENCE")
            {
                if (trafficRequest == token) trafficRequest = null;
                if (started == generation)
                {
                    noTrafficKey = key;
                    noTrafficUntil = now() + NoTrafficRetryInterval;
                }
                return -1;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                if (trafficRequest == token) trafficRequest = null;
                return -1;
            }
            catch (OperationCanceledException) { return -1; }
            if (trafficRequest == token) trafficRequest = null;
            if (started != generation || key != trafficKey) return -1;
            if (frames.Count == 0) return 0;
            trafficCursor = frames[frames.Count - 1].Tick;
            TrafficFramesAccepted?.Invoke(frames);
            return frames.Count;
        }

        // -----------------------------------------------------------------------------------
        // Function: SendControlAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sends START, PAUSE, RESUME or ARCHIVE for the joined session.
        //
        // Args:
        //     commandType (string): The control.
        //
        // Returns:
        //     Task<bool>: True when the command was accepted for sending.
        //
        // Throws:
        //     ArgumentException: If commandType is not a session control.
        public Task<bool> SendControlAsync(string commandType)
        {
            if (!SessionControls.Contains(commandType)) throw new ArgumentException("Not a session control", nameof(commandType));
            if (Session == null) return Refuse();
            return SubmitAsync(ControlCommand(Session.SessionId, Session.SessionEpoch, Session.AggregateRevision, commandType));
        }

        // -----------------------------------------------------------------------------------
        // Function: ControlCommand
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds a session control command (START, PAUSE, RESUME or ARCHIVE)
        //     for a session at a given epoch and revision. It is the one place such commands are
        //     made, for the joined session and for the sessions Archive all ends.
        //
        // Args:
        //     sessionId (string): The session.
        //     epoch (long): The session's current epoch.
        //     revision (long): The session's current aggregate revision.
        //     commandType (string): The control.
        //
        // Returns:
        //     DispatcherCommand: The command, ready to submit.
        public static DispatcherCommand ControlCommand(string sessionId, long epoch, long revision, string commandType) =>
            new DispatcherCommand
            {
                ContractVersion = "2.0", CommandId = Guid.NewGuid().ToString(), IdempotencyKey = Guid.NewGuid().ToString(),
                SessionId = sessionId, CommandType = commandType, ExpectedEpoch = epoch,
                ExpectedRevision = revision, TargetType = "SESSION", TargetId = sessionId,
                Payload = new Newtonsoft.Json.Linq.JObject(), Reason = null,
            };

        // -----------------------------------------------------------------------------------
        // Function: ArchiveAllAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function archives every live session on the backend, whoever started it, to
        //     free their slots. The joined session goes through SendControlAsync, as its Archive
        //     button does, so the one open command and the refresh after it still hold; any other
        //     is archived at the epoch and revision its snapshot gives. A session that fails, for
        //     any reason but a login that has run out, is left live and the rest carry on. What
        //     was archived is then read back from the session list rather than from the replies,
        //     since a queued ARCHIVE is only applied a tick or two later.
        //
        // Returns:
        //     Task<(int Live, int Archived)>: How many sessions were live, and how many of those
        //         are no longer live.
        //
        // Throws:
        //     NotLoggedInException: If the login runs out.
        public async Task<(int Live, int Archived)> ArchiveAllAsync()
        {
            var listed = await api.ListSessionsAsync(ArchiveListPage, lifetime.Token);
            var live = listed.Sessions.Where(s => s.OccupiesCapacity).Select(s => s.SessionId).ToList();
            foreach (var id in live)
            {
                try
                {
                    if (id == SessionId && Session != null)
                    {
                        await SendControlAsync("ARCHIVE");
                        continue;
                    }
                    var snapshot = await api.GetSnapshotAsync(id, lifetime.Token);
                    await api.SubmitCommandAsync(ControlCommand(id, snapshot.SessionEpoch, snapshot.Session.AggregateRevision, "ARCHIVE"), lifetime.Token);
                }
                catch (NotLoggedInException) { throw; }
                // A timeout, a refusal or a bad reply: this one stays live, counted below
                catch (Exception) { }
            }
            var stillLive = new HashSet<string>(live);
            for (var check = 0; check < ArchiveChecks && stillLive.Count > 0; check++)
            {
                if (check > 0) await delay(ArchiveCheckInterval, lifetime.Token);
                try
                {
                    var now = await api.ListSessionsAsync(ArchiveListPage, lifetime.Token);
                    stillLive.IntersectWith(now.Sessions.Where(s => s.OccupiesCapacity).Select(s => s.SessionId));
                }
                catch (NotLoggedInException) { throw; }
                catch (Exception) { break; }
            }
            return (live.Count, live.Count - stillLive.Count);
        }

        // -----------------------------------------------------------------------------------
        // Function: SubmitAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function submits one command. Only one may be open at a time, as in the Qt
        //     client, and a malformed command is rejected before it becomes pending.
        //
        // Args:
        //     command (DispatcherCommand): The command for the joined session.
        //
        // Returns:
        //     Task<bool>: False when refused because the session is not ready or a command is open.
        //
        // Throws:
        //     ArgumentException: If the command is for another session.
        //     ContractException: If the command breaks the contract.
        public async Task<bool> SubmitAsync(DispatcherCommand command)
        {
            if (Session == null || !Available || resyncPending || joiningSessionId != null || pending != null) return await Refuse();
            if (command.SessionId != SessionId) throw new ArgumentException("Command is for another session", nameof(command));
            ContractParser.Serialize(command); // reject a malformed command before it becomes pending
            pending = new PendingCommand { Command = command };
            RecordPending("PENDING_CONFIRMATION");
            await RunPendingAsync(0);
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Function: ApproveRouteAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function approves a validated route proposal. Route approvals use a separate
        //     endpoint, with the same identity, owner and recovery machinery as all other commands,
        //     so a lost reply never creates a second approval. Every shuttle on the route is a
        //     target.
        //
        // Args:
        //     preview (RouteEditPreview): A fresh, approvable preview of the current route
        //         revision.
        //     reason (string): Why the route is changed.
        //
        // Returns:
        //     Task<bool>: False when refused because the session is not ready or a command is open.
        //
        // Throws:
        //     ArgumentException: If the preview is stale or not approvable, or the reason is empty.
        public async Task<bool> ApproveRouteAsync(RouteEditPreview preview, string reason)
        {
            if (Session == null || !Available || resyncPending || joiningSessionId != null || pending != null) return await Refuse();
            var snapshot = Store.Snapshot;
            // A pre-trip approval detours no shuttle round an incident, so it names none.
            if (!DecisionRules.RoutePreviewMatches(snapshot, null, preview) || !preview.Approvable || string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Validate a fresh route proposal and enter a reason before approving.");
            var approval = new RouteApprovalRequest
            {
                CommandId = Guid.NewGuid().ToString(), IdempotencyKey = Guid.NewGuid().ToString(),
                PreviewId = preview.PreviewId, PreviewRevision = preview.PreviewRevision, Reason = reason.Trim(),
                TargetVehicleIds = snapshot.Vehicles.Where(v => v.RouteId == preview.RouteId).Select(v => v.VehicleId).OrderBy(v => v).ToList(),
            };
            ContractParser.Serialize(approval);
            pending = new PendingCommand
            {
                Approval = approval,
                Command = new DispatcherCommand
                {
                    CommandId = approval.CommandId, IdempotencyKey = approval.IdempotencyKey, SessionId = SessionId,
                    ExpectedEpoch = Session.SessionEpoch, ExpectedRevision = preview.RouteRevision,
                    TargetId = preview.RouteId, TargetType = "ROUTE",
                },
            };
            RecordPending("PENDING_CONFIRMATION");
            await RunPendingAsync(0);
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Function: Refuse
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reports why a command cannot be sent now.
        //
        // Returns:
        //     Task<bool>: False.
        private Task<bool> Refuse()
        {
            Error("Refresh the selected session and finish any pending command first.");
            return Task.FromResult(false);
        }

        // -----------------------------------------------------------------------------------
        // Function: RequestSnapshotAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function fetches a snapshot and takes it (AcceptSnapshotAsync). An expired
        //     login keeps the session for the next login; a session another client deleted is left.
        //     A reply no newer than a snapshot the stream delivered meanwhile is dropped.
        //
        // Args:
        //     sessionId (string): The session.
        private async Task RequestSnapshotAsync(string sessionId)
        {
            if (snapshotRequest != null) return;
            var token = snapshotRequest = new object();
            var started = generation;
            var pushedBefore = snapshotsPushed;
            FullSnapshot snapshot;
            try
            {
                snapshot = await api.GetSnapshotAsync(sessionId, lifetime.Token);
            }
            catch (Exception error)
            {
                if (snapshotRequest == token) snapshotRequest = null;
                if (started != generation || error is OperationCanceledException) return;
                if (joiningSessionId == sessionId) joiningSessionId = null;
                // An expired login keeps the session; the poll resumes after the next login.
                if (error is NotLoggedInException) return;
                // Another client deleted the joined session; polling it would only fail forever.
                if (SessionId == sessionId && error is BackendErrorException gone && gone.Error.Status == 404 && gone.Error.Code == "INVALID_REFERENCE")
                {
                    Leave();
                    Error($"Training session {sessionId} no longer exists; it was permanently deleted.");
                    return;
                }
                // An error reply means the backend is still answering; only no reply is a drop.
                if (error is NoReplyException) LoseConnection();
                SetAvailable(false);
                Error(error is ContractException ? $"Invalid v2 snapshot: {error.Message}" : $"Join/refresh failed: {error.Message}");
                return;
            }
            if (snapshotRequest == token) snapshotRequest = null;
            if (started != generation) return;
            if (joiningSessionId == sessionId) joiningSessionId = null;
            // The stream delivered a newer one while this reply was on its way: not a stale backend
            if (snapshotsPushed != pushedBefore && NotNewer(snapshot)) return;
            await AcceptSnapshotAsync(sessionId, snapshot, streamed: false);
        }

        // -----------------------------------------------------------------------------------
        // Function: AcceptSnapshotAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes a snapshot of a session, polled or streamed, into the store,
        //     then catches up events. On a new epoch of the same run it fetches the events
        //     recovery appended, which the cursor jump would otherwise skip, and an open command
        //     is only confirmed, never resent.
        //
        // Args:
        //     sessionId (string): The session.
        //     snapshot (FullSnapshot): The snapshot.
        //     streamed (bool): True when the stream delivered it: events are then fetched only
        //         when it has new ones, as most of its snapshots, a few a second, have none.
        private async Task AcceptSnapshotAsync(string sessionId, FullSnapshot snapshot, bool streamed)
        {
            var resetEvents = resyncPending;
            var refusal = Store.AcceptSnapshot(snapshot, resetEvents);
            if (refusal != null)
            {
                SetAvailable(false);
                Error($"Invalid v2 snapshot: {refusal}");
                return;
            }
            var newEpoch = snapshot.SessionEpoch;
            var sameSession = SessionId == sessionId && epoch >= 0;
            var epochChanged = sameSession && epoch != newEpoch;
            // A new epoch of the same run: recovery appended durable events before this snapshot's
            // sequence, which the cursor jump below would otherwise skip.
            var recovered = epochChanged && eventCursor >= 0 && eventCursor < snapshot.EventSequence;
            var recoveredAfter = eventCursor;
            if (SessionId != sessionId || epoch != newEpoch || resetEvents)
            {
                eventCursor = snapshot.EventSequence;
                epoch = newEpoch;
            }
            SessionId = sessionId;
            resyncPending = false;
            Session = snapshot.Session;
            if (epochChanged && pending != null && pending.Command.SessionId == sessionId) pending.StatusOnly = true;
            SetAvailable(true);
            if (connectionLost) { connectionLost = false; ConnectionChanged?.Invoke(true); }
            Pace.Observe(snapshot, now().Ticks / (double)TimeSpan.TicksPerSecond);
            SnapshotAccepted?.Invoke(snapshot, resetEvents);
            if (recovered) _ = RequestRecoveryEventsAsync(sessionId, recoveredAfter, snapshot.EventSequence);
            if (pending != null && pending.Command.SessionId == sessionId && pending.StatusOnly) _ = RunPendingAsync(0);
            // After a resync, the next poll resumes replay; a persistently bad reply must not loop.
            if (!resetEvents && (!streamed || snapshot.EventSequence > eventCursor)) await RequestEventsAsync();
        }

        // -----------------------------------------------------------------------------------
        // Function: RequestEventsAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function fetches events after the cursor and adds them to the store. The cursor
        //     moves only after the store accepts the batch; an invalid or mismatched batch triggers
        //     a resync.
        private async Task RequestEventsAsync()
        {
            if (eventsRequest != null || resyncPending || SessionId == null || eventCursor < 0) return;
            var token = eventsRequest = new object();
            var started = generation;
            var sessionId = SessionId;
            var cursor = eventCursor;
            EventReplay replay;
            try
            {
                replay = await api.GetEventsAsync(sessionId, cursor, 100, lifetime.Token);
            }
            catch (Exception error)
            {
                if (eventsRequest == token) eventsRequest = null;
                if (started != generation || sessionId != SessionId || error is OperationCanceledException || error is NotLoggedInException) return;
                if (error is ContractException) await ResyncAsync($"Invalid v2 events: {error.Message}");
                else
                {
                    if (error is NoReplyException) LoseConnection();
                    SetAvailable(false);
                    Error($"Event replay failed: {error.Message}");
                }
                return;
            }
            if (eventsRequest == token) eventsRequest = null;
            if (started != generation || sessionId != SessionId) return;
            if (replay.SessionEpoch != epoch)
            {
                await ResyncAsync("Invalid v2 events: event epoch mismatch");
                return;
            }
            var refusal = Store.AcceptEvents(sessionId, epoch, replay.Events);
            if (refusal != null)
            {
                await ResyncAsync($"Event batch rejected: {refusal}");
                return;
            }
            // Commit the transport cursor only after the store commits the batch.
            eventCursor = replay.NextSequence;
            EventsAccepted?.Invoke(replay.Events);
        }

        // -----------------------------------------------------------------------------------
        // Function: RequestRecoveryEventsAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function fetches the durable events a recovery appended before the new epoch's
        //     snapshot. They are the newest before through, so one bounded page reaches them.
        //
        // Args:
        //     sessionId (string): The session.
        //     after (long): The cursor before the recovery.
        //     through (long): The new snapshot's event sequence.
        private async Task RequestRecoveryEventsAsync(string sessionId, long after, long through)
        {
            // Recovery events are the newest before `through`; one bounded page reaches them.
            var from = Math.Max(after, through - 500);
            var started = generation;
            EventReplay replay;
            try { replay = await api.GetEventsAsync(sessionId, from, (int)Math.Max(1, through - from), lifetime.Token); }
            catch (ContractException error)
            {
                if (started == generation) Error($"Invalid recovery events: {error.Message}");
                return;
            }
            catch (Exception) { return; }
            if (started != generation || sessionId != SessionId) return;
            RecoveryEventsReceived?.Invoke(replay.Events.Where(e => e.EventSequence <= through).ToList());
        }

        // -----------------------------------------------------------------------------------
        // Function: ResyncAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function marks the session unavailable and reloads it from a fresh snapshot.
        //
        // Args:
        //     reason (string): Why, for the error message.
        private Task ResyncAsync(string reason)
        {
            resyncPending = true;
            SetAvailable(false);
            Error($"{reason}; resynchronizing from snapshot");
            return RequestSnapshotAsync(SessionId);
        }

        // -----------------------------------------------------------------------------------
        // Function: RunPendingAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sends the pending command, or checks its status once its outcome is
        //     open. It never gives up and never forgets the key, since a fresh command could apply
        //     the decision twice. Without a login it waits: an anonymous request's 403 is final and
        //     would drop a command the backend may have accepted. A complete refusal of a
        //     submission is final; a status check the backend can no longer answer ends as
        //     NEEDS_RECONFIRMATION.
        //
        // Args:
        //     attempt (int): How many times this command has been retried, for the backoff.
        private async Task RunPendingAsync(int attempt)
        {
            var command = pending;
            if (command == null || commandRequest != null) return;
            // Without a login the request would be anonymous and its 403 final, dropping a command
            // the backend may have accepted. RefreshAsync resends it after the next login.
            if (!auth.IsLoggedIn || !ClaimForCurrentLogin(command)) return;
            if (!command.StatusOnly && (!Available || resyncPending))
            {
                command.StatusOnly = true;
                return;
            }
            if (command.StatusOnly && (!Available || resyncPending)) return;
            var token = commandRequest = new object();
            var started = generation;
            var statusOnly = command.StatusOnly;
            CommandStatus reply = null;
            Exception failure = null;
            try
            {
                reply = statusOnly
                    ? await api.GetCommandStatusAsync(command.Command.SessionId, command.Command.IdempotencyKey, lifetime.Token)
                    : command.Approval != null
                        ? await api.ApproveRouteAsync(command.Command.SessionId, command.Approval, lifetime.Token)
                        : await api.SubmitCommandAsync(command.Command, lifetime.Token);
            }
            catch (Exception error) { failure = error; }
            if (commandRequest == token) commandRequest = null;
            if (pending != command || failure is OperationCanceledException) return;
            if (started != generation || command.Command.SessionId != SessionId)
            {
                // A result for a session the trainee has left must not show in the new one.
                FinishPending(null);
                return;
            }
            if (reply != null)
            {
                if (reply.CommandId != command.Command.CommandId || reply.IdempotencyKey != command.Command.IdempotencyKey
                    || reply.TargetType != command.Command.TargetType || reply.TargetId != command.Command.TargetId)
                {
                    RetryLater(command, attempt, statusOnly);
                    return;
                }
                var outcome = CommandOutcome.From(reply);
                outcome.CommandType = command.Command.CommandType;
                CommandLedgerChanged?.Invoke(outcome);
                CommandFinished?.Invoke(outcome);
                if (reply.Status == "QUEUED") RetryLater(command, attempt, statusOnly);
                else FinishPending(null);
                return;
            }
            switch (failure)
            {
                case NotLoggedInException _:
                    return; // keep it; RefreshAsync resends after the owner logs in again
                case NoReplyException _:
                case ContractException _:
                    if (attempt == 0) Error(statusOnly
                        ? "Connection recovered; checking the outstanding command before allowing another decision."
                        : "No complete reply to the training command; confirming it");
                    RetryLater(command, attempt, statusOnly);
                    return;
                case BackendErrorException backend:
                    var code = backend.Error.Code;
                    if (backend.Error.Status == 503 || code == "SESSION_RECOVERING")
                    {
                        RetryLater(command, attempt, statusOnly);
                        return;
                    }
                    if (statusOnly)
                    {
                        // The backend no longer knows the command, so its outcome cannot be confirmed.
                        FinishPending(Unconfirmed(backend.Error.Status == 404 && code == "INVALID_REFERENCE" ? "SESSION_RECOVERING" : code));
                        return;
                    }
                    // A complete error to a submission means the command was not journaled; it is final.
                    RecordPending("NOT_ACCEPTED", code);
                    FinishPending(null);
                    Error($"Training command failed: {backend.Message}");
                    return;
                default:
                    RecordPending("UNCONFIRMED", failure?.Message);
                    FinishPending(null);
                    Error($"Training command failed: {failure?.Message}");
                    return;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: RetryLater
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function schedules the next try, backing off by 100 ms (250 ms for status
        //     checks) per attempt, up to 5 s.
        //
        // Args:
        //     command (PendingCommand): The command.
        //     attempt (int): The attempt that just failed.
        //     statusOnly (bool): True for a status check.
        private void RetryLater(PendingCommand command, int attempt, bool statusOnly)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Min((statusOnly ? 250 : 100) * (attempt + 1), 5000));
            _ = RetryAfterAsync(command, attempt + 1, wait);
        }

        // -----------------------------------------------------------------------------------
        // Function: RetryAfterAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function waits, then retries the command if it is still the pending one.
        //
        // Args:
        //     command (PendingCommand): The command.
        //     attempt (int): The attempt number.
        //     wait (TimeSpan): How long to wait.
        private async Task RetryAfterAsync(PendingCommand command, int attempt, TimeSpan wait)
        {
            try { await delay(wait, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (pending == command) await RunPendingAsync(attempt);
        }

        // -----------------------------------------------------------------------------------
        // Function: ClaimForCurrentLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives a command made while logged out to whoever logs in next. After
        //     that it stays with that account: resent under another, the backend answers
        //     IDEMPOTENCY_CONFLICT, which reads as final and would drop the original decision.
        //
        // Args:
        //     command (PendingCommand): The command.
        //
        // Returns:
        //     bool: True when the current login owns the command.
        private bool ClaimForCurrentLogin(PendingCommand command)
        {
            if (command.Owner == null) command.Owner = auth.Username;
            return command.Owner == auth.Username;
        }

        // -----------------------------------------------------------------------------------
        // Function: Unconfirmed
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a NEEDS_RECONFIRMATION outcome for the pending command.
        //
        // Args:
        //     failureCode (string): Why; INVALID_REFERENCE when null.
        //
        // Returns:
        //     CommandOutcome: The unconfirmed outcome.
        private CommandOutcome Unconfirmed(string failureCode) => new CommandOutcome
        {
            CommandId = pending.Command.CommandId, IdempotencyKey = pending.Command.IdempotencyKey, Status = "NEEDS_RECONFIRMATION",
            TargetType = pending.Command.TargetType, TargetId = pending.Command.TargetId,
            TargetRevision = pending.Command.ExpectedRevision, FailureCode = failureCode ?? "INVALID_REFERENCE", Confirmed = false,
        };

        // -----------------------------------------------------------------------------------
        // Function: FinishPending
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function closes the pending command and reports its outcome, if any.
        //
        // Args:
        //     outcome (CommandOutcome): The outcome to report, or null when already reported.
        private void FinishPending(CommandOutcome outcome)
        {
            pending = null;
            commandRequest = null;
            if (outcome != null) { CommandLedgerChanged?.Invoke(outcome); CommandFinished?.Invoke(outcome); }
        }

        // -----------------------------------------------------------------------------------
        // Function: RecordPending
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function adds the pending command to the command ledger with a client-side
        //     status.
        //
        // Args:
        //     status (string): For example PENDING_CONFIRMATION or NOT_ACCEPTED.
        //     failure (string): The failure code or message, if any.
        private void RecordPending(string status, string failure = null)
        {
            var outcome = Unconfirmed(failure);
            outcome.Status = status; outcome.FailureCode = failure;
            CommandLedgerChanged?.Invoke(outcome);
        }

        // -----------------------------------------------------------------------------------
        // Function: LoseConnection
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reports the backend dropping, once per drop, while a session is joined.
        //     Callers pass only NoReplyException failures: an error reply means the backend is up.
        private void LoseConnection()
        {
            if (connectionLost || SessionId == null) return;
            connectionLost = true;
            ConnectionChanged?.Invoke(false);
        }

        // -----------------------------------------------------------------------------------
        // Function: SetAvailable
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function changes whether the session can take commands. While it cannot, an open
        //     command may have been received, so it is only confirmed afterwards.
        //
        // Args:
        //     available (bool): The new availability.
        private void SetAvailable(bool available)
        {
            if (Available == available) return;
            Available = available;
            if (!available && pending != null) pending.StatusOnly = true;
            AvailabilityChanged?.Invoke(available);
        }

        // -----------------------------------------------------------------------------------
        // Function: Error
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reports an error to the screens.
        //
        // Args:
        //     message (string): The message.
        private void Error(string message) => ErrorOccurred?.Invoke(message);
    }
}
