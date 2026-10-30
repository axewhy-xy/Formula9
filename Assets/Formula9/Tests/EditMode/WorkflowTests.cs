using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public class WorkflowTests
    {
        private static string Fixture(string name) => File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json"));
        private sealed class Backend : IHttpTransport
        {
            public readonly List<HttpRequest> Requests = new List<HttpRequest>();
            public Func<HttpRequest, HttpResponse> Reply;
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            { Requests.Add(request); return Task.FromResult(Reply(request)); }
        }
        private static HttpResponse Ok(string body) => new HttpResponse { Status = 200, Body = body };
        private static FullSnapshot Snapshot() => ContractParser.Parse<FullSnapshot>(Fixture("snapshot"));

        [Test]
        public void RouteApprovalIsInvalidatedByClosureRouteEpochAndMandatoryChecks()
        {
            var s = Snapshot(); s.Session.Status = "READY";
            var p = ContractParser.Parse<RouteEditPreview>(Fixture("route_edit_preview"));
            p.RouteId = s.Routes[0].RouteId; p.RouteRevision = s.Routes[0].Revision;
            p.ClosureRevisions = s.Disruptions.ToDictionary(d => d.DisruptionId, d => d.AggregateRevision);
            Assert.IsTrue(DecisionRules.RoutePreviewMatches(s, p));
            p.Checks[0].Status = "UNKNOWN";
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, p)); p.Checks[0].Status = "PASS";
            s.Disruptions[0].AggregateRevision++;
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, p)); s.Disruptions[0].AggregateRevision--;
            s.Routes[0].Revision++;
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, p)); s.Routes[0].Revision--;
            s.SessionEpoch++;
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, p));
        }

        [Test]
        public void ViaNodesPreserveOrderAndRejectUnsafeNumbersAndOversizedDrafts()
        {
            CollectionAssert.AreEqual(new long[] { 3, 1, 3 }, DecisionRules.ParseViaNodes("3, 1\n3"));
            Assert.Throws<ArgumentException>(() => DecisionRules.ParseViaNodes("3.5"));
            Assert.Throws<ArgumentException>(() => DecisionRules.ParseViaNodes("9007199254740992"));
            Assert.Throws<ArgumentException>(() => DecisionRules.ParseViaNodes(string.Join(",", Enumerable.Range(0, 17))));
        }

        [TestCase("CHARGE_NOW", "RESERVE_CHARGER", "CHARGER")]
        [TestCase("CONTINUE", "CONTINUE_SERVICE", "VEHICLE")]
        public void ChargingUsesCurrentOfferAndTargetRevision(string action, string commandType, string target)
        {
            var s = ContractParser.Parse<FullSnapshot>(Fixture("low_reserve_snapshot"));
            var option = s.ChargeOptions.First(o => o.Action == action);
            s.Session.Status = "RUNNING";
            var c = DecisionRules.ChargeCommand(s, option.OptionId, "  Training decision  ");
            Assert.AreEqual(commandType, c.CommandType); Assert.AreEqual(target, c.TargetType);
            Assert.AreEqual(option.Revision, (long)c.Payload["option_revision"]);
            Assert.AreEqual("Training decision", c.Reason);
            ContractParser.Serialize(c);
            option.ExpiresAtTick = s.Tick - 1;
            Assert.Throws<ArgumentException>(() => DecisionRules.ChargeCommand(s, option.OptionId, "reason"));
        }

        [Test]
        public void IncidentApprovalRequiresLivePassingOptionAndReason()
        {
            var s = Snapshot(); s.Session.Status = "RUNNING";
            var i = s.Incidents[0]; i.State = "OPTIONS_READY";
            var candidate = s.Candidates.First(c => c.IncidentId == i.IncidentId);
            var c = DecisionRules.IncidentCommand(s, i.IncidentId, candidate.CandidateId, "Safe hold");
            Assert.AreEqual(i.AggregateRevision, c.ExpectedRevision);
            Assert.AreEqual(candidate.CandidateId, (string)c.Payload["candidate_id"]);
            ContractParser.Serialize(c);
            Assert.Throws<ArgumentException>(() => DecisionRules.IncidentCommand(s, i.IncidentId, candidate.CandidateId, " "));
            candidate.Validation = "FAIL";
            Assert.Throws<ArgumentException>(() => DecisionRules.IncidentCommand(s, i.IncidentId, candidate.CandidateId, "reason"));
        }

        [Test]
        public void OptionExpiryTickIsInclusiveAsInTheBackendContract()
        {
            var s = ContractParser.Parse<FullSnapshot>(Fixture("low_reserve_snapshot"));
            s.Session.Status = "RUNNING";
            var option = s.ChargeOptions.First(o => o.Validation == "PASS");
            option.ExpiresAtTick = s.Tick;
            Assert.DoesNotThrow(() => DecisionRules.ChargeCommand(s, option.OptionId, "At expiry tick"));
            s = Snapshot(); s.Session.Status = "RUNNING";
            var incident = s.Incidents[0]; incident.State = "OPTIONS_READY";
            var candidate = s.Candidates.First(c => c.IncidentId == incident.IncidentId);
            candidate.ExpiresAtTick = s.Tick;
            Assert.DoesNotThrow(() => DecisionRules.IncidentCommand(s, incident.IncidentId, candidate.CandidateId, "At expiry tick"));
        }

        [Test]
        public async Task ReadinessReportsTheVerifiedManifestAndAssetBytes()
        {
            var manifest = ContractParser.Parse<TrainingSceneRevision>(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/m2_scene/manifest.json")));
            var package = new Formula9.World.ScenePackage { Manifest = manifest, ManifestSha256 = new string('a', 64) };
            foreach (var asset in manifest.Assets.Where(a => a.Mandatory))
                package.Assets[asset.AssetId] = File.ReadAllBytes(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/m2_scene", asset.Path));
            var backend = new Backend { Reply = r => Ok(new JObject { ["ready"] = true, ["scene_revision"] = manifest.SceneRevision, ["manifest_sha256"] = package.ManifestSha256 }.ToString()) };
            var loader = new Formula9.World.SceneLoader(backend, new Uri("http://backend.test"));
            await loader.ReportReadyAsync(package);
            var sent = JObject.Parse(backend.Requests.Single().Body);
            Assert.AreEqual(package.ManifestSha256, (string)sent["manifest_sha256"]);
            Assert.IsTrue((bool)sent["render_ready"]);
            CollectionAssert.AreEquivalent(package.Assets.Keys, ((JObject)sent["asset_hashes"]).Properties().Select(p => p.Name));
            foreach (var asset in package.Assets)
                Assert.AreEqual(Formula9.World.SceneLoader.Sha256(asset.Value), (string)sent["asset_hashes"][asset.Key]);
            StringAssert.EndsWith("/readiness", backend.Requests.Single().Url.AbsolutePath);
            backend.Reply = _ => Ok("{\"ready\":false}");
            Assert.ThrowsAsync<Formula9.World.SceneException>(() => loader.ReportReadyAsync(package));
        }

        [Test]
        public async Task IncidentPreviewAndHistoryNeverReplaceTheLiveSnapshot()
        {
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var s = Snapshot(); s.Session.Status = "RUNNING";
            var incident = s.Incidents[0]; incident.State = "OPTIONS_READY";
            var candidate = s.Candidates.First(c => c.IncidentId == incident.IncidentId);
            var command = DecisionRules.IncidentCommand(s, incident.IncidentId, candidate.CandidateId, "Preview only");
            var backend = new Backend { Reply = _ => Ok(new JObject { ["snapshot"] = JObject.Parse(Fixture("snapshot")) }.ToString()) };
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var originalId = s.SnapshotId;
            await api.PreviewIncidentAsync(command);
            Assert.AreEqual(originalId, s.SnapshotId);
            StringAssert.EndsWith("/incident-previews", backend.Requests.Last().Url.AbsolutePath);
            backend.Reply = _ => Ok(new JObject { ["incident_id"] = incident.IncidentId, ["snapshots"] = new JArray(JObject.Parse(Fixture("snapshot"))) }.ToString());
            await api.GetIncidentReportAsync(s.SessionId, incident.IncidentId, false);
            var wrong = JObject.Parse(Fixture("snapshot")); wrong["session_id"] = "another-session";
            backend.Reply = _ => Ok(new JObject { ["incident_id"] = incident.IncidentId, ["snapshots"] = new JArray(wrong) }.ToString());
            Assert.ThrowsAsync<ContractException>(() => api.GetIncidentReportAsync(s.SessionId, incident.IncidentId, false));
        }

        [Test]
        public async Task RouteEndpointsValidateDraftAndNeverUseTheGenericCommandEndpoint()
        {
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var backend = new Backend { Reply = r => Ok(Fixture(r.Url.AbsolutePath.EndsWith("/editor") ? "route_editor_context" : "route_edit_preview")) };
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var context = await api.GetRouteEditorAsync("session-001", "route-m2-detour");
            var preview = await api.PreviewRouteAsync("session-001", new RouteEditRequest
            { RouteId = context.RouteId, ExpectedRouteRevision = context.RouteRevision, DraftRevision = 3 });
            Assert.AreEqual(3, preview.PreviewRevision);
            StringAssert.EndsWith("/route-previews", backend.Requests.Last().Url.AbsolutePath);
            Assert.AreEqual("Bearer token", backend.Requests.Last().Headers["Authorization"]);
            Assert.ThrowsAsync<ContractException>(() => api.GetRouteEditorAsync("other-session", "route-m2-detour"));
        }

        [Test]
        public async Task LostRouteApprovalReplyRetriesTheExactApprovalIdentity()
        {
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var raw = JObject.Parse(Fixture("snapshot")); raw["session"]["status"] = "READY";
            var s = ContractParser.Parse<FullSnapshot>(raw.ToString());
            var preview = ContractParser.Parse<RouteEditPreview>(Fixture("route_edit_preview"));
            preview.RouteId = s.Routes[0].RouteId; preview.RouteRevision = s.Routes[0].Revision;
            preview.ClosureRevisions = s.Disruptions.ToDictionary(d => d.DisruptionId, d => d.AggregateRevision);
            var gate = new TaskCompletionSource<bool>(); var posts = 0;
            var backend = new Backend();
            backend.Reply = request =>
            {
                if (request.Url.AbsolutePath.EndsWith("/snapshot")) return Ok(raw.ToString());
                if (request.Url.AbsolutePath.EndsWith("/events"))
                {
                    var replay = JObject.Parse(Fixture("event_replay")); replay["events"] = new JArray();
                    replay["after_sequence"] = s.EventSequence; replay["next_sequence"] = s.EventSequence; replay["has_more"] = false;
                    return Ok(replay.ToString());
                }
                if (!request.Url.AbsolutePath.EndsWith("/route-approvals")) throw new Exception("Wrong endpoint: " + request.Url);
                if (++posts == 1) return new HttpResponse { Failure = HttpFailure.Incomplete };
                var approval = JObject.Parse(request.Body); var status = JObject.Parse(Fixture("command_status"));
                status["command_id"] = approval["command_id"]; status["idempotency_key"] = approval["idempotency_key"];
                status["target_type"] = "ROUTE"; status["target_id"] = preview.RouteId; status["status"] = "APPLIED";
                return Ok(status.ToString());
            };
            var controller = new SessionController(new TrainingApi(backend, auth, new Uri("http://backend.test")), auth, delay: (_, __) => gate.Task);
            var ledger = new List<CommandOutcome>(); controller.CommandLedgerChanged += ledger.Add;
            try
            {
                await controller.JoinAsync(s.SessionId);
                Assert.IsTrue(await controller.ApproveRouteAsync(preview, "Avoid closure"));
                Assert.IsTrue(controller.HasPendingCommand);
                Assert.AreEqual("PENDING_CONFIRMATION", ledger.Last().Status);
                var finished = new TaskCompletionSource<bool>(); controller.CommandFinished += _ => finished.TrySetResult(true);
                gate.SetResult(true);
                Assert.AreEqual(finished.Task, await Task.WhenAny(finished.Task, Task.Delay(3000)));
                var requests = backend.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/route-approvals")).ToList();
                Assert.AreEqual(2, requests.Count); Assert.AreEqual(requests[0].Body, requests[1].Body);
                var ids = JObject.Parse(requests[0].Body)["target_vehicle_ids"].Values<string>();
                CollectionAssert.AreEquivalent(s.Vehicles.Where(v => v.RouteId == preview.RouteId).Select(v => v.VehicleId), ids);
                Assert.AreEqual("APPLIED", ledger.Last().Status);
                Assert.AreEqual(ledger.First().CommandId, ledger.Last().CommandId);
                backend.Reply = request => new HttpResponse { Status = 409, Body = "{\"error\":{\"code\":\"STALE_REVISION\",\"message\":\"Preview expired\"}}" };
                Assert.IsTrue(await controller.ApproveRouteAsync(preview, "Recheck the same route"));
                Assert.IsFalse(controller.HasPendingCommand);
                Assert.AreEqual("NOT_ACCEPTED", ledger.Last().Status);
                Assert.AreEqual("STALE_REVISION", ledger.Last().FailureCode);
            }
            finally { controller.Shutdown(); }
        }
    }
}
