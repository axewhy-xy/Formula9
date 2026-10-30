"""Bounded, CPU-only importer for formula9-gltf2-r1 (not general glTF).

No URI, extension, skin, animation, sparse accessor or implicit decoder is
allowed. The shared profile deliberately accepts only baked, indexed geometry.
"""

from __future__ import annotations

import io
import json
import math
import struct
from dataclasses import dataclass

from contracts.v2.spatial import (MAX_ASSET_BYTES, MAX_DECODED_BYTES, MAX_INDICES,
                                  MAX_TEXTURE_DIMENSION, MAX_VERTICES, rotate, unit_quaternion)


class AssetValidationError(ValueError):
    pass


def require(condition: bool, code: str) -> None:
    if not condition:
        raise AssetValidationError(code)


def integer(v, low=0, high=MAX_DECODED_BYTES):
    require(type(v) is int and low <= v <= high, "INVALID_INTEGER_OR_LIMIT")
    return v


def vector(v, count):
    require(isinstance(v, list) and len(v) == count and all(type(x) in (int, float) and math.isfinite(x) for x in v), "NONFINITE_OR_INVALID_VECTOR")
    return v


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "DUPLICATE_JSON_KEY")
        result[key] = value
    return result


def decode_json(data: bytes):
    return json.loads(data, object_pairs_hook=unique_object,
                      parse_constant=lambda _: (_ for _ in ()).throw(AssetValidationError("NONFINITE_JSON")))


@dataclass(frozen=True)
class GeometryInventory:
    decoded_bytes: int
    vertices: int
    indices: int
    bounds_min_m: tuple[float, float, float]
    bounds_max_m: tuple[float, float, float]


def validate_glb(data: bytes) -> GeometryInventory:
    try:
        return _validate_glb(data)
    except AssetValidationError:
        raise
    except (ValueError, TypeError, KeyError, IndexError, struct.error, RecursionError, OverflowError, AttributeError) as exc:
        raise AssetValidationError("MALFORMED_GLB") from exc


