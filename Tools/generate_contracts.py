"""
Generate the Unity client's C# contract classes from this repository's v2 JSON schemas.

Author: Choo Jian Wei
Date Created: 30/10/2026
Last Modified: 30/10/2026

Description:
    The schemas stay the single source of truth: this script writes typed C# classes for every
    object they define, embeds the schemas so the client validates incoming JSON against the same
    rules as the backend, writes the shared alert cue table, and copies the shared fixtures and
    the 3D scene package for the EditMode tests.

    Usage (from this repository's root):

        python Tools/generate_contracts.py [--source contracts/v2]
"""

from __future__ import annotations

import argparse
import json
import keyword
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "Assets/Formula9/Runtime/Contracts/Generated"
FIXTURES = ROOT / "Assets/Formula9/Tests/EditMode/Fixtures"
NAMESPACE = "Formula9.Contracts"
CSHARP_KEYWORDS = {"base", "class", "event", "object", "string", "params", "operator", "internal", "fixed", "checked", "lock"}


"""
# -----------------------------------------------------------------------------------
# Function: pascal
# -----------------------------------------------------------------------------------
Description:
    This function turns a schema name into a C# identifier in PascalCase. A name starting with a
    digit gets a leading underscore.

Args:
    name (str): The schema or field name.

Returns:
    str: The identifier.
"""
def pascal(name: str) -> str:
    result = "".join(part[:1].upper() + part[1:] for part in re.split(r"[^A-Za-z0-9]+", name) if part)
    return result if result and not result[0].isdigit() else "_" + result


"""
# -----------------------------------------------------------------------------------
# Function: ref_name
# -----------------------------------------------------------------------------------
Description:
    This function reads the definition name from a "#/$defs/Name" reference.

Args:
    ref (str): The $ref value.

Returns:
    str: The definition name.

Raises:
    ValueError: If the reference is of another form.
"""
def ref_name(ref: str) -> str:
    if not ref.startswith("#/$defs/"):
        raise ValueError(f"Unsupported $ref {ref}")
    return ref.split("/")[-1]


