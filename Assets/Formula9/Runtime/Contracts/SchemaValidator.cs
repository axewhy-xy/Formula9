using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Formula9.Contracts
{
    // Checks JSON against the subset of JSON Schema the v2 contracts use, with pydantic's strict
    // types: integers must be JSON integers, booleans must be booleans, and numbers must be finite.
    internal sealed class SchemaValidator
    {
        private readonly JObject root;
        private readonly List<string> errors = new List<string>();
        private static readonly Dictionary<string, Regex> Patterns = new Dictionary<string, Regex>();

        private SchemaValidator(JObject root) => this.root = root;

        public static IReadOnlyList<string> Validate(JToken value, JObject document, JObject schema)
        {
            var validator = new SchemaValidator(document);
            validator.Check(value, schema, "$");
            return validator.errors;
        }

        private bool Check(JToken value, JObject schema, string path)
        {
            var before = errors.Count;
            if (schema.TryGetValue("$ref", out var reference)) Check(value, Resolve((string)reference), path);
            if (schema.TryGetValue("anyOf", out var anyOf) && !Matches(value, (JArray)anyOf, path, 1, false))
                Fail(path, "matches none of the allowed shapes");
            if (schema.TryGetValue("oneOf", out var oneOf) && !Matches(value, (JArray)oneOf, path, 1, true))
                Fail(path, "must match exactly one message shape");
            if (schema.TryGetValue("const", out var constant) && !JToken.DeepEquals(value, constant))
                Fail(path, $"must be {constant.ToString(Newtonsoft.Json.Formatting.None)}");
            if (schema.TryGetValue("enum", out var choices) && !choices.Any(choice => JToken.DeepEquals(choice, value)))
                Fail(path, $"'{Describe(value)}' is not an allowed value");
            if (schema.TryGetValue("type", out var type) && !HasType(value, type))
            {
                Fail(path, $"must be {type.ToString(Newtonsoft.Json.Formatting.None)}");
                return false;
            }
            switch (value.Type)
            {
                case JTokenType.String: CheckString((string)value, schema, path); break;
                case JTokenType.Integer:
                case JTokenType.Float: CheckNumber(value, schema, path); break;
                case JTokenType.Array: CheckArray((JArray)value, schema, path); break;
                case JTokenType.Object: CheckObject((JObject)value, schema, path); break;
            }
            return errors.Count == before;
        }

        private bool Matches(JToken value, JArray options, string path, int required, bool exactlyOne)
        {
            var matches = 0;
            var collected = new List<string>();
            foreach (JObject option in options)
            {
                var trial = new SchemaValidator(root);
                trial.Check(value, option, path);
                if (trial.errors.Count == 0) matches++;
                else collected.AddRange(trial.errors);
            }
            var ok = exactlyOne ? matches == required : matches >= required;
            // Report the inner reasons when no option fits; they explain the rejection best.
            if (!ok && matches == 0) errors.AddRange(collected.Distinct().Take(8));
            return ok;
        }

        private JObject Resolve(string reference)
        {
            if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                throw new ContractException($"Unsupported schema reference {reference}");
            var name = reference.Substring("#/$defs/".Length);
            if (root["$defs"]?[name] is JObject definition) return definition;
            throw new ContractException($"Unknown schema definition {name}");
        }

        private static bool HasType(JToken value, JToken type)
        {
            if (type.Type == JTokenType.Array) return type.Any(t => HasType(value, t));
            switch ((string)type)
            {
                case "string": return value.Type == JTokenType.String;
                case "integer": return value.Type == JTokenType.Integer;
                case "number": return value.Type == JTokenType.Integer || (value.Type == JTokenType.Float && IsFinite(value));
                case "boolean": return value.Type == JTokenType.Boolean;
                case "array": return value.Type == JTokenType.Array;
                case "object": return value.Type == JTokenType.Object;
                case "null": return value.Type == JTokenType.Null;
                default: return false;
            }
        }

        private static bool IsFinite(JToken value)
        {
            var number = value.Value<double>();
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        private void CheckString(string value, JObject schema, string path)
        {
            // pydantic counts Unicode code points, not UTF-16 units.
            var length = new StringInfo(value).LengthInTextElements;
            if (schema.TryGetValue("minLength", out var min) && length < (int)min) Fail(path, $"is shorter than {min}");
            if (schema.TryGetValue("maxLength", out var max) && length > (int)max) Fail(path, $"is longer than {max}");
            if (schema.TryGetValue("pattern", out var pattern) && !PatternMatches((string)pattern, value))
                Fail(path, $"'{value}' does not match {pattern}");
        }

        private void CheckNumber(JToken value, JObject schema, string path)
        {
            var number = value.Value<double>();
            if (schema.TryGetValue("minimum", out var min) && number < (double)min) Fail(path, $"must be at least {min}");
            if (schema.TryGetValue("maximum", out var max) && number > (double)max) Fail(path, $"must be at most {max}");
            if (schema.TryGetValue("exclusiveMinimum", out var above) && number <= (double)above) Fail(path, $"must be greater than {above}");
            if (schema.TryGetValue("exclusiveMaximum", out var below) && number >= (double)below) Fail(path, $"must be less than {below}");
        }

        private void CheckArray(JArray value, JObject schema, string path)
        {
            if (schema.TryGetValue("minItems", out var min) && value.Count < (int)min) Fail(path, $"needs at least {min} items");
            if (schema.TryGetValue("maxItems", out var max) && value.Count > (int)max) Fail(path, $"allows at most {max} items");
            if (schema["items"] is JObject items)
                for (var i = 0; i < value.Count; i++) Check(value[i], items, $"{path}[{i}]");
        }

        private void CheckObject(JObject value, JObject schema, string path)
        {
            var properties = schema["properties"] as JObject;
            if (schema["required"] is JArray required)
                foreach (var name in required.Values<string>())
                    if (!value.ContainsKey(name)) Fail($"{path}.{name}", "is required");
            var patterns = schema["patternProperties"] as JObject;
            foreach (var property in value.Properties())
            {
                var childPath = $"{path}.{property.Name}";
                if (schema["propertyNames"] is JObject names) Check(new JValue(property.Name), names, childPath + " (key)");
                if (properties?[property.Name] is JObject declared)
                {
                    Check(property.Value, declared, childPath);
                    continue;
                }
                var matched = false;
                if (patterns != null)
                    foreach (var entry in patterns.Properties())
                        if (PatternMatches(entry.Name, property.Name))
                        {
                            matched = true;
                            Check(property.Value, (JObject)entry.Value, childPath);
                        }
                if (matched) continue;
                var extra = schema["additionalProperties"];
                if (extra is JValue flag && flag.Type == JTokenType.Boolean && !(bool)flag) Fail(childPath, "is not allowed here");
                else if (extra is JObject extraSchema) Check(property.Value, extraSchema, childPath);
            }
        }

        private static bool PatternMatches(string pattern, string value)
        {
            // .NET's '$' also matches before a final newline; the backend's does not.
            if (pattern.EndsWith("$", StringComparison.Ordinal) && value.EndsWith("\n", StringComparison.Ordinal)) return false;
            Regex regex;
            lock (Patterns)
            {
                if (!Patterns.TryGetValue(pattern, out regex))
                    Patterns[pattern] = regex = new Regex(pattern, RegexOptions.ECMAScript);
            }
            return regex.IsMatch(value);
        }

        private static string Describe(JToken value) =>
            value.Type == JTokenType.String ? (string)value : value.ToString(Newtonsoft.Json.Formatting.None);

        private void Fail(string path, string message) => errors.Add($"{path} {message}");
    }
}
