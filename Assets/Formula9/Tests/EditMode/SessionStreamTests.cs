/*
 * Unit tests for the session stream (Backend/SessionStream.cs, SessionController.PumpStream).
 *
 * Author: Lau Xiang Yang
 * Date Created: 18/11/2026
 * Last Modified: 18/11/2026
 *
 * Description:
 *     Part of SessionControllerTests, over the same scripted backend, with a fake WebSocket the
 *     tests push messages into. Covers the stream's URL and login, streamed snapshots taking over
 *     from the 1 s snapshot poll (and events fetched only when a snapshot has new ones), stale
 *     snapshots passed over without complaint, streamed traffic reported once and turned on and
 *     off with the 3D city, the poll taking over when a stream ends or goes silent, retries
 *     backing off while no stream delivers, and the stream messages checked against the
 *     contract.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Formula9.Tests
{
    public partial class SessionControllerTests
    {
        // -----------------------------------------------------------------------------------
        // Class: FakeStreamSocket
        // -----------------------------------------------------------------------------------
        // Description:
        //     This class stands in for the backend's end of a session stream. Push hands the
        //     stream a message as if the backend sent it; Close ends it with a close code.
        //     Continuations run at once on the test's thread, so a push is read and parsed
        //     before it returns.
        //
        //     Attributes: Url and Headers of the connection; Sent, the client's messages; Refuse
        //     to fail the connection; Hold to keep it connecting until FinishConnecting;
        //     FailSends to fail every send; Disposed once the client closed it. As a real
        //     WebSocket, it refuses a send before it has connected.
        private sealed class FakeStreamSocket : IStreamSocket
        {
            public Uri Url;
            public IReadOnlyDictionary<string, string> Headers;
            public readonly List<string> Sent = new List<string>();
            public bool Refuse, Hold, FailSends, Disposed;
            private bool open;
            private readonly TaskCompletionSource<bool> connecting = new TaskCompletionSource<bool>();
            private readonly Queue<string> ready = new Queue<string>();
            private TaskCompletionSource<string> receiving;

            public int? CloseStatus { get; private set; }

            public Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellation)
            {
                Url = url;
                Headers = headers;
                if (Refuse) return Task.FromException(new WebSocketException("Connection refused"));
                if (Hold) return ConnectLater();
                open = true;
                return Task.CompletedTask;
            }

            private async Task ConnectLater()
            {
                await connecting.Task.ConfigureAwait(false);
                open = true;
            }

            public void FinishConnecting() => WithoutContext(() => connecting.SetResult(true));

            public Task SendAsync(string text, CancellationToken cancellation)
            {
                if (!open) throw new InvalidOperationException("The WebSocket is not connected");
                if (FailSends) throw new WebSocketException("Connection lost");
                Sent.Add(text);
                return Task.CompletedTask;
            }

            public Task<string> ReceiveAsync(CancellationToken cancellation)
            {
                if (ready.Count > 0) return Task.FromResult(ready.Dequeue());
                var next = receiving = new TaskCompletionSource<string>();
                cancellation.Register(() => next.TrySetCanceled());
                return next.Task;
            }

            public void Push(string text)
            {
                var waiting = receiving;
                receiving = null;
                if (waiting == null || waiting.Task.IsCompleted)
                {
                    ready.Enqueue(text);
                    return;
                }
                WithoutContext(() => waiting.SetResult(text));
            }

            public void Close(int status)
            {
                CloseStatus = status;
                Push(null);
            }

            public void Dispose() => Disposed = true;
        }

        // -----------------------------------------------------------------------------------
        // Function: WithoutContext
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function runs an action with no synchronisation context. The stream's reading
        //     runs on at once only where no synchronisation context would have it wait for the
        //     main thread's next turn, which a test never gives it.
        //
        // Args:
        //     action (Action): What to run.
        private static void WithoutContext(Action action)
        {
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { action(); }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
        }

        private List<FakeStreamSocket> sockets;
        private bool refuseNextStream, holdNextStream;

        // -----------------------------------------------------------------------------------
        // Function: UseFakeStreams
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function turns the controller's stream on over fake sockets, one a stream.
        private void UseFakeStreams()
        {
            sockets = new List<FakeStreamSocket>();
            api.StreamSockets = () =>
            {
                var socket = new FakeStreamSocket { Refuse = refuseNextStream, Hold = holdNextStream };
                sockets.Add(socket);
                return socket;
            };
            controller.UseStream = true;
        }

        // -----------------------------------------------------------------------------------
        // Function: SnapshotMessage
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a stream's snapshot message.
        //
        // Args:
        //     snapshot (JObject): The snapshot, copied as it is now.
        //
        // Returns:
        //     string: The message.
        private static string SnapshotMessage(JObject snapshot) => new JObject
        {
            ["type"] = "snapshot", ["session_id"] = snapshot["session_id"], ["snapshot"] = snapshot.DeepClone(),
        }.ToString(Formatting.None);

        // -----------------------------------------------------------------------------------
        // Function: TrafficMessage
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a stream's traffic message, the frames as FakeBackend.Traffic
        //     makes them.
        //
        // Args:
        //     ticks (long[]): The frames' ticks, oldest first.
        //
        // Returns:
        //     string: The message.
        private string TrafficMessage(params long[] ticks)
        {
            var message = backend.Traffic(ticks);
            message["type"] = "traffic";
            return message.ToString(Formatting.None);
        }

        // -----------------------------------------------------------------------------------
        // Function: Advance
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function moves the backend's snapshot on, as a commit does.
        //
        // Args:
        //     ticks (int): How many ticks.
        //     events (int): How many new events it includes.
        private void Advance(int ticks, int events = 0)
        {
            var tick = (long)backend.Snapshot["tick"] + ticks;
            backend.Snapshot["tick"] = tick;
            backend.Snapshot["simulation_time_s"] = tick * 0.05;
            backend.Snapshot["snapshot_id"] = $"snapshot-{tick}";
            backend.Snapshot["event_sequence"] = (long)backend.Snapshot["event_sequence"] + events;
        }

        // -----------------------------------------------------------------------------------
        // Function: AJoinedSessionIsStreamedWithTheLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     Once a session is joined, its stream opens on the backend's address as ws://, with
        //     the login, the snapshot interval and whether traffic is wanted; nothing opens
        //     before a join, or without UseStream.
        [Test]
        public async Task AJoinedSessionIsStreamedWithTheLogin()
        {
            UseFakeStreams();
            controller.PumpStream(false);
            Assert.IsEmpty(sockets, "no stream before a session is joined");
            controller.UseStream = false;
            await controller.JoinAsync(SessionId);
            controller.PumpStream(false);
            Assert.IsEmpty(sockets, "no stream unless asked for");
            controller.UseStream = true;
            controller.PumpStream(true);
            var socket = sockets.Single();
            Assert.AreEqual("ws", socket.Url.Scheme);
            Assert.AreEqual("backend.test", socket.Url.Host);
            Assert.AreEqual($"/api/v2/sessions/{SessionId}/stream", socket.Url.AbsolutePath);
            StringAssert.Contains("traffic=true", socket.Url.Query);
            StringAssert.Contains("after_tick=-1", socket.Url.Query);
            StringAssert.Contains("snapshot_interval_ms=250", socket.Url.Query);
            Assert.AreEqual("Bearer token-1", socket.Headers["Authorization"]);
            Assert.IsFalse(controller.Streaming, "not until it delivers");
            Assert.AreEqual(new Uri("wss://example.test:8443/a"), TrainingApi.StreamUrl(new Uri("https://example.test:8443/a")));
        }

        // -----------------------------------------------------------------------------------
        // Function: StreamedSnapshotsTakeOverFromThePoll
        // -----------------------------------------------------------------------------------
        // Description:
        //     While the stream delivers, the poll fetches no snapshot. Each newer streamed
        //     snapshot is taken as a polled one would be; events are fetched only for one that
        //     has new events. One no newer than the snapshot held is passed over without an
        //     error.
        [Test]
        public async Task StreamedSnapshotsTakeOverFromThePoll()
        {
            UseFakeStreams();
            await controller.JoinAsync(SessionId);
            controller.PumpStream(false);
            var socket = sockets.Single();
            // The stream's first snapshot is the one just polled: delivered, but nothing new
            socket.Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            Assert.IsTrue(controller.Streaming);
            Assert.AreEqual(1, snapshots.Count);
            var polled = backend.To("/snapshot").Count();
            var asked = backend.To("/events").Count();
            await controller.RefreshAsync();
            Assert.AreEqual(polled, backend.To("/snapshot").Count(), "no snapshot is polled while the stream delivers");
            Assert.AreEqual(asked, backend.To("/events").Count(), "no events are missing");

            Advance(10);
            socket.Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            Assert.AreEqual(2, snapshots.Count);
            Assert.AreEqual(210, controller.Store.Snapshot.Tick);
            Assert.AreEqual(asked, backend.To("/events").Count(), "a snapshot without new events fetches none");

            Advance(10, events: 2);
            backend.OnEvents = request => Json(backend.Replay(FakeBackend.After(request), 2));
            socket.Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            Assert.AreEqual(3, snapshots.Count);
            CollectionAssert.AreEqual(new long[] { 7 }, backend.To("/events").Skip(asked).Select(FakeBackend.After));
            Assert.AreEqual(9, controller.Store.LastEventSequence);

            // An older one (as one sent before a poll brought a newer) is passed over
            var older = (JObject)backend.Snapshot.DeepClone();
            older["tick"] = 215;
            older["snapshot_id"] = "snapshot-215";
            older["event_sequence"] = 7;
            socket.Push(SnapshotMessage(older));
            controller.PumpStream(false);
            Assert.AreEqual(3, snapshots.Count);
            Assert.IsEmpty(errors);
            Assert.IsTrue(controller.Available);
        }

        // -----------------------------------------------------------------------------------
        // Function: StreamedTrafficIsReportedOnceAndFollowsTheCity
        // -----------------------------------------------------------------------------------
        // Description:
        //     Streamed traffic frames are reported once each, in order, and the traffic is not
        //     polled meanwhile. Hiding the city tells the stream to stop sending it, and showing
        //     it again to go on from the newest frame taken.
        [Test]
        public async Task StreamedTrafficIsReportedOnceAndFollowsTheCity()
        {
            UseFakeStreams();
            var accepted = new List<IReadOnlyList<TrafficFrame>>();
            controller.TrafficFramesAccepted += accepted.Add;
            await controller.JoinAsync(SessionId);
            controller.PumpStream(true);
            var socket = sockets.Single();
            socket.Push(SnapshotMessage(backend.Snapshot));
            socket.Push(TrafficMessage(10, 12));
            socket.Push(TrafficMessage(12, 14));
            controller.PumpStream(true);
            CollectionAssert.AreEqual(new long[] { 10, 12, 14 }, accepted.SelectMany(f => f).Select(f => f.Tick).ToList());
            Assert.AreEqual(-1, await controller.RefreshTrafficAsync(SessionController.TrafficWait));
            Assert.IsEmpty(backend.To("/traffic"), "the traffic is not polled while the stream sends it");

            controller.PumpStream(false);
            Assert.AreEqual(JObject.Parse("{\"type\":\"traffic\",\"enabled\":false,\"after_tick\":14}").ToString(), JObject.Parse(socket.Sent.Last()).ToString());
            controller.PumpStream(true);
            Assert.AreEqual(2, socket.Sent.Count);
            Assert.AreEqual(true, (bool)JObject.Parse(socket.Sent.Last())["enabled"]);
            Assert.AreEqual(14L, (long)JObject.Parse(socket.Sent.Last())["after_tick"]);
        }

        // -----------------------------------------------------------------------------------
        // Function: TrafficAskedForBeforeTheStreamConnectsIsSentOnceItHas
        // -----------------------------------------------------------------------------------
        // Description:
        //     The city shown while the stream is still connecting: the request for traffic
        //     waits for the connection and is sent then, not dropped (which would leave neither
        //     the stream nor the poll bringing traffic).
        [Test]
        public async Task TrafficAskedForBeforeTheStreamConnectsIsSentOnceItHas()
        {
            UseFakeStreams();
            holdNextStream = true;
            await controller.JoinAsync(SessionId);
            controller.PumpStream(false);
            holdNextStream = false;
            var socket = sockets.Single();
            controller.PumpStream(true);
            Assert.IsEmpty(socket.Sent, "nothing is sent before the stream connects");

            socket.FinishConnecting();
            Assert.AreEqual(1, socket.Sent.Count);
            Assert.AreEqual(true, (bool)JObject.Parse(socket.Sent.Single())["enabled"]);
            socket.Push(SnapshotMessage(backend.Snapshot));
            socket.Push(TrafficMessage(10));
            var accepted = new List<IReadOnlyList<TrafficFrame>>();
            controller.TrafficFramesAccepted += accepted.Add;
            controller.PumpStream(true);
            Assert.IsTrue(controller.Streaming);
            CollectionAssert.AreEqual(new long[] { 10 }, accepted.SelectMany(f => f).Select(f => f.Tick).ToList());
            Assert.AreEqual(1, socket.Sent.Count, "asked once");
        }

        // -----------------------------------------------------------------------------------
        // Function: AFailedSendEndsTheStream
        // -----------------------------------------------------------------------------------
        // Description:
        //     A traffic request the stream could not send ends the stream, and the poll takes
        //     over at once, traffic included, rather than wait on frames never asked for.
        [Test]
        public async Task AFailedSendEndsTheStream()
        {
            UseFakeStreams();
            await controller.JoinAsync(SessionId);
            controller.PumpStream(false);
            var socket = sockets.Single();
            socket.Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            Assert.IsTrue(controller.Streaming);
            var polled = backend.To("/snapshot").Count();

            socket.FailSends = true;
            WithoutContext(() => controller.PumpStream(true));
            controller.PumpStream(true);
            Assert.IsFalse(controller.Streaming);
            Assert.IsTrue(socket.Disposed);
            Assert.AreEqual(polled + 1, backend.To("/snapshot").Count(), "the poll takes over at once");
            backend.OnTraffic = _ => Json(backend.Traffic(10));
            await controller.RefreshTrafficAsync(SessionController.TrafficWait);
            Assert.IsNotEmpty(backend.To("/traffic"), "and polls the traffic");
        }

        // -----------------------------------------------------------------------------------
        // Function: AnEndedStreamHandsBackToThePollAndIsTriedAgainLater
        // -----------------------------------------------------------------------------------
        // Description:
        //     A stream that ends after delivering is followed by a poll at once, and the next is
        //     tried after StreamRetryInterval. One that cannot connect waits twice as long before
        //     the next try. A stream that delivers resets the wait.
        [Test]
        public async Task AnEndedStreamHandsBackToThePollAndIsTriedAgainLater()
        {
            UseFakeStreams();
            await controller.JoinAsync(SessionId);
            controller.PumpStream(false);
            sockets[0].Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            Assert.IsTrue(controller.Streaming);
            var polled = backend.To("/snapshot").Count();

            sockets[0].Push("{\"type\":\"error\",\"session_id\":\"session-001\",\"error\":{\"code\":\"SESSION_RECOVERING\",\"message\":\"later\",\"retryable\":true}}");
            sockets[0].Close(1013);
            controller.PumpStream(false);
            Assert.IsFalse(controller.Streaming);
            Assert.IsTrue(sockets[0].Disposed);
            Assert.AreEqual(polled + 1, backend.To("/snapshot").Count(), "the poll takes over at once");
            await controller.RefreshAsync();
            Assert.AreEqual(polled + 2, backend.To("/snapshot").Count(), "and goes on polling");

            controller.PumpStream(false);
            Assert.AreEqual(1, sockets.Count, "not straight away");
            refuseNextStream = true;
            clock += SessionController.StreamRetryInterval;
            controller.PumpStream(false);
            Assert.AreEqual(2, sockets.Count);
            controller.PumpStream(false);
            Assert.IsFalse(controller.Streaming);

            // It never connected: the wait doubles
            refuseNextStream = false;
            clock += SessionController.StreamRetryInterval;
            controller.PumpStream(false);
            Assert.AreEqual(2, sockets.Count);
            clock += SessionController.StreamRetryInterval;
            controller.PumpStream(false);
            Assert.AreEqual(3, sockets.Count);
            sockets[2].Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            Assert.IsTrue(controller.Streaming);
        }

        // -----------------------------------------------------------------------------------
        // Function: ASilentStreamIsDroppedAndLeavingClosesIt
        // -----------------------------------------------------------------------------------
        // Description:
        //     A stream heard nothing from for StreamSilenceLimit is dropped (heartbeats count);
        //     leaving the session closes its stream.
        [Test]
        public async Task ASilentStreamIsDroppedAndLeavingClosesIt()
        {
            UseFakeStreams();
            await controller.JoinAsync(SessionId);
            controller.PumpStream(false);
            sockets[0].Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            clock += SessionController.StreamSilenceLimit;
            sockets[0].Push("{\"type\":\"heartbeat\",\"session_id\":\"session-001\"}");
            controller.PumpStream(false);
            clock += SessionController.StreamSilenceLimit;
            controller.PumpStream(false);
            Assert.IsTrue(controller.Streaming, "a heartbeat counts");
            clock += TimeSpan.FromSeconds(1);
            controller.PumpStream(false);
            Assert.IsFalse(controller.Streaming);
            Assert.IsTrue(sockets[0].Disposed);

            clock += SessionController.StreamRetryInterval;
            controller.PumpStream(false);
            sockets[1].Push(SnapshotMessage(backend.Snapshot));
            controller.PumpStream(false);
            Assert.IsTrue(controller.Streaming);
            controller.Leave();
            Assert.IsFalse(controller.Streaming);
            Assert.IsTrue(sockets[1].Disposed);
            controller.PumpStream(false);
            Assert.AreEqual(2, sockets.Count, "no stream without a session");
        }

        // -----------------------------------------------------------------------------------
        // Function: StreamMessagesAreCheckedAgainstTheContract
        // -----------------------------------------------------------------------------------
        // Description:
        //     Each kind of stream message parses; one for another session, or a snapshot that
        //     breaks the contract, is refused, and a type this client does not know is skipped.
        [Test]
        public void StreamMessagesAreCheckedAgainstTheContract()
        {
            var snapshot = SessionStream.Parse(SnapshotMessage(backend.Snapshot), SessionId);
            Assert.AreEqual(StreamMessageKind.Snapshot, snapshot.Kind);
            Assert.AreEqual(200, snapshot.Snapshot.Tick);
            var traffic = SessionStream.Parse(TrafficMessage(4, 6), SessionId);
            CollectionAssert.AreEqual(new long[] { 4, 6 }, traffic.Frames.Select(f => f.Tick).ToList());
            Assert.AreEqual(StreamMessageKind.Heartbeat, SessionStream.Parse("{\"type\":\"heartbeat\",\"session_id\":\"session-001\"}", SessionId).Kind);
            var error = SessionStream.Parse("{\"type\":\"error\",\"session_id\":\"session-001\",\"error\":{\"code\":\"INVALID_REFERENCE\",\"message\":\"gone\",\"retryable\":false}}", SessionId);
            Assert.AreEqual("INVALID_REFERENCE", error.Error.Code);
            Assert.IsNull(SessionStream.Parse("{\"type\":\"newer\",\"session_id\":\"session-001\"}", SessionId));

            Assert.Throws<ContractException>(() => SessionStream.Parse("{\"type\":\"heartbeat\",\"session_id\":\"session-002\"}", SessionId));
            var broken = (JObject)backend.Snapshot.DeepClone();
            broken.Remove("snapshot_id");
            Assert.Throws<ContractException>(() => SessionStream.Parse(SnapshotMessage(broken), SessionId));
            Assert.Throws<ContractException>(() => SessionStream.Parse("{\"type\":", SessionId));

            // The shared fixture, one message of each type, as the backend's contract tests check it
            var shared = Fixture("stream_messages")["messages"].Select(m => SessionStream.Parse(m.ToString(Formatting.None), SessionId)).ToList();
            CollectionAssert.AreEqual(new[] { StreamMessageKind.Snapshot, StreamMessageKind.Traffic, StreamMessageKind.Heartbeat, StreamMessageKind.Error },
                shared.Select(m => m.Kind).ToList());
            CollectionAssert.AreEqual(new[] { 3, 1200, -450, 900, 1000 }, shared[1].Frames.Single().Vehicles);
            Assert.AreEqual(40.0, shared[1].Frames.Single().SignalFaults["junction-1"], 1e-9);
            Assert.IsTrue(shared[3].Error.Retryable);
        }
    }
}