"""
# -----------------------------------------------------------------------------------
# Class: Generator
# -----------------------------------------------------------------------------------
Description:
    This class collects every object the schemas define and writes their C# classes and the embedded
    schemas.

    Attributes: documents, schema file name to parsed schema; classes, class name to its document
    and schema.
"""
class Generator:
    """
    # -----------------------------------------------------------------------------------
    # Function: __init__
    # -----------------------------------------------------------------------------------
    Description:
        This function collects the $defs of every document, and each document that is itself an
        object.

    Args:
        documents (dict[str, dict]): Schema file name (without .schema.json) to parsed schema.
    """
    def __init__(self, documents: dict[str, dict]):
        self.documents = documents
        self.classes: dict[str, tuple[str, dict]] = {}  # class name -> (document key, schema)
        for key, document in documents.items():
            for name, schema in document.get("$defs", {}).items():
                self.add_class(name, key, schema)
            if document.get("type") == "object" and document.get("properties"):
                self.add_class(document["title"], key, document)

    """
    # -----------------------------------------------------------------------------------
    # Function: add_class
    # -----------------------------------------------------------------------------------
    Description:
        This function records one class. A standalone schema file repeats a $defs entry; the copies
        must agree, apart from their $id label.

    Args:
        name (str): The class name.
        document (str): The document it comes from.
        schema (dict): Its schema.

    Raises:
        ValueError: If two copies of a definition differ.
    """
    def add_class(self, name: str, document: str, schema: dict) -> None:
        if name in self.classes:
            # A standalone schema file repeats a $defs entry; only its $id label may differ.
            def rules(value: dict) -> str:
                return json.dumps({k: v for k, v in value.items() if k not in {"$id", "$schema"}}, sort_keys=True)
            if rules(self.classes[name][1]) != rules(schema):
                raise ValueError(f"Schema {name} differs between {self.classes[name][0]} and {document}")
            return
        self.classes[name] = (document, schema)

    """
    # -----------------------------------------------------------------------------------
    # Function: type_of
    # -----------------------------------------------------------------------------------
    Description:
        This function picks the C# type for a schema: references become their class, nullable unions
        their single other type, enums string or bool, arrays List and maps Dictionary. Anything
        else stays JToken.

    Args:
        schema (dict): The property schema.

    Returns:
        tuple[str, bool]: The C# type, and whether it is a value type (needs '?' to hold null).
    """
    def type_of(self, schema: dict) -> tuple[str, bool]:
        """C# type and whether that type is a value type (needs '?' to hold null)."""
        if "$ref" in schema:
            return pascal(ref_name(schema["$ref"])), False
        if "anyOf" in schema:
            options = [s for s in schema["anyOf"] if s.get("type") != "null"]
            if len(options) == 1:
                return self.type_of(options[0])
            return "JToken", False
        if "const" in schema or "enum" in schema:
            values = schema.get("enum", [schema.get("const")])
            if all(isinstance(v, bool) for v in values):
                return "bool", True
            if all(isinstance(v, str) for v in values):
                return "string", False
            return "JToken", False
        kind = schema.get("type")
        if isinstance(kind, list):
            kinds = [k for k in kind if k != "null"]
            return self.type_of({**schema, "type": kinds[0]}) if len(kinds) == 1 else ("JToken", False)
        if kind == "string":
            return "string", False
        if kind == "integer":
            return "long", True
        if kind == "number":
            return "double", True
        if kind == "boolean":
            return "bool", True
        if kind == "array":
            item, _ = self.type_of(schema.get("items", {}))
            return f"List<{item}>", False
        if kind == "object":
            if "properties" in schema:
                return "JObject", False
            values = schema.get("additionalProperties")
            if isinstance(values, dict) and values:
                value, _ = self.type_of(values)
                return f"Dictionary<string, {value}>", False
            patterns = schema.get("patternProperties")
            if patterns:
                value, _ = self.type_of(next(iter(patterns.values())))
                return f"Dictionary<string, {value}>", False
            return "JObject", False
        return "JToken", False

    """
    # -----------------------------------------------------------------------------------
    # Function: property
    # -----------------------------------------------------------------------------------
    Description:
        This function writes one C# property with its JSON name, whether it is required or may be
        null, and its default. pydantic's default_factory leaves no default in the schema, so an
        absent optional list or map starts empty.

    Args:
        class_name (str): The class being written.
        field (str): The JSON field name.
        schema (dict): The field's schema.
        required (bool): Whether the field is required.

    Returns:
        list[str]: The C# lines.
    """
    def property(self, class_name: str, field: str, schema: dict, required: bool) -> list[str]:
        cs_type, value_type = self.type_of(schema)
        nullable = (not required and "default" not in schema) or self.allows_null(schema) or (
            not required and schema.get("default") is None)
        member = pascal(field)
        if member == class_name or member in CSHARP_KEYWORDS or keyword.iskeyword(member):
            member += "Value"
        declared = cs_type + ("?" if nullable and value_type else "")
        initializer = ""
        default = schema.get("default")
        if not required and default is not None:
            if isinstance(default, bool):
                initializer = " = " + ("true" if default else "false") + ";"
            elif isinstance(default, (int, float)):
                initializer = f" = {default};"
            elif isinstance(default, str):
                initializer = " = " + json.dumps(default) + ";"
            elif default == [] and cs_type.startswith("List<"):
                initializer = f" = new {cs_type}();"
            elif default == {} and cs_type.startswith("Dictionary<"):
                initializer = f" = new {cs_type}();"
        elif not required and not self.allows_null(schema) and cs_type.startswith(("List<", "Dictionary<")):
            # pydantic's default_factory leaves no "default" in the schema; an absent list is empty.
            initializer = f" = new {cs_type}();"
        required_attr = "Required.AllowNull" if required and self.allows_null(schema) else (
            "Required.Always" if required else "Required.Default")
        lines = []
        if schema.get("description"):
            lines.append(f"        /// <summary>{schema['description']}</summary>")
        lines.append(f'        [JsonProperty("{field}", Required = {required_attr})]')
        lines.append(f"        public {declared} {member} {{ get; set; }}{initializer}")
        return lines

    """
    # -----------------------------------------------------------------------------------
    # Function: allows_null
    # -----------------------------------------------------------------------------------
    Description:
        This function tells whether a schema accepts null.

    Args:
        schema (dict): The schema.

    Returns:
        bool: True when null is allowed.
    """
    @staticmethod
    def allows_null(schema: dict) -> bool:
        return any(s.get("type") == "null" for s in schema.get("anyOf", [])) or (
            isinstance(schema.get("type"), list) and "null" in schema["type"])

    """
    # -----------------------------------------------------------------------------------
    # Function: class_source
    # -----------------------------------------------------------------------------------
    Description:
        This function writes one partial C# class. Unless the schema forbids extra properties, the
        class keeps additive 2.x fields from a newer backend instead of rejecting them.

    Args:
        name (str): The schema name.
        schema (dict): Its schema.

    Returns:
        str: The C# source.
    """
    def class_source(self, name: str, schema: dict) -> str:
        class_name = pascal(name)
        required = set(schema.get("required", []))
        body: list[str] = []
        for field, field_schema in schema.get("properties", {}).items():
            body += self.property(class_name, field, field_schema, field in required)
        extensible = schema.get("additionalProperties", True) is not False
        if extensible:
            body.append("        // Additive 2.x fields from a newer backend are kept, not rejected.")
            body.append("        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }")
        summary = schema.get("description", f"{name} from the shared v2 contract.").splitlines()[0]
        return "\n".join([f"    /// <summary>{summary}</summary>",
                          f"    public sealed partial class {class_name}", "    {", *body, "    }"])

    """
    # -----------------------------------------------------------------------------------
    # Function: models
    # -----------------------------------------------------------------------------------
    Description:
        This function writes ContractModels.g.cs: every class, sorted by name.

    Returns:
        str: The file's source.
    """
    def models(self) -> str:
        parts = [self.class_source(name, schema) for name, (_, schema) in sorted(self.classes.items())]
        return HEADER + "using System.Collections.Generic;\nusing Newtonsoft.Json;\nusing Newtonsoft.Json.Linq;\n\n" + \
            f"namespace {NAMESPACE}\n{{\n" + "\n\n".join(parts) + "\n}\n"

    """
    # -----------------------------------------------------------------------------------
    # Function: schemas
    # -----------------------------------------------------------------------------------
    Description:
        This function writes ContractSchemas.g.cs: each schema document exactly as published, and
        where each class's schema lives (document and JSON pointer).

    Returns:
        str: The file's source.
    """
    def schemas(self) -> str:
        documents = []
        for key, document in sorted(self.documents.items()):
            text = json.dumps(document, sort_keys=True, separators=(",", ":"))
            documents.append(f'            ["{key}"] = @"{text.replace(chr(34), chr(34) * 2)}",')
        locations = []
        for name, (document, schema) in sorted(self.classes.items()):
            pointer = "" if self.documents[document] is schema else f"#/$defs/{name}"
            locations.append(f'            [typeof({pascal(name)})] = ("{document}", "{pointer}"),')
        return HEADER + "using System;\nusing System.Collections.Generic;\n\n" + f"namespace {NAMESPACE}\n{{\n" + \
            "    public static partial class ContractSchemas\n    {\n" + \
            "        // Schema documents exactly as published in contracts/v2.\n" + \
            "        internal static readonly Dictionary<string, string> Documents = new Dictionary<string, string>\n        {\n" + \
            "\n".join(documents) + "\n        };\n\n" + \
            "        // Where each generated class's schema lives: (document, JSON pointer within it).\n" + \
            "        internal static readonly Dictionary<Type, (string Document, string Pointer)> Locations =\n" + \
            "            new Dictionary<Type, (string, string)>\n        {\n" + "\n".join(locations) + "\n        };\n    }\n}\n"


