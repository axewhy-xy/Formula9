/*
 * The backend's login and training APIs.
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (session retention and cleanup, waiting for the bundled backend)
 * Date Created: 30/10/2026
 * Last Modified: 10/11/2026
 *
 * Description:
 *     Part of the Unity client's backend layer. AuthClient logs in against the team's shared
 *     accounts; TrainingApi calls /api/v2/sessions and returns validated contract objects. Every
 *     failure is one of three kinds: BackendErrorException (a complete error reply, for example
 *     SESSION_RECOVERING), NoReplyException (no complete reply; the request may still have taken
 *     effect, so commands must be confirmed, never re-created) or NotLoggedInException. Training
 *     requests are never sent without a login.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    // -----------------------------------------------------------------------------------
    // Class: ApiError
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is the backend's error body: {"error": {"code", "message", "retryable",
    //     "current_revision"}}, plus the HTTP status.
    public sealed class ApiError
    {
        public int Status;
        public string Code;
        public string Message;
        public bool Retryable;
        public long? CurrentRevision;
    }

    // -----------------------------------------------------------------------------------
    // Class: BackendErrorException
    // -----------------------------------------------------------------------------------
    // Description:
    //     This exception is a complete error reply. For a training command it means the command was
    //     not journaled.
    //
    //     Attributes: Error holds the parsed error.
    public sealed class BackendErrorException : Exception
    {
        public ApiError Error { get; }
        public BackendErrorException(ApiError error) : base($"{error.Code} ({error.Status}): {error.Message}") => Error = error;
    }

    // -----------------------------------------------------------------------------------
    // Class: NoReplyException
    // -----------------------------------------------------------------------------------
    // Description:
    //     This exception means there was no complete, readable reply, so the request may or may not
    //     have taken effect.
    //
    //     Attributes: Status is the HTTP status when a reply arrived but was unreadable, and 0 when
    //     nothing arrived at all.
    public sealed class NoReplyException : Exception
    {
        // The HTTP status when a reply arrived but was unreadable; 0 when nothing arrived at all.
        public int Status { get; }

        public NoReplyException(string message, int status = 0) : base(message) => Status = status;
    }

    // -----------------------------------------------------------------------------------
    // Class: NotLoggedInException
    // -----------------------------------------------------------------------------------
    // Description:
    //     This exception means there is no valid login, either locally or because the backend
    //     rejected the token (401).
    public sealed class NotLoggedInException : Exception
    {
        public NotLoggedInException(string message) : base(message) { }
    }

    // -----------------------------------------------------------------------------------
    // Class: LoginSession
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is the backend's reply to a login or sign-up: the account, its bearer token
    //     and when it expires.
    public sealed class LoginSession
    {
        [JsonProperty("username", Required = Required.Always)] public string Username { get; set; }
        [JsonProperty("token", Required = Required.Always)] public string Token { get; set; }
        [JsonProperty("expires_at", Required = Required.Always)] public string ExpiresAt { get; set; }
        [JsonProperty("expires_in", Required = Required.Always)] public long ExpiresIn { get; set; }
    }

    // -----------------------------------------------------------------------------------
    // Class: SessionPurge
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is the reply to a session purge: how many sessions were deleted.
    public sealed class SessionPurge
    {
        [JsonProperty("deleted_count", Required = Required.Always)] public long DeletedCount { get; set; }
    }

    // -----------------------------------------------------------------------------------
    // Class: SessionCompaction
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is the reply to a database compaction: the database size before and after, and
    //     whether the write-ahead log was truncated.
    public sealed class SessionCompaction
    {
        [JsonProperty("database_bytes_before", Required = Required.Always)] public long DatabaseBytesBefore { get; set; }
        [JsonProperty("database_bytes_after", Required = Required.Always)] public long DatabaseBytesAfter { get; set; }
        [JsonProperty("wal_truncated", Required = Required.Always)] public bool WalTruncated { get; set; }
    }

    // -----------------------------------------------------------------------------------
    // Class: BackendClientBase
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class holds the request plumbing the auth and training clients share.
    public abstract class BackendClientBase
    {
        protected readonly IHttpTransport Transport;
        protected readonly Uri BaseUrl;

        // -----------------------------------------------------------------------------------
        // Function: BackendClientBase (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sets the transport and backend address.
        //
        // Args:
        //     transport (IHttpTransport): How requests are sent.
        //     baseUrl (Uri): The backend address; BackendEndpoint.ConfiguredUrl() when null.
        //
        // Throws:
        //     ArgumentNullException: If transport is null.
        protected BackendClientBase(IHttpTransport transport, Uri baseUrl)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            BaseUrl = baseUrl ?? BackendEndpoint.ConfiguredUrl();
        }

        // -----------------------------------------------------------------------------------
        // Function: Request
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds a request for an API path on this backend.
        //
        // Args:
        //     method (string): The HTTP method.
        //     path (string): The API path, starting with '/'.
        //     body (string): The JSON body, or null.
        //
        // Returns:
        //     HttpRequest: The request.
        protected HttpRequest Request(string method, string path, string body = null) =>
            new HttpRequest { Method = method, Url = BackendEndpoint.ApiUrl(BaseUrl, path), Body = body };

        // -----------------------------------------------------------------------------------
        // Function: SendAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sends a request and returns the body of a 2xx reply. Anything else
        //     becomes one of the exceptions in this file, so callers can tell a refusal from an
        //     unknown outcome.
        //
        // Args:
        //     request (HttpRequest): The request.
        //     cancellation (CancellationToken): Cancels the request.
        //     onUnauthorized (Action<ApiError>): Called before throwing when the reply is 401.
        //
        // Returns:
        //     Task<string>: The reply body.
        //
        // Throws:
        //     BackendErrorException: For a complete error reply.
        //     NoReplyException: When no complete, readable reply arrived.
        //     OperationCanceledException: When cancelled.
        protected async Task<string> SendAsync(HttpRequest request, CancellationToken cancellation, Action<ApiError> onUnauthorized = null) =>
            (await SendForReplyAsync(request, cancellation, onUnauthorized)).Body;

        // -----------------------------------------------------------------------------------
        // Function: SendForReplyAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function works like SendAsync but returns the whole 2xx reply, including its raw
        //     bytes.
        //
        // Args:
        //     request (HttpRequest): The request.
        //     cancellation (CancellationToken): Cancels the request.
        //     onUnauthorized (Action<ApiError>): Called before throwing when the reply is 401.
        //
        // Returns:
        //     Task<HttpResponse>: The complete 2xx reply.
        //
        // Throws:
        //     BackendErrorException: For a complete error reply.
        //     NoReplyException: When no complete, readable reply arrived.
        //     OperationCanceledException: When cancelled.
        protected async Task<HttpResponse> SendForReplyAsync(HttpRequest request, CancellationToken cancellation,
            Action<ApiError> onUnauthorized = null)
        {
            // A build's own backend can take a few seconds to start after the player opens.
            await BundledBackend.WhenReadyAsync(cancellation);
            var response = await Transport.SendAsync(request, cancellation);
            if (response.Failure == HttpFailure.Cancelled) throw new OperationCanceledException(cancellation);
            if (!response.Complete) throw new NoReplyException($"No complete reply from {request.Method} {request.Url.AbsolutePath}");
            if (response.Status >= 200 && response.Status < 300) return response;
            var error = ReadError(response);
            if (error == null) throw new NoReplyException($"Unreadable {response.Status} reply from {request.Method} {request.Url.AbsolutePath}", response.Status);
            if (response.Status == 401 && onUnauthorized != null) onUnauthorized(error);
            throw new BackendErrorException(error);
        }

        // -----------------------------------------------------------------------------------
        // Function: ReadError
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads the backend's error body. A reply without a string error code is
        //     not one of the backend's errors.
        //
        // Args:
        //     response (HttpResponse): A complete non-2xx reply.
        //
        // Returns:
        //     ApiError: The error, or null when the body is not a backend error.
        private static ApiError ReadError(HttpResponse response)
        {
            try
            {
                if (!(JObject.Parse(response.Body ?? "")["error"] is JObject error) || !(error["code"] is JValue code) || code.Type != JTokenType.String)
                    return null;
                return new ApiError
                {
                    Status = response.Status,
                    Code = (string)code,
                    Message = error["message"]?.Type == JTokenType.String ? (string)error["message"] : (string)code,
                    Retryable = error["retryable"]?.Type == JTokenType.Boolean && (bool)error["retryable"],
                    CurrentRevision = error["current_revision"]?.Type == JTokenType.Integer ? (long?)error["current_revision"] : null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: AuthClient
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class logs in, signs up and logs out against the team's shared accounts (/api/auth).
    public sealed class AuthClient : BackendClientBase
    {
        private readonly AuthSession session;

        // -----------------------------------------------------------------------------------
        // Function: AuthClient (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the client.
        //
        // Args:
        //     transport (IHttpTransport): How requests are sent.
        //     session (AuthSession): Where the login is kept.
        //     baseUrl (Uri): The backend address; the configured one when null.
        //
        // Throws:
        //     ArgumentNullException: If session is null.
        public AuthClient(IHttpTransport transport, AuthSession session, Uri baseUrl = null) : base(transport, baseUrl) =>
            this.session = session ?? throw new ArgumentNullException(nameof(session));

        // -----------------------------------------------------------------------------------
        // Function: LogInAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function logs in and records the login in the session.
        //
        // Args:
        //     username (string): The account name.
        //     password (string): The password.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Throws:
        //     ArgumentException: If the username or password is empty or too long.
        //     BackendErrorException: If the backend refuses the login.
        //     NoReplyException: If no complete reply arrived.
        public Task LogInAsync(string username, string password, CancellationToken cancellation = default) =>
            BeginAsync("/api/auth/login", username, password, cancellation);

        // -----------------------------------------------------------------------------------
        // Function: SignUpAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates an account and logs in with it.
        //
        // Args:
        //     username (string): The new account name.
        //     password (string): The password.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Throws:
        //     ArgumentException: If the username or password is empty or too long.
        //     BackendErrorException: If the backend refuses, for example because the name is taken.
        //     NoReplyException: If no complete reply arrived.
        public Task SignUpAsync(string username, string password, CancellationToken cancellation = default) =>
            BeginAsync("/api/auth/signup", username, password, cancellation);

        // -----------------------------------------------------------------------------------
        // Function: LogOutAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function logs out. The login ends locally first, so a lost reply still logs the
        //     user out of this client.
        //
        // Args:
        //     cancellation (CancellationToken): Cancels the request.
        public async Task LogOutAsync(CancellationToken cancellation = default)
        {
            if (string.IsNullOrEmpty(session.Token)) return;
            var request = Request("POST", "/api/auth/logout");
            request.Headers["Authorization"] = "Bearer " + session.Token;
            session.End();
            // Ending locally first means a lost reply still logs the user out of this client.
            try { await SendAsync(request, cancellation); }
            catch (Exception error) when (error is NoReplyException || error is BackendErrorException) { }
        }

        // -----------------------------------------------------------------------------------
        // Function: BeginAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sends a login or sign-up and starts the session from the reply.
        //     Usernames are 1 to 64 characters after trimming; passwords 1 to 256.
        //
        // Args:
        //     path (string): The login or sign-up path.
        //     username (string): The account name.
        //     password (string): The password.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Throws:
        //     ArgumentException: If the username or password is empty or too long.
        //     ContractException: If the reply is not a valid login reply.
        private async Task BeginAsync(string path, string username, string password, CancellationToken cancellation)
        {
            username = (username ?? "").Trim();
            if (username.Length == 0 || username.Length > 64) throw new ArgumentException("Enter a username of up to 64 characters.");
            if (string.IsNullOrEmpty(password) || password.Length > 256) throw new ArgumentException("Enter a password of up to 256 characters.");
            var body = JsonConvert.SerializeObject(new Dictionary<string, string> { ["username"] = username, ["password"] = password });
            var reply = await SendAsync(Request("POST", path, body), cancellation);
            LoginSession login;
            try { login = JsonConvert.DeserializeObject<LoginSession>(reply); }
            catch (JsonException error) { throw new ContractException($"Invalid login reply: {error.Message}"); }
            session.Begin(login.Username, login.Token, login.ExpiresIn);
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: TrainingApi
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class calls /api/v2/sessions. Every call needs a login and returns validated
    //     contract objects.
    public sealed partial class TrainingApi : BackendClientBase
    {
        // Rewriting or trimming a large database outlasts the usual request bound.
        private const int MaintenanceTimeoutSeconds = 300;

        private readonly AuthSession session;

        // -----------------------------------------------------------------------------------
        // Function: TrainingApi (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the client.
        //
        // Args:
        //     transport (IHttpTransport): How requests are sent.
        //     session (AuthSession): The login every request uses.
        //     baseUrl (Uri): The backend address; the configured one when null.
        //
        // Throws:
        //     ArgumentNullException: If session is null.
        public TrainingApi(IHttpTransport transport, AuthSession session, Uri baseUrl = null) : base(transport, baseUrl) =>
            this.session = session ?? throw new ArgumentNullException(nameof(session));

        // -----------------------------------------------------------------------------------
        // Function: ListSessionsAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function lists the user's training sessions.
        //
        // Args:
        //     limit (int): The most sessions to return.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<SessionListResponse>: The validated session list.
        public async Task<SessionListResponse> ListSessionsAsync(int limit = 100, CancellationToken cancellation = default) =>
            ContractParser.Parse<SessionListResponse>(await SendTrainingAsync("GET", $"?limit={limit}", null, cancellation));

        // -----------------------------------------------------------------------------------
        // Function: DeleteSessionAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function deletes a session permanently. A missing session also succeeds, so
        //     retrying after a lost reply is safe.
        //
        // Args:
        //     sessionId (string): The training session ID.
        //     cancellation (CancellationToken): Cancels the request.
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellation = default) =>
            SendTrainingAsync("DELETE", $"/{Segment(sessionId)}", null, cancellation);

        // -----------------------------------------------------------------------------------
        // Function: PurgeSessionsAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function deletes finished sessions older than the newest keepLatest. It allows a
        //     longer timeout, because trimming a large database outlasts the usual request bound.
        //
        // Args:
        //     keepLatest (int): How many of the newest finished sessions to keep.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<long>: How many sessions were deleted.
        public async Task<long> PurgeSessionsAsync(int keepLatest, CancellationToken cancellation = default) =>
            ReadReply<SessionPurge>(await SendTrainingAsync("POST", $"/purge?keep_latest={keepLatest}", null, cancellation,
                http => http.TimeoutSeconds = MaintenanceTimeoutSeconds)).DeletedCount;

        // -----------------------------------------------------------------------------------
        // Function: CompactSessionsAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function asks the backend to compact its session database, with the longer
        //     maintenance timeout.
        //
        // Args:
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<SessionCompaction>: The database size before and after.
        public async Task<SessionCompaction> CompactSessionsAsync(CancellationToken cancellation = default) =>
            ReadReply<SessionCompaction>(await SendTrainingAsync("POST", "/compact", null, cancellation,
                http => http.TimeoutSeconds = MaintenanceTimeoutSeconds));

        // -----------------------------------------------------------------------------------
        // Function: ReadReply
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a small JSON reply that has no schema of its own.
        //
        // Args:
        //     json (string): The reply body.
        //
        // Returns:
        //     T: The reply.
        //
        // Throws:
        //     ContractException: If the reply does not match T.
        private static T ReadReply<T>(string json)
        {
            try { return JsonConvert.DeserializeObject<T>(json); }
            catch (JsonException error) { throw new ContractException($"Invalid {typeof(T).Name} reply: {error.Message}"); }
        }

        // -----------------------------------------------------------------------------------
        // Function: CreateSessionAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates a training session. Retrying with the same idempotency key
        //     returns the same session instead of creating another.
        //
        // Args:
        //     request (CreateSessionRequest): The scenario and options.
        //     idempotencyKey (string): Identifies this creation attempt across retries.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<TrainingSession>: The created session.
        public async Task<TrainingSession> CreateSessionAsync(CreateSessionRequest request, string idempotencyKey,
            CancellationToken cancellation = default)
        {
            RequireId(idempotencyKey, nameof(idempotencyKey));
            var body = ContractParser.Serialize(request);
            return ContractParser.Parse<TrainingSession>(await SendTrainingAsync("POST", "", body, cancellation,
                http => http.Headers["Idempotency-Key"] = idempotencyKey));
        }

        // -----------------------------------------------------------------------------------
        // Function: GetSnapshotAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function fetches a session's full snapshot.
        //
        // Args:
        //     sessionId (string): The training session ID.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<FullSnapshot>: The validated snapshot.
        //
        // Throws:
        //     ContractException: If the snapshot is for a different session.
        public async Task<FullSnapshot> GetSnapshotAsync(string sessionId, CancellationToken cancellation = default)
        {
            var snapshot = Expect<FullSnapshot>(await SendTrainingAsync("GET", $"/{Segment(sessionId)}/snapshot", null, cancellation));
            if (snapshot.SessionId != sessionId) throw new ContractException("Snapshot is for a different session");
            return snapshot;
        }

        // -----------------------------------------------------------------------------------
        // Function: GetEventsAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function fetches the events after a sequence number.
        //
        // Args:
        //     sessionId (string): The training session ID.
        //     after (long): The last sequence number already seen.
        //     limit (int): The most events to return.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<EventReplay>: The validated events.
        //
        // Throws:
        //     ArgumentOutOfRangeException: If after is negative or limit is below 1.
        //     ContractException: If the reply is for a different session or cursor.
        public async Task<EventReplay> GetEventsAsync(string sessionId, long after, int limit = 100, CancellationToken cancellation = default)
        {
            if (after < 0 || limit < 1) throw new ArgumentOutOfRangeException(nameof(after));
            var replay = Expect<EventReplay>(await SendTrainingAsync("GET", $"/{Segment(sessionId)}/events?after={after}&limit={limit}", null, cancellation));
            if (replay.SessionId != sessionId || replay.AfterSequence != after) throw new ContractException("Event replay does not match the request");
            return replay;
        }

        // -----------------------------------------------------------------------------------
        // Function: SubmitCommandAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function submits a dispatcher command. A 202 carries the command's journaled
        //     status; QUEUED means its outcome is still open.
        //
        // Args:
        //     command (DispatcherCommand): The command.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<CommandStatus>: The command's status.
        public async Task<CommandStatus> SubmitCommandAsync(DispatcherCommand command, CancellationToken cancellation = default)
        {
            var body = ContractParser.Serialize(command);
            return Expect<CommandStatus>(await SendTrainingAsync("POST", $"/{Segment(command.SessionId)}/commands", body, cancellation));
        }

        // -----------------------------------------------------------------------------------
        // Function: GetCommandStatusAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function asks for a command's status by its idempotency key, to confirm a
        //     command whose reply was lost.
        //
        // Args:
        //     sessionId (string): The training session ID.
        //     idempotencyKey (string): The command's idempotency key.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<CommandStatus>: The command's status.
        public async Task<CommandStatus> GetCommandStatusAsync(string sessionId, string idempotencyKey, CancellationToken cancellation = default) =>
            Expect<CommandStatus>(await SendTrainingAsync("GET", $"/{Segment(sessionId)}/commands/{Segment(idempotencyKey)}", null, cancellation));

        // -----------------------------------------------------------------------------------
        // Function: ArchiveSessionAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function archives a live session without joining it: it reads the session's
        //     current epoch and revision, then sends it an ARCHIVE command, as Archive session does.
        //
        // Args:
        //     sessionId (string): The training session ID.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<CommandStatus>: The command's status.
        public async Task<CommandStatus> ArchiveSessionAsync(string sessionId, CancellationToken cancellation = default)
        {
            var snapshot = await GetSnapshotAsync(sessionId, cancellation);
            return await SubmitCommandAsync(new DispatcherCommand
            {
                ContractVersion = "2.0",
                CommandId = Guid.NewGuid().ToString(),
                IdempotencyKey = Guid.NewGuid().ToString(),
                SessionId = sessionId,
                CommandType = "ARCHIVE",
                ExpectedEpoch = snapshot.SessionEpoch,
                ExpectedRevision = snapshot.Session.AggregateRevision,
                TargetType = "SESSION",
                TargetId = sessionId,
                Payload = new JObject(),
                Reason = null,
            }, cancellation);
        }

        // -----------------------------------------------------------------------------------
        // Function: SendTrainingAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sends an authorised training request. It never sends one anonymously:
        //     the backend's 403 for that is final, which would drop a command it may already have
        //     accepted. A 401 ends the login that was used.
        //
        // Args:
        //     method (string): The HTTP method.
        //     path (string): The path after /api/v2/sessions.
        //     body (string): The JSON body, or null.
        //     cancellation (CancellationToken): Cancels the request.
        //     configure (Action<HttpRequest>): Adjusts the request before sending, for example
        //         headers or timeout.
        //
        // Returns:
        //     Task<string>: The reply body.
        //
        // Throws:
        //     NotLoggedInException: If there is no login or the backend rejected it.
        private async Task<string> SendTrainingAsync(string method, string path, string body, CancellationToken cancellation,
            Action<HttpRequest> configure = null)
        {
            // Never send a training request anonymously: the backend's 403 for that is final,
            // which would drop a command it may already have accepted.
            if (!session.IsLoggedIn) throw new NotLoggedInException("Log in to use training sessions.");
            var request = Request(method, "/api/v2/sessions" + path, body);
            session.Authorize(request);
            configure?.Invoke(request);
            var tokenUsed = session.Token;
            try
            {
                return await SendAsync(request, cancellation, _ => session.Expire(tokenUsed));
            }
            catch (BackendErrorException error) when (error.Error.Status == 401)
            {
                throw new NotLoggedInException("Your login has expired. Log in again to continue.");
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Expect
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function parses and validates a reply that must be a particular contract
        //     message.
        //
        // Args:
        //     json (string): The reply body.
        //
        // Returns:
        //     T: The message.
        //
        // Throws:
        //     ContractException: If the reply is invalid or another message type.
        private static T Expect<T>(string json) where T : class, IContractMessage =>
            ContractParser.ParseMessage(json) as T ?? throw new ContractException($"Expected {typeof(T).Name}");

        // -----------------------------------------------------------------------------------
        // Function: Segment
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks an ID and escapes it for use in a URL path.
        //
        // Args:
        //     id (string): The ID.
        //
        // Returns:
        //     string: The escaped ID.
        //
        // Throws:
        //     ArgumentException: If the ID is empty or longer than 128 characters.
        private static string Segment(string id)
        {
            RequireId(id, nameof(id));
            return Uri.EscapeDataString(id);
        }

        // -----------------------------------------------------------------------------------
        // Function: RequireId
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks that an ID is 1 to 128 characters, as the backend requires.
        //
        // Args:
        //     id (string): The ID.
        //     name (string): The parameter name, for the error.
        //
        // Throws:
        //     ArgumentException: If the ID is empty or too long.
        private static void RequireId(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) throw new ArgumentException("Identifiers are 1-128 characters.", name);
        }
    }
}