def _validate_glb(data: bytes) -> GeometryInventory:
    require(28 <= len(data) <= MAX_ASSET_BYTES, "ASSET_BYTE_LIMIT")
    magic, version, length = struct.unpack_from("<III", data)
    require(magic == 0x46546C67 and version == 2 and length == len(data), "INVALID_GLB_HEADER")
    chunks, offset = [], 12
    while offset < length:
        require(len(chunks) < 2 and offset + 8 <= length, "INVALID_GLB_CHUNKS")
        size, kind = struct.unpack_from("<II", data, offset)
        offset += 8
        require(size % 4 == 0 and offset + size <= length, "INVALID_GLB_CHUNK_SIZE")
        chunks.append((kind, data[offset:offset+size]))
        offset += size
    require(len(chunks) == 2 and [c[0] for c in chunks] == [0x4E4F534A, 0x004E4942], "INVALID_GLB_CHUNKS")
    doc, binary = decode_json(chunks[0][1]), chunks[1][1]
    require(isinstance(doc, dict) and doc.get("asset", {}).get("version") == "2.0", "UNSUPPORTED_GLTF_VERSION")

    # Reject extensions/URIs at every depth, including unused objects.
    stack, visited = [doc], 0
    while stack:
        item = stack.pop()
        visited += 1
        require(visited <= 100000, "JSON_OBJECT_LIMIT")
        if isinstance(item, dict):
            require(not ({"uri", "extensions", "extensionsUsed", "extensionsRequired", "skins", "animations", "cameras", "sparse", "targets", "weights", "skin"} & item.keys()), "UNSUPPORTED_FEATURE_OR_URI")
            stack.extend(item.values())
        elif isinstance(item, list):
            stack.extend(item)
        elif isinstance(item, float):
            require(math.isfinite(item), "NONFINITE_JSON")

    buffers = doc["buffers"]
    require(len(buffers) == 1, "EXTERNAL_BUFFER")
    declared_binary = integer(buffers[0]["byteLength"], 1)
    require(0 <= len(binary)-declared_binary <= 3, "BUFFER_LENGTH_MISMATCH")
    views = doc["bufferViews"]
    require(1 <= len(views) <= 256, "BUFFER_VIEW_LIMIT")
    for view in views:
        require(view.get("buffer") == 0, "EXTERNAL_BUFFER")
        start, size = integer(view.get("byteOffset", 0)), integer(view["byteLength"], 1)
        require(start+size <= declared_binary, "BUFFER_VIEW_BOUNDS")
        if "byteStride" in view:
            require(4 <= integer(view["byteStride"]) <= 252 and view["byteStride"] % 4 == 0, "INVALID_STRIDE")

    accessors = doc["accessors"]
    require(1 <= len(accessors) <= 256, "ACCESSOR_LIMIT")
    decoded, arrays = 0, []
    for a in accessors:
        require(not a.get("normalized", False), "NORMALIZED_ACCESSOR_UNSUPPORTED")
        require(a["componentType"] in {5123, 5125, 5126} and a["type"] in {"SCALAR", "VEC2", "VEC3"}, "UNSUPPORTED_ACCESSOR")
        n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3}[a["type"]]
        size, fmt = {5123: (2, "H"), 5125: (4, "I"), 5126: (4, "f")}[a["componentType"]]
        count = integer(a["count"], 1, MAX_INDICES if n == 1 else MAX_VERTICES)
        view = views[integer(a["bufferView"], 0, len(views)-1)]
        if a["type"] == "SCALAR" and a["componentType"] in {5123, 5125}:
            require("byteStride" not in view, "STRIDED_INDICES_UNSUPPORTED")
        stride = view.get("byteStride", size*n)
        start = integer(a.get("byteOffset", 0))
        require(start % size == 0 and view.get("byteOffset", 0) % size == 0 and stride >= size*n and stride % size == 0
                and start+(count-1)*stride+size*n <= view["byteLength"], "ACCESSOR_BOUNDS")
        decoded += count*size*n
        require(decoded <= MAX_DECODED_BYTES, "DECODED_BYTE_LIMIT")
        values = [struct.unpack_from("<"+fmt*n, binary, view.get("byteOffset", 0)+start+i*stride) for i in range(count)]
        require(all(math.isfinite(v) for row in values for v in row), "NONFINITE_GEOMETRY")
        arrays.append(values)

    for image in doc.get("images", []):
        require(image.get("mimeType") in {"image/png", "image/jpeg"}, "UNSUPPORTED_TEXTURE")
        view = views[integer(image["bufferView"], 0, len(views)-1)]
        require("byteStride" not in view, "INVALID_IMAGE_VIEW")
        start = view.get("byteOffset", 0)
        # Pillow is a locked backend dependency. Read
        # dimensions before decoding to bound decompression, then verify pixels.
        from PIL import Image
        try:
            with Image.open(io.BytesIO(binary[start:start+view["byteLength"]])) as im:
                require(im.format == ("PNG" if image["mimeType"] == "image/png" else "JPEG"), "TEXTURE_FORMAT_MISMATCH")
                require(im.mode in {"RGB", "RGBA", "L", "LA", "P"}, "UNSUPPORTED_TEXTURE_CHANNELS")
                require(0 < im.width <= MAX_TEXTURE_DIMENSION and 0 < im.height <= MAX_TEXTURE_DIMENSION, "TEXTURE_DIMENSION_LIMIT")
                decoded += im.width*im.height*4
                require(decoded <= MAX_DECODED_BYTES, "DECODED_BYTE_LIMIT")
                im.load()
        except (OSError, Image.DecompressionBombError) as exc:
            raise AssetValidationError("INVALID_TEXTURE") from exc
    for texture in doc.get("textures", []):
        integer(texture["source"], 0, len(doc.get("images", []))-1)
        if "sampler" in texture:
            integer(texture["sampler"], 0, len(doc.get("samplers", []))-1)
    for sampler in doc.get("samplers", []):
        require(sampler.get("magFilter", 9729) in {9728, 9729} and sampler.get("minFilter", 9987) in {9728, 9729, 9984, 9985, 9986, 9987}
                and all(sampler.get(k, 10497) in {33071, 33648, 10497} for k in ("wrapS", "wrapT")), "UNSUPPORTED_SAMPLER")
    for material in doc.get("materials", []):
        require(set(material) <= {"name", "pbrMetallicRoughness", "doubleSided", "alphaMode", "extras"}
                and material.get("alphaMode", "OPAQUE") == "OPAQUE", "UNSUPPORTED_MATERIAL")
        pbr = material.get("pbrMetallicRoughness", {})
        require(set(pbr) <= {"baseColorFactor", "metallicFactor", "roughnessFactor", "baseColorTexture", "metallicRoughnessTexture"}, "UNSUPPORTED_MATERIAL")
        factors = vector(pbr.get("baseColorFactor", [1, 1, 1, 1]), 4) + [pbr.get("metallicFactor", 1), pbr.get("roughnessFactor", 1)]
        require(all(type(v) in (float, int) and math.isfinite(v) and 0 <= v <= 1 for v in factors), "INVALID_MATERIAL_FACTOR")
        for k in ("baseColorTexture", "metallicRoughnessTexture"):
            if k in pbr:
                require(pbr[k].get("texCoord", 0) == 0, "UNSUPPORTED_TEXCOORD")
                integer(pbr[k]["index"], 0, len(doc.get("textures", []))-1)

    meshes = doc["meshes"]
    require(1 <= len(meshes) <= 64, "MESH_LIMIT")
    positions, vertices, indices = [], 0, 0
    used_accessors = set()
    for mesh in meshes:
        require(1 <= len(mesh["primitives"]) <= 64, "PRIMITIVE_LIMIT")
        mesh_positions = []
        for p in mesh["primitives"]:
            require(p.get("mode", 4) == 4 and set(p["attributes"]) == {"POSITION", "NORMAL", "TEXCOORD_0"}, "UNSUPPORTED_PRIMITIVE")
            attrs = p["attributes"]
            for name, kind in (("POSITION", "VEC3"), ("NORMAL", "VEC3"), ("TEXCOORD_0", "VEC2")):
                i = integer(attrs[name], 0, len(accessors)-1)
                require(accessors[i]["type"] == kind and accessors[i]["componentType"] == 5126, "INVALID_VERTEX_ATTRIBUTE")
                used_accessors.add(i)
            pos, norm, uv = (arrays[attrs[k]] for k in ("POSITION", "NORMAL", "TEXCOORD_0"))
            require(len(pos) == len(norm) == len(uv), "ATTRIBUTE_COUNT_MISMATCH")
            require(all(abs(sum(v*v for v in n)-1) <= 1e-4 for n in norm), "INVALID_NORMAL")
            idx = integer(p["indices"], 0, len(accessors)-1)
            used_accessors.add(idx)
            require(accessors[idx]["type"] == "SCALAR" and accessors[idx]["componentType"] in {5123, 5125}
                    and len(arrays[idx]) % 3 == 0 and all(i[0] < len(pos) for i in arrays[idx]), "INDEX_BOUNDS")
            for t in range(0, len(arrays[idx]), 3):
                a, b, c = [pos[arrays[idx][t+i][0]] for i in range(3)]
                u, v = [b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)]
                cross = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]
                require(sum(x*x for x in cross) > 1e-16, "DEGENERATE_TRIANGLE")
            integer(p["material"], 0, len(doc.get("materials", []))-1)
            a = accessors[attrs["POSITION"]]
            lo, hi = [min(v[c] for v in pos) for c in range(3)], [max(v[c] for v in pos) for c in range(3)]
            require(vector(a["min"], 3) == lo and vector(a["max"], 3) == hi, "POSITION_BOUNDS_MISMATCH")
            mesh_positions.extend(pos)
            vertices += len(pos)
            indices += len(arrays[idx])
            require(vertices <= MAX_VERTICES and indices <= MAX_INDICES, "GEOMETRY_COUNT_LIMIT")
        positions.append(mesh_positions)
    require(len(used_accessors) == len(accessors), "UNUSED_ACCESSOR")

    nodes = doc["nodes"]
    require(1 <= len(nodes) <= 128 and len(doc["scenes"]) == 1 and doc.get("scene", 0) == 0, "SCENE_NODE_LIMIT")
    seen, used_meshes, world_positions = set(), set(), []

    def visit(index, parents):
        integer(index, 0, len(nodes)-1)
        require(index not in seen and len(parents) < 32, "NODE_CYCLE_OR_DUPLICATE")
        seen.add(index)
        node = nodes[index]
        require(not ("matrix" in node and any(k in node for k in ("translation", "rotation", "scale"))), "MIXED_NODE_TRANSFORMS")
        # All authoring transforms are baked by this profile. Nested rigid TRS
        # remains supported for fixtures/imports; scale/matrix is rejected with
        # a named diagnostic instead of interpreting unsupported shear.
        require("matrix" not in node and node.get("scale", [1, 1, 1]) == [1, 1, 1], "BAKE_NODE_SCALE_OR_MATRIX")
        translation = vector(node.get("translation", [0, 0, 0]), 3)
        rotation = unit_quaternion(vector(node.get("rotation", [0, 0, 0, 1]), 4))
        transforms = [(translation, rotation)] + parents
        if "mesh" in node:
            m = integer(node["mesh"], 0, len(meshes)-1)
            used_meshes.add(m)
            for p in positions[m]:
                p = list(p)
                for t, q in transforms:
                    p = [a+b for a, b in zip(rotate(q, p), t)]
                    require(all(math.isfinite(v) for v in p), "NONFINITE_TRANSFORMED_GEOMETRY")
                world_positions.append(p)
                require(len(world_positions) <= MAX_VERTICES, "INSTANCED_VERTEX_LIMIT")
        children = node.get("children", [])
        require(isinstance(children, list) and len(children) <= 128, "NODE_CHILD_LIMIT")
        for child in children:
            visit(child, transforms)
    for root in doc["scenes"][0]["nodes"]:
        visit(root, [])
    require(len(seen) == len(nodes) and len(used_meshes) == len(meshes) and world_positions, "UNBOUND_GEOMETRY")
    # Count expanded node instances in decoded/upload inventory as well.
    decoded += len(world_positions)*32
    require(decoded <= MAX_DECODED_BYTES, "DECODED_BYTE_LIMIT")
    return GeometryInventory(decoded, len(world_positions), indices,
                             tuple(min(p[c] for p in world_positions) for c in range(3)),
                             tuple(max(p[c] for p in world_positions) for c in range(3)))