HEADER = "// <auto-generated>\n// Generated by Tools/generate_contracts.py from contracts/v2. Do not edit by hand;\n" \
         "// change the schemas in contracts/v2 and run the generator again.\n// </auto-generated>\n"


"""
# -----------------------------------------------------------------------------------
# Function: alert_cues
# -----------------------------------------------------------------------------------
Description:
    This function writes AlertCues.g.cs from the shared table that maps "entityType/eventType" to
    the cue that plays.

Args:
    path (Path): contracts/v2/alert_cues.json.

Returns:
    str: The file's source.

Raises:
    ValueError: If the table does not map event keys to nonempty cue names.
"""
def alert_cues(path: Path) -> str:
    """The shared "entityType/eventType" -> cue table that decides which events play a sound."""
    cues = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(cues, dict) or not all(isinstance(v, str) and v for v in cues.values()):
        raise ValueError("alert_cues.json must map event keys to nonempty cue names")
    entries = "\n".join(f'            [{json.dumps(k)}] = {json.dumps(v)},' for k, v in sorted(cues.items()))
    return HEADER + "using System.Collections.Generic;\n\n" + f"namespace {NAMESPACE}\n{{\n" + \
        "    public static class AlertCues\n    {\n" + \
        "        // contracts/v2/alert_cues.json. Routine outcomes are absent, so they stay silent.\n" + \
        "        public static readonly IReadOnlyDictionary<string, string> Default = new Dictionary<string, string>\n        {\n" + \
        entries + "\n        };\n    }\n}\n"


"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function generates the three C# files, copies the shared fixtures and alert cues, and
    copies the 3D scene package, keeping Unity's .meta files and their GUIDs.

Raises:
    SystemExit: If there are no schemas under the source folder.
"""
def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, default=ROOT / "contracts" / "v2")
    args = parser.parse_args()
    source = args.source.resolve()
    documents = {path.name.removesuffix(".schema.json"): json.loads(path.read_text(encoding="utf-8"))
                 for path in sorted(source.glob("*.schema.json"))}
    if not documents:
        raise SystemExit(f"No *.schema.json files under {source}")
    generator = Generator(documents)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    (OUTPUT / "ContractModels.g.cs").write_text(generator.models(), encoding="utf-8", newline="\n")
    (OUTPUT / "ContractSchemas.g.cs").write_text(generator.schemas(), encoding="utf-8", newline="\n")
    (OUTPUT / "AlertCues.g.cs").write_text(alert_cues(source / "alert_cues.json"), encoding="utf-8", newline="\n")
    FIXTURES.mkdir(parents=True, exist_ok=True)
    for fixture in sorted((source / "fixtures").glob("*.json")):
        shutil.copyfile(fixture, FIXTURES / fixture.name)
    shutil.copyfile(source / "alert_cues.json", FIXTURES / "alert_cues.json")
    # The shared 3D scene package, so world tests load the real manifest and GLB assets.
    scene = source.parents[1] / "backend" / "fixtures" / "m2_scene"
    if scene.is_dir():
        target = FIXTURES / "m2_scene"
        # Preserve Unity .meta files and their GUIDs when updating the scene fixtures.
        shutil.copytree(scene, target, dirs_exist_ok=True, ignore=shutil.ignore_patterns("README.md"))
    print(f"{len(generator.classes)} classes from {len(documents)} schemas; fixtures copied to {FIXTURES.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
