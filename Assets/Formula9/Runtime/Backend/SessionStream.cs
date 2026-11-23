/*
 * A training session's snapshots and traffic, pushed by the backend over a WebSocket.
 *
 * Author: Lau Xiang Yang
 * Date Created: 18/11/2026
 * Last Modified: 18/11/2026
 *
 * Description:
 *     Part of the Unity client's backend layer: the client half of the backend's push transport
 *     (WS /api/v2/sessions/{session_id}/stream, contracts/v2/stream.py). Instead of polling the
 *     snapshot once a second and long-polling the traffic, the client keeps one stream open to the
 *     joined session, and the backend sends each newer committed snapshot (at most one per interval)
 *     and every traffic frame as it is made.
 *
 *     A SessionStream reads and parses its messages off the main thread and keeps them, in order,
 *     for the main thread to take (TryTake). It never throws: a stream that cannot connect, or that
 *     the backend ends, finishes with a Closed message, and the caller goes back to polling over
 *     HTTP. Durable events and commands always go over HTTP.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    // -----------------------------------------------------------------------------------
    // Class: StreamMessageKind
    // -----------------------------------------------------------------------------------
    // Description:
    //     This enum is what a session stream message carries. Closed is the stream's last: it
    //     could not connect, or it ended.
    public enum StreamMessageKind
    {
        Snapshot,
        Traffic,
        Heartbeat,
        Error,
        Closed,
    }

    // -----------------------------------------------------------------------------------
    // Class: StreamMessage
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is one parsed message of a session stream.
    //
    //     Attributes: Kind; SessionId; Snapshot for a snapshot; Frames, oldest first, for
    //     traffic; Error for an error (its Status is 0: it came over the stream, not HTTP);
    //     CloseStatus, the WebSocket close code, for Closed, null when the connection was lost or
    //     never made.
    public sealed class StreamMessage
    {
        public StreamMessageKind Kind;
        public string SessionId;
        public FullSnapshot Snapshot;
        public List<TrafficFrame> Frames;
        public ApiError Error;
        public int? CloseStatus;
    }

    // -----------------------------------------------------------------------------------
    // Class: IStreamSocket
    // -----------------------------------------------------------------------------------
    // Description:
    //     This interface is one WebSocket carrying text messages, so tests can stand in for the
    //     backend. One receive and one send may be under way at once.
    public interface IStreamSocket : IDisposable
    {
        Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellation);
        Task SendAsync(string text, CancellationToken cancellation);
        // The next text message, or null once the other side has closed the stream.
        Task<string> ReceiveAsync(CancellationToken cancellation);
        // The close code the other side sent, once it has closed.
        int? CloseStatus { get; }
    }

    // -----------------------------------------------------------------------------------
    // Class: ClientWebSocketTransport
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is an IStreamSocket over System.Net.WebSockets.ClientWebSocket. Binary
    //     messages are skipped; the backend sends none.
    public sealed class ClientWebSocketTransport : IStreamSocket
    {
        // The most a message may be: far more than a snapshot or a second's traffic frames
        private const int MaxMessageBytes = 16 * 1024 * 1024;
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly byte[] buffer = new byte[64 * 1024];

        public int? CloseStatus => (int?)socket.CloseStatus;

        public Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellation)
        {
            foreach (var header in headers) socket.Options.SetRequestHeader(header.Key, header.Value);
            return socket.ConnectAsync(url, cancellation);
        }

        public Task SendAsync(string text, CancellationToken cancellation) =>
            socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellation);

        public async Task<string> ReceiveAsync(CancellationToken cancellation)
        {
            using (var message = new MemoryStream())
            {
                while (true)
                {
                    var part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) return null;
                    message.Write(buffer, 0, part.Count);
                    if (message.Length > MaxMessageBytes) throw new WebSocketException("Stream message too large");
                    if (!part.EndOfMessage) continue;
                    if (part.MessageType == WebSocketMessageType.Text) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                }
            }
        }

        public void Dispose() => socket.Dispose();
    }

    // -----------------------------------------------------------------------------------
    // Class: SessionStream
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is one open stream of one session. Its messages are read and parsed as
    //     they come, then kept, in order, until the main thread takes them; the last is always a
    //     Closed. Dispose ends it.
    //
    //     Attributes: SessionId; Closed, true once it has ended (its Closed message may still be
    //     waiting to be taken).
    public sealed class SessionStream : IDisposable
    {
        private readonly IStreamSocket socket;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        // Cancelled when a send fails: the reading then ends the stream, as a lost connection does
        private readonly CancellationTokenSource broken = new CancellationTokenSource();
        private readonly ConcurrentQueue<StreamMessage> inbox = new ConcurrentQueue<StreamMessage>();
        private readonly object sendLock = new object();
        // Done once connected (cancelled if the connection could not be made): sends queue behind it,
        // as a WebSocket refuses a send before it is open
        private readonly TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>();
        private Task sending;
        private int closed;

        public string SessionId { get; }
        public bool Closed => Volatile.Read(ref closed) != 0;

        // -----------------------------------------------------------------------------------
        // Function: SessionStream (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function opens the stream: it connects and then reads until the stream ends.
        //
        // Args:
        //     socket (IStreamSocket): The WebSocket, not yet connected; the stream disposes it.
        //     url (Uri): The stream's ws:// or wss:// URL.
        //     headers (IReadOnlyDictionary<string, string>): The upgrade request's headers, such
        //         as the login.
        //     sessionId (string): The session.
        //     inBackground (bool): Connect, read and parse on a worker thread, as the app does;
        //         otherwise on the caller's, until the first wait, as tests over a fake socket do.
        internal SessionStream(IStreamSocket socket, Uri url, IReadOnlyDictionary<string, string> headers, string sessionId, bool inBackground)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            SessionId = sessionId;
            sending = connected.Task;
            if (inBackground) Task.Run(() => RunAsync(url, headers));
            else _ = RunAsync(url, headers);
        }

        // -----------------------------------------------------------------------------------
        // Function: TryTake
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes the oldest message not yet taken.
        //
        // Args:
        //     message (StreamMessage): The message, when there is one.
        //
        // Returns:
        //     bool: True when a message was taken.
        public bool TryTake(out StreamMessage message) => inbox.TryDequeue(out message);

        // -----------------------------------------------------------------------------------
        // Function: SetTraffic
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells the backend whether to send traffic frames, and after which
        //     tick. Called before the stream has connected, it is sent once it has. A send that
        //     fails ends the stream, so the caller goes back to polling rather than wait for
        //     frames the backend was never asked for.
        //
        // Args:
        //     enabled (bool): Whether to send them.
        //     afterTick (long): Only frames after this tick; -1 for the newest kept.
        public void SetTraffic(bool enabled, long afterTick)
        {
            var control = new JObject { ["type"] = "traffic", ["enabled"] = enabled, ["after_tick"] = Math.Max(-1, afterTick) };
            Send(control.ToString(Formatting.None));
        }

        // -----------------------------------------------------------------------------------
        // Function: Send
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function queues a text message after the connection and any message still
        //     being sent: a WebSocket takes one send at a time, and none before it is open.
        //
        // Args:
        //     text (string): The message.
        private void Send(string text)
        {
            if (Closed) return;
            lock (sendLock) sending = SendAfterAsync(sending, text);
        }

        private async Task SendAfterAsync(Task previous, string text)
        {
            try
            {
                // Only the connection can fail here: an earlier send never throws
                await previous.ConfigureAwait(false);
            }
            // Not connected: the reading has already ended the stream and said so
            catch (Exception) { return; }
            try
            {
                if (Closed) return;
                await socket.SendAsync(text, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The backend may not have the message, and the stream no longer says what the
                // caller asked for: end it, and the reading says so
                broken.Cancel();
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: RunAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects, then reads, parses and keeps each message until the stream
        //     ends, and finishes with a Closed message. Every await leaves the caller's context,
        //     so in the app nothing here runs on the main thread.
        //
        // Args:
        //     url (Uri): The stream's URL.
        //     headers (IReadOnlyDictionary<string, string>): The upgrade request's headers.
        private async Task RunAsync(Uri url, IReadOnlyDictionary<string, string> headers)
        {
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, broken.Token))
            {
                try
                {
                    try
                    {
                        await socket.ConnectAsync(url, headers, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Cancelled rather than failed: a queued send that never waits on it leaves
                        // no unobserved exception behind
                        connected.TrySetCanceled();
                        throw;
                    }
                    connected.TrySetResult(true);
                    while (!reading.IsCancellationRequested)
                    {
                        var text = await socket.ReceiveAsync(reading.Token).ConfigureAwait(false);
                        if (text == null) break;
                        var message = Parse(text, SessionId);
                        // A message this client does not know is from a newer backend: skipped
                        if (message != null) inbox.Enqueue(message);
                    }
                }
                // Connection refused or lost, a send that failed, a malformed message (the stream
                // can no longer be trusted), or Dispose: the caller polls over HTTP again
                catch (Exception) { }
            }
            int? status = null;
            try { status = socket.CloseStatus; } catch (Exception) { }
            Volatile.Write(ref closed, 1);
            inbox.Enqueue(new StreamMessage { Kind = StreamMessageKind.Closed, SessionId = SessionId, CloseStatus = status });
        }

        // -----------------------------------------------------------------------------------
        // Function: Parse
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function parses and checks one stream message: a snapshot against the
        //     contract, traffic frames as GET /traffic's.
        //
        // Args:
        //     json (string): The message.
        //     sessionId (string): The session the stream is for.
        //
        // Returns:
        //     StreamMessage: The message, or null for a type this client does not know.
        //
        // Throws:
        //     ContractException: If the message is malformed or for another session.
        public static StreamMessage Parse(string json, string sessionId)
        {
            if (!(ContractParser.ReadJson(json) is JObject root)) throw new ContractException("Stream message is not an object");
            if ((string)root["session_id"] != sessionId) throw new ContractException("Stream message is for another session");
            var message = new StreamMessage { SessionId = sessionId };
            switch ((string)root["type"])
            {
                case "snapshot":
                    message.Kind = StreamMessageKind.Snapshot;
                    message.Snapshot = ContractParser.ParseMessage(root["snapshot"] ?? throw new ContractException("Stream snapshot is missing"))
                        as FullSnapshot ?? throw new ContractException("Stream snapshot is not a FULL_SNAPSHOT");
                    if (message.Snapshot.SessionId != sessionId) throw new ContractException("Stream snapshot is for another session");
                    return message;
                case "traffic":
                    message.Kind = StreamMessageKind.Traffic;
                    message.Frames = TrainingApi.ParseTrafficFrames(root, sessionId);
                    return message;
                case "heartbeat":
                    message.Kind = StreamMessageKind.Heartbeat;
                    return message;
                case "error":
                    var error = root["error"] as JObject ?? throw new ContractException("Stream error has no error");
                    message.Kind = StreamMessageKind.Error;
                    message.Error = new ApiError
                    {
                        Code = (string)error["code"], Message = (string)error["message"],
                        Retryable = error["retryable"]?.Type == JTokenType.Boolean && (bool)error["retryable"],
                    };
                    return message;
                default:
                    return null;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Dispose
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function ends the stream; messages not yet taken are dropped.
        public void Dispose()
        {
            if (lifetime.IsCancellationRequested) return;
            Volatile.Write(ref closed, 1);
            lifetime.Cancel();
            socket.Dispose();
            while (inbox.TryDequeue(out _)) { }
        }
    }

    public sealed partial class TrainingApi
    {
        // -----------------------------------------------------------------------------------
        // Property: StreamSockets
        // -----------------------------------------------------------------------------------
        // Description:
        //     Makes the WebSocket each session stream uses; ClientWebSocketTransport by default.
        //     Tests stand in for the backend here.
        public Func<IStreamSocket> StreamSockets { get; set; } = () => new ClientWebSocketTransport();

        // -----------------------------------------------------------------------------------
        // Function: OpenSessionStream
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function opens a session's stream with the current login. It is read and
        //     parsed off the calling thread when ParseInBackground is on.
        //
        // Args:
        //     sessionId (string): The session.
        //     traffic (bool): Whether to receive traffic frames.
        //     afterTick (long): Only traffic frames after this tick; -1 for the newest kept.
        //     snapshotIntervalMs (int): The least time between two snapshots, 100 to 5000.
        //
        // Returns:
        //     SessionStream: The stream, or null without a login.
        public SessionStream OpenSessionStream(string sessionId, bool traffic, long afterTick, int snapshotIntervalMs)
        {
            if (!session.IsLoggedIn) return null;
            var http = Request("GET", $"/api/v2/sessions/{Segment(sessionId)}/stream?traffic={(traffic ? "true" : "false")}"
                                      + $"&after_tick={Math.Max(-1, afterTick)}&snapshot_interval_ms={Math.Max(100, Math.Min(5000, snapshotIntervalMs))}");
            session.Authorize(http);
            return new SessionStream(StreamSockets(), StreamUrl(http.Url), http.Headers, sessionId, ParseInBackground);
        }

        // -----------------------------------------------------------------------------------
        // Function: StreamUrl
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function turns an http(s) URL into the matching ws(s) one, port and all.
        //
        // Args:
        //     url (Uri): The HTTP URL.
        //
        // Returns:
        //     Uri: The WebSocket URL.
        public static Uri StreamUrl(Uri url)
        {
            var builder = new UriBuilder(url) { Scheme = url.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" };
            if (url.IsDefaultPort) builder.Port = -1;
            return builder.Uri;
        }
    }
}
