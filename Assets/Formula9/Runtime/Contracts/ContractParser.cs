/*
 * Reads and writes v2 contract JSON in the Unity client.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's contract layer. Backend JSON is turned into the generated contract
 *     classes only after it passes the same schema and cross-field rules the backend applies, so
 *     the rest of the client never sees a half-valid document. Outgoing documents are checked the
 *     same way before they are sent.
 */

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Formula9.Contracts
{
    // -----------------------------------------------------------------------------------
    // Class: ContractException
    // -----------------------------------------------------------------------------------
    // Description:
    //     This exception means a backend message or document broke the shared v2 contract.
    //
    //     Attributes: Problems lists every rule that failed, each starting with the JSON path.
    public sealed class ContractException : Exception
    {
        public IReadOnlyList<string> Problems { get; }

        // -----------------------------------------------------------------------------------
        // Function: ContractException (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the exception for a single problem.
        //
        // Args:
        //     message (string): The problem.
        public ContractException(string message) : this(new[] { message }) { }

        // -----------------------------------------------------------------------------------
        // Function: ContractException (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the exception for several problems.
        //
        // Args:
        //     problems (IReadOnlyList<string>): The problems found.
        public ContractException(IReadOnlyList<string> problems)
            : base(string.Join("; ", problems)) => Problems = problems;
    }

    // -----------------------------------------------------------------------------------
    // Class: IContractMessage
    // -----------------------------------------------------------------------------------
    // Description:
    //     This interface is one of the four messages the training API returns, told apart by
    //     message_type.
    public interface IContractMessage
    {
        string MessageType { get; }
        string SessionId { get; }
    }

    // -----------------------------------------------------------------------------------
    // Class: FullSnapshot, EventReplay, CommandStatus, ErrorResponse
    // -----------------------------------------------------------------------------------
    // Description:
    //     These are the top-level messages the backend sends. Their fields are generated from the
    //     schemas in Generated/ContractModels.g.cs; these halves only mark them as parseable
    //     messages.
    public sealed partial class FullSnapshot : IContractMessage { }
    public sealed partial class EventReplay : IContractMessage { }
    public sealed partial class CommandStatus : IContractMessage { }
    public sealed partial class ErrorResponse : IContractMessage { }

    // -----------------------------------------------------------------------------------
    // Class: ContractParser
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class parses and serialises contract documents. Timestamps stay strings, exactly as
    //     the schema describes them, and numbers are read as doubles.
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
        // Every embedded schema, parsed once when the parser is first used and only read after, so
        // replies can be parsed on any thread (TrainingApi.ParseInBackground)
        private static readonly IReadOnlyDictionary<string, JObject> Documents = LoadDocuments();

        // -----------------------------------------------------------------------------------
        // Function: ParseMessage
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function parses a training API reply whose type is only known from its
        //     message_type.
        //
        // Args:
        //     json (string): The reply body.
        //
        // Returns:
        //     IContractMessage: The validated message.
        //
        // Throws:
        //     ContractException: If the JSON is invalid, of an unknown message type, or breaks the
        //         contract.
        public static IContractMessage ParseMessage(string json)
        {
            var token = Read(json);
            if (!(token is JObject message) || !(message["message_type"] is JValue kind) || kind.Type != JTokenType.String
                || !Messages.TryGetValue((string)kind, out var type))
                throw new ContractException("$.message_type is not a known training message");
            return (IContractMessage)Convert(token, type);
        }

        // -----------------------------------------------------------------------------------
        // Function: Parse
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function parses a document of a known contract type.
        //
        // Args:
        //     json (string): The document.
        //
        // Returns:
        //     T: The validated document.
        //
        // Throws:
        //     ContractException: If the JSON is invalid or breaks the contract.
        public static T Parse<T>(string json) where T : class => (T)Convert(Read(json), typeof(T));

        // -----------------------------------------------------------------------------------
        // Function: Serialize
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function serialises an outgoing document after checking it, so the client never
        //     sends what the backend would reject as malformed.
        //
        // Args:
        //     value (T): The document.
        //
        // Returns:
        //     string: Compact JSON.
        //
        // Throws:
        //     ContractException: If the document breaks its schema.
        public static string Serialize<T>(T value) where T : class
        {
            var token = JToken.FromObject(value, Serializer);
            Validate(token, typeof(T));
            return token.ToString(Formatting.None);
        }

        // -----------------------------------------------------------------------------------
        // Function: Read
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads JSON strictly: duplicate keys and trailing content are rejected,
        //     where a lenient parser would silently keep one of the keys or ignore the rest.
        //
        // Args:
        //     json (string): The text.
        //
        // Returns:
        //     JToken: The parsed JSON.
        //
        // Throws:
        //     ContractException: If the text is empty or not valid JSON.
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

        // -----------------------------------------------------------------------------------
        // Function: Convert
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks JSON against its schema, converts it, then applies the cross-
        //     field rules.
        //
        // Args:
        //     token (JToken): The JSON.
        //     type (Type): The contract class.
        //
        // Returns:
        //     object: The validated document.
        //
        // Throws:
        //     ContractException: If any check fails.
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

        // -----------------------------------------------------------------------------------
        // Function: Validate
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks JSON against the embedded schema for a contract class.
        //
        // Args:
        //     token (JToken): The JSON.
        //     type (Type): The contract class.
        //
        // Throws:
        //     ArgumentException: If the class has no schema.
        //     ContractException: If the JSON breaks the schema.
        private static void Validate(JToken token, Type type)
        {
            if (!ContractSchemas.Locations.TryGetValue(type, out var location))
                throw new ArgumentException($"{type.Name} has no contract schema", nameof(type));
            var document = Document(location.Document);
            var schema = string.IsNullOrEmpty(location.Pointer) ? document : (JObject)document.SelectToken(Pointer(location.Pointer));
            var problems = SchemaValidator.Validate(token, document, schema);
            if (problems.Count > 0) throw new ContractException(problems);
        }

        // -----------------------------------------------------------------------------------
        // Function: Document
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function returns an embedded schema document.
        //
        // Args:
        //     name (string): The schema file name.
        //
        // Returns:
        //     JObject: The schema document.
        private static JObject Document(string name) => Documents[name];

        // -----------------------------------------------------------------------------------
        // Function: LoadDocuments
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function parses every embedded schema document.
        //
        // Returns:
        //     IReadOnlyDictionary<string, JObject>: The documents by file name.
        private static IReadOnlyDictionary<string, JObject> LoadDocuments()
        {
            var documents = new Dictionary<string, JObject>();
            foreach (var entry in ContractSchemas.Documents) documents[entry.Key] = JObject.Parse(entry.Value);
            return documents;
        }

        // -----------------------------------------------------------------------------------
        // Function: Pointer
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function turns a JSON pointer such as "#/$defs/Name" into "['$defs']['Name']"
        //     for SelectToken, since '$' is special there.
        //
        // Args:
        //     pointer (string): The JSON pointer.
        //
        // Returns:
        //     string: The SelectToken path.
        private static string Pointer(string pointer)
        {
            var parts = pointer.TrimStart('#', '/').Split('/');
            return string.Concat(Array.ConvertAll(parts, part => $"['{part}']"));
        }
    }
}
