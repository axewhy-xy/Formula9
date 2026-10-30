using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Contracts
{
    // A backend message or document that broke the shared v2 contract.
    public sealed class ContractException : Exception
    {
        public IReadOnlyList<string> Problems { get; }

        public ContractException(string message) : this(new[] { message }) { }

        public ContractException(IReadOnlyList<string> problems)
            : base(string.Join("; ", problems)) => Problems = problems;
    }

    // The four messages the training API returns, told apart by message_type.
    public interface IContractMessage
    {
        string MessageType { get; }
        string SessionId { get; }
    }

    // The top-level messages the backend sends. Their fields are generated from the schemas in
    // Generated/ContractModels.g.cs; these halves only mark them as parseable messages.
    public sealed partial class FullSnapshot : IContractMessage { }
    public sealed partial class EventReplay : IContractMessage { }
    public sealed partial class CommandStatus : IContractMessage { }
    public sealed partial class ErrorResponse : IContractMessage { }

    // Parses backend JSON only after it passes the same schema and cross-reference rules the
    // backend applies, so the rest of the client never sees a half-valid document.
    public static class ContractParser
    {
        private static readonly Dictionary<string, Type> Messages = new Dictionary<string, Type>
        {
            ["FULL_SNAPSHOT"] = typeof(FullSnapshot),
            ["EVENT_REPLAY"] = typeof(EventReplay),
            ["COMMAND_STATUS"] = typeof(CommandStatus),
            ["ERROR"] = typeof(ErrorResponse),
        };

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            // Timestamps stay strings, exactly as the schema describes them.
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);
        private static readonly Dictionary<string, JObject> Documents = new Dictionary<string, JObject>();

        public static IContractMessage ParseMessage(string json)
        {
            var token = Read(json);
            if (!(token is JObject message) || !(message["message_type"] is JValue kind) || kind.Type != JTokenType.String
                || !Messages.TryGetValue((string)kind, out var type))
                throw new ContractException("$.message_type is not a known training message");
            return (IContractMessage)Convert(token, type);
        }

        public static T Parse<T>(string json) where T : class => (T)Convert(Read(json), typeof(T));

        // Serialises an outgoing document after checking it, so the client never sends what the
        // backend would reject as malformed.
        public static string Serialize<T>(T value) where T : class
        {
            var token = JToken.FromObject(value, Serializer);
            Validate(token, typeof(T));
            return token.ToString(Formatting.None);
        }

        private static JToken Read(string json)
        {
            if (json == null) throw new ContractException("$ is empty");
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
                {
                    // Duplicate keys are rejected; a lenient parser would silently keep one of them.
                    var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new ContractException("$ has trailing content");
                    return token;
                }
            }
            catch (JsonException error)
            {
                throw new ContractException($"$ is not valid JSON: {error.Message}");
            }
        }

        private static object Convert(JToken token, Type type)
        {
            Validate(token, type);
            object value;
            try { value = token.ToObject(type, Serializer); }
            catch (JsonException error) { throw new ContractException($"$ could not be read as {type.Name}: {error.Message}"); }
            var problems = ContractRules.Check(value);
            if (problems.Count > 0) throw new ContractException(problems);
            return value;
        }

        private static void Validate(JToken token, Type type)
        {
            if (!ContractSchemas.Locations.TryGetValue(type, out var location))
                throw new ArgumentException($"{type.Name} has no contract schema", nameof(type));
            var document = Document(location.Document);
            var schema = string.IsNullOrEmpty(location.Pointer) ? document : (JObject)document.SelectToken(Pointer(location.Pointer));
            var problems = SchemaValidator.Validate(token, document, schema);
            if (problems.Count > 0) throw new ContractException(problems);
        }

        private static JObject Document(string name)
        {
            lock (Documents)
            {
                if (!Documents.TryGetValue(name, out var document))
                    Documents[name] = document = JObject.Parse(ContractSchemas.Documents[name]);
                return document;
            }
        }

        // "#/$defs/Name" -> "['$defs']['Name']" for SelectToken, since '$' is special there.
        private static string Pointer(string pointer)
        {
            var parts = pointer.TrimStart('#', '/').Split('/');
            return string.Concat(Array.ConvertAll(parts, part => $"['{part}']"));
        }
    }
}
