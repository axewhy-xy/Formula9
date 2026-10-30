using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    // The backend's error body: {"error": {"code", "message", "retryable", "current_revision"}}.
    public sealed class ApiError
    {
        public int Status;
        public string Code;
        public string Message;
        public bool Retryable;
        public long? CurrentRevision;
    }

    // A complete error reply. For a training command this means it was not journaled.
    public sealed class BackendErrorException : Exception
    {
        public ApiError Error { get; }
        public BackendErrorException(ApiError error) : base($"{error.Code} ({error.Status}): {error.Message}") => Error = error;
    }

    // No complete, readable reply. The request may or may not have taken effect.
    public sealed class NoReplyException : Exception
    {
        // The HTTP status when a reply arrived but was unreadable; 0 when nothing arrived at all.
        public int Status { get; }

        public NoReplyException(string message, int status = 0) : base(message) => Status = status;
    }

    // There is no valid login, either locally or because the backend rejected the token (401).
    public sealed class NotLoggedInException : Exception
    {
        public NotLoggedInException(string message) : base(message) { }
    }

    public sealed class LoginSession
    {
        [JsonProperty("username", Required = Required.Always)] public string Username { get; set; }
        [JsonProperty("token", Required = Required.Always)] public string Token { get; set; }
        [JsonProperty("expires_at", Required = Required.Always)] public string ExpiresAt { get; set; }
        [JsonProperty("expires_in", Required = Required.Always)] public long ExpiresIn { get; set; }
    }

    // Shared request plumbing for the auth and training clients.
    public abstract class BackendClientBase
    {
        protected readonly IHttpTransport Transport;
        protected readonly Uri BaseUrl;

        protected BackendClientBase(IHttpTransport transport, Uri baseUrl)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            BaseUrl = baseUrl ?? BackendEndpoint.ConfiguredUrl();
        }

        protected HttpRequest Request(string method, string path, string body = null) =>
            new HttpRequest { Method = method, Url = BackendEndpoint.ApiUrl(BaseUrl, path), Body = body };

        // Sends the request and returns the body of a 2xx reply; anything else becomes one of the
        // exceptions above, so callers can tell a refusal from an unknown outcome.
        protected async Task<string> SendAsync(HttpRequest request, CancellationToken cancellation, Action<ApiError> onUnauthorized = null)
        {
            var response = await Transport.SendAsync(request, cancellation);
            if (response.Failure == HttpFailure.Cancelled) throw new OperationCanceledException(cancellation);
            if (!response.Complete) throw new NoReplyException($"No complete reply from {request.Method} {request.Url.AbsolutePath}");
            if (response.Status >= 200 && response.Status < 300) return response.Body;
            var error = ReadError(response);
            if (error == null) throw new NoReplyException($"Unreadable {response.Status} reply from {request.Method} {request.Url.AbsolutePath}", response.Status);
            if (response.Status == 401 && onUnauthorized != null) onUnauthorized(error);
            throw new BackendErrorException(error);
        }

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

    // /api/auth: log in, sign up and log out against the team's shared accounts.
    public sealed class AuthClient : BackendClientBase
    {
        private readonly AuthSession session;

        public AuthClient(IHttpTransport transport, AuthSession session, Uri baseUrl = null) : base(transport, baseUrl) =>
            this.session = session ?? throw new ArgumentNullException(nameof(session));

        public Task LogInAsync(string username, string password, CancellationToken cancellation = default) =>
            BeginAsync("/api/auth/login", username, password, cancellation);

        public Task SignUpAsync(string username, string password, CancellationToken cancellation = default) =>
            BeginAsync("/api/auth/signup", username, password, cancellation);

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

    // /api/v2/sessions: every call needs a login and returns validated contract objects.
    public sealed class TrainingApi : BackendClientBase
    {
        private readonly AuthSession session;

        public TrainingApi(IHttpTransport transport, AuthSession session, Uri baseUrl = null) : base(transport, baseUrl) =>
            this.session = session ?? throw new ArgumentNullException(nameof(session));

        public async Task<SessionListResponse> ListSessionsAsync(CancellationToken cancellation = default) =>
            ContractParser.Parse<SessionListResponse>(await SendTrainingAsync("GET", "", null, cancellation));

        // Retrying with the same key returns the same session instead of creating another.
        public async Task<TrainingSession> CreateSessionAsync(CreateSessionRequest request, string idempotencyKey,
            CancellationToken cancellation = default)
        {
            RequireId(idempotencyKey, nameof(idempotencyKey));
            var body = ContractParser.Serialize(request);
            return ContractParser.Parse<TrainingSession>(await SendTrainingAsync("POST", "", body, cancellation,
                http => http.Headers["Idempotency-Key"] = idempotencyKey));
        }

        public async Task<FullSnapshot> GetSnapshotAsync(string sessionId, CancellationToken cancellation = default)
        {
            var snapshot = Expect<FullSnapshot>(await SendTrainingAsync("GET", $"/{Segment(sessionId)}/snapshot", null, cancellation));
            if (snapshot.SessionId != sessionId) throw new ContractException("Snapshot is for a different session");
            return snapshot;
        }

        public async Task<EventReplay> GetEventsAsync(string sessionId, long after, int limit = 100, CancellationToken cancellation = default)
        {
            if (after < 0 || limit < 1) throw new ArgumentOutOfRangeException(nameof(after));
            var replay = Expect<EventReplay>(await SendTrainingAsync("GET", $"/{Segment(sessionId)}/events?after={after}&limit={limit}", null, cancellation));
            if (replay.SessionId != sessionId || replay.AfterSequence != after) throw new ContractException("Event replay does not match the request");
            return replay;
        }

        // A 202 carries the command's journaled status; QUEUED means its outcome is still open.
        public async Task<CommandStatus> SubmitCommandAsync(DispatcherCommand command, CancellationToken cancellation = default)
        {
            var body = ContractParser.Serialize(command);
            return Expect<CommandStatus>(await SendTrainingAsync("POST", $"/{Segment(command.SessionId)}/commands", body, cancellation));
        }

        public async Task<CommandStatus> GetCommandStatusAsync(string sessionId, string idempotencyKey, CancellationToken cancellation = default) =>
            Expect<CommandStatus>(await SendTrainingAsync("GET", $"/{Segment(sessionId)}/commands/{Segment(idempotencyKey)}", null, cancellation));

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

        private static T Expect<T>(string json) where T : class, IContractMessage =>
            ContractParser.ParseMessage(json) as T ?? throw new ContractException($"Expected {typeof(T).Name}");

        private static string Segment(string id)
        {
            RequireId(id, nameof(id));
            return Uri.EscapeDataString(id);
        }

        private static void RequireId(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) throw new ArgumentException("Identifiers are 1-128 characters.", name);
        }
    }
}
