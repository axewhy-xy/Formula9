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
    // What happened to a submitted command. Confirmed is false for outcomes the client had to
    // conclude itself (NEEDS_RECONFIRMATION) because the backend could no longer confirm it.
    public sealed class CommandOutcome
    {
        public string CommandId, IdempotencyKey, Status, TargetType, TargetId, FailureCode;
        public long TargetRevision;
        public long? AppliedTick;
        public bool Confirmed;

        internal static CommandOutcome From(CommandStatus status) => new CommandOutcome
        {
            CommandId = status.CommandId, IdempotencyKey = status.IdempotencyKey, Status = status.Status,
            TargetType = status.TargetType, TargetId = status.TargetId, TargetRevision = status.TargetRevision,
            FailureCode = status.FailureCode, AppliedTick = status.AppliedTick, Confirmed = true,
        };
    }

    // Owns the joined training session: creation, join, the 1 s poll, event catch-up and the one
    // command whose outcome is still open. Port of the training half of the Qt BackendClient.
    // Call it from Unity's main thread; drive Refresh from a 1 s timer.
    public sealed class SessionController
    {
        public const string IntegratedScenario = "scenario-m2-integrated-r1";
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9_.:-]*$");
        private static readonly HashSet<string> SessionControls = new HashSet<string> { "START", "PAUSE", "RESUME", "ARCHIVE" };

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
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        private long generation;
        private long epoch = -1;
        private long eventCursor = -1;
        private bool resyncPending;
        private string joiningSessionId;
        private object snapshotRequest, eventsRequest, commandRequest, createRequest;
        private string creationKey, creationOwner, creationScenario;
        private PendingCommand pending;

        public SessionStore Store { get; }
        public string SessionId { get; private set; }
        public TrainingSession Session { get; private set; }
        public bool Available { get; private set; }
        public bool HasPendingCommand => pending != null;
        public bool CreationPending => creationKey != null;
        // Scenario for the next creation; an unresolved creation keeps the one it started with.
        public string Scenario { get; set; } = IntegratedScenario;

        public event Action<FullSnapshot, bool> SnapshotAccepted;
        public event Action<IReadOnlyList<Event>> EventsAccepted;
        // Durable events a recovery appended before the new epoch's snapshot.
        public event Action<IReadOnlyList<Event>> RecoveryEventsReceived;
        public event Action<bool> AvailabilityChanged;
        public event Action<CommandOutcome> CommandFinished;
        // Includes submissions with an unknown outcome and complete HTTP refusals, so the
        // report does not silently omit commands that never produced a journaled receipt.
        public event Action<CommandOutcome> CommandLedgerChanged;
        public event Action<string> ErrorOccurred;
        public event Action SessionLeft;

        public SessionController(TrainingApi api, AuthSession auth, SessionStore store = null,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.auth = auth ?? throw new ArgumentNullException(nameof(auth));
            Store = store ?? new SessionStore();
            this.delay = delay ?? Task.Delay;
        }

        // Stops retries and ignores replies still in flight, for example when the scene closes.
        public void Shutdown()
        {
            generation++;
            lifetime.Cancel();
        }

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
            SetAvailable(false);
            // Requests for the previous join are abandoned; their replies are ignored by generation.
            snapshotRequest = eventsRequest = commandRequest = null;
            // A same-ID join keeps the store and cursor, replaying from where it left off.
            Store.Select(sessionId);
            return RequestSnapshotAsync(sessionId);
        }

        // Forgets the selected session and stops polling it, once it has been permanently deleted.
        public void Leave()
        {
            generation++;
            if (pending != null) FinishPending(Unconfirmed("INVALID_REFERENCE"));
            joiningSessionId = null;
            snapshotRequest = eventsRequest = commandRequest = null;
            SessionId = null;
            Session = null;
            epoch = eventCursor = -1;
            resyncPending = false;
            Store.Select(null);
            SetAvailable(false);
            SessionLeft?.Invoke();
        }

        // One poll: confirm a pending creation, refresh the snapshot and reconcile a pending command.
        public async Task RefreshAsync()
        {
            if (!auth.IsLoggedIn) return;
            if (creationKey != null && creationOwner == auth.Username) await CreateAsync();
            if (SessionId != null) await RequestSnapshotAsync(SessionId);
            if (pending != null && pending.Command.SessionId == SessionId) await RunPendingAsync(0);
        }

        // START, PAUSE, RESUME or ARCHIVE for the joined session.
        public Task<bool> SendControlAsync(string commandType)
        {
            if (!SessionControls.Contains(commandType)) throw new ArgumentException("Not a session control", nameof(commandType));
            if (Session == null) return Refuse();
            return SubmitAsync(new DispatcherCommand
            {
                ContractVersion = "2.0", CommandId = Guid.NewGuid().ToString(), IdempotencyKey = Guid.NewGuid().ToString(),
                SessionId = Session.SessionId, CommandType = commandType, ExpectedEpoch = Session.SessionEpoch,
                ExpectedRevision = Session.AggregateRevision, TargetType = "SESSION", TargetId = Session.SessionId,
                Payload = new Newtonsoft.Json.Linq.JObject(), Reason = null,
            });
        }

        // Submits one command; only one may be open at a time, as in the Qt client.
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

        // Route approvals use a separate endpoint, with the same identity, owner and recovery
        // machinery as all other commands. A lost reply must never create a second approval.
        public async Task<bool> ApproveRouteAsync(RouteEditPreview preview, string reason)
        {
            if (Session == null || !Available || resyncPending || joiningSessionId != null || pending != null) return await Refuse();
            var snapshot = Store.Snapshot;
            if (!DecisionRules.RoutePreviewMatches(snapshot, preview) || !preview.Approvable || string.IsNullOrWhiteSpace(reason))
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

        private Task<bool> Refuse()
        {
            Error("Refresh the selected session and finish any pending command first.");
            return Task.FromResult(false);
        }

        private async Task RequestSnapshotAsync(string sessionId)
        {
            if (snapshotRequest != null) return;
            var token = snapshotRequest = new object();
            var started = generation;
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
                SetAvailable(false);
                Error(error is ContractException ? $"Invalid v2 snapshot: {error.Message}" : $"Join/refresh failed: {error.Message}");
                return;
            }
            if (snapshotRequest == token) snapshotRequest = null;
            if (started != generation) return;
            if (joiningSessionId == sessionId) joiningSessionId = null;

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
            SnapshotAccepted?.Invoke(snapshot, resetEvents);
            if (recovered) _ = RequestRecoveryEventsAsync(sessionId, recoveredAfter, snapshot.EventSequence);
            if (pending != null && pending.Command.SessionId == sessionId && pending.StatusOnly) _ = RunPendingAsync(0);
            // After a resync, the next poll resumes replay; a persistently bad reply must not loop.
            if (!resetEvents) await RequestEventsAsync();
        }

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

        private Task ResyncAsync(string reason)
        {
            resyncPending = true;
            SetAvailable(false);
            Error($"{reason}; resynchronizing from snapshot");
            return RequestSnapshotAsync(SessionId);
        }

        // Sends the pending command, or checks its status once its outcome is open. Never gives up
        // and never forgets the key: a fresh command could otherwise apply the decision twice.
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
                CommandLedgerChanged?.Invoke(CommandOutcome.From(reply));
                CommandFinished?.Invoke(CommandOutcome.From(reply));
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

        private void RetryLater(PendingCommand command, int attempt, bool statusOnly)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Min((statusOnly ? 250 : 100) * (attempt + 1), 5000));
            _ = RetryAfterAsync(command, attempt + 1, wait);
        }

        private async Task RetryAfterAsync(PendingCommand command, int attempt, TimeSpan wait)
        {
            try { await delay(wait, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (pending == command) await RunPendingAsync(attempt);
        }

        // A command made while logged out belongs to whoever logs in next. After that it stays
        // with that account: resent under another, the backend answers IDEMPOTENCY_CONFLICT, which
        // reads as final and would drop the original decision.
        private bool ClaimForCurrentLogin(PendingCommand command)
        {
            if (command.Owner == null) command.Owner = auth.Username;
            return command.Owner == auth.Username;
        }

        private CommandOutcome Unconfirmed(string failureCode) => new CommandOutcome
        {
            CommandId = pending.Command.CommandId, IdempotencyKey = pending.Command.IdempotencyKey, Status = "NEEDS_RECONFIRMATION",
            TargetType = pending.Command.TargetType, TargetId = pending.Command.TargetId,
            TargetRevision = pending.Command.ExpectedRevision, FailureCode = failureCode ?? "INVALID_REFERENCE", Confirmed = false,
        };

        private void FinishPending(CommandOutcome outcome)
        {
            pending = null;
            commandRequest = null;
            if (outcome != null) { CommandLedgerChanged?.Invoke(outcome); CommandFinished?.Invoke(outcome); }
        }

        private void RecordPending(string status, string failure = null)
        {
            var outcome = Unconfirmed(failure);
            outcome.Status = status; outcome.FailureCode = failure;
            CommandLedgerChanged?.Invoke(outcome);
        }

        private void SetAvailable(bool available)
        {
            if (Available == available) return;
            Available = available;
            if (!available && pending != null) pending.StatusOnly = true;
            AvailabilityChanged?.Invoke(available);
        }

        private void Error(string message) => ErrorOccurred?.Invoke(message);
    }
}
