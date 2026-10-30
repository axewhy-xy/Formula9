using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Backend
{
    public sealed partial class TrainingApi
    {
        public async Task<RouteEditorContext> GetRouteEditorAsync(string id, string route, CancellationToken cancellation = default)
        {
            var value = ContractParser.Parse<RouteEditorContext>(await SendTrainingAsync("GET", $"/{Segment(id)}/routes/{Segment(route)}/editor", null, cancellation));
            if (value.SessionId != id || value.RouteId != route) throw new ContractException("Route context does not match the request");
            return value;
        }

        public async Task<RouteEditPreview> PreviewRouteAsync(string id, RouteEditRequest draft, CancellationToken cancellation = default)
        {
            var value = ContractParser.Parse<RouteEditPreview>(await SendTrainingAsync("POST", $"/{Segment(id)}/route-previews", ContractParser.Serialize(draft), cancellation));
            if (value.SessionId != id || value.RouteId != draft.RouteId || value.PreviewRevision != draft.DraftRevision)
                throw new ContractException("Route preview does not match the draft");
            return value;
        }

        public async Task<CommandStatus> ApproveRouteAsync(string id, RouteApprovalRequest approval, CancellationToken cancellation = default) =>
            Expect<CommandStatus>(await SendTrainingAsync("POST", $"/{Segment(id)}/route-approvals", ContractParser.Serialize(approval), cancellation));

        // These endpoints have no shared JSON schema. Validate their embedded snapshots through
        // the normal parser; never put a hypothetical or historical snapshot in the live store.
        public async Task<JObject> PreviewIncidentAsync(DispatcherCommand command, CancellationToken cancellation = default)
        {
            var value = ReadDocument(await SendTrainingAsync("POST", $"/{Segment(command.SessionId)}/incident-previews", ContractParser.Serialize(command), cancellation));
            var snapshot = ContractParser.Parse<FullSnapshot>(value["snapshot"]?.ToString());
            if (snapshot.SessionId != command.SessionId) throw new ContractException("Incident preview is for another session");
            return value;
        }

        public async Task<JObject> GetIncidentReportAsync(string id, string incident, bool debrief, CancellationToken cancellation = default)
        {
            var value = ReadDocument(await SendTrainingAsync("GET", $"/{Segment(id)}/incidents/{Segment(incident)}/{(debrief ? "debrief" : "history")}", null, cancellation));
            if ((string)value["incident_id"] != incident) throw new ContractException("Incident report does not match the request");
            if (!debrief)
                foreach (var raw in value["snapshots"] as JArray ?? throw new ContractException("Missing history snapshots"))
                    if (ContractParser.Parse<FullSnapshot>(raw.ToString()).SessionId != id) throw new ContractException("History is for another session");
            return value;
        }

        private static JObject ReadDocument(string json)
        {
            try { return JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
            catch (JsonException error) { throw new ContractException("Invalid incident reply: " + error.Message); }
        }
    }
}
