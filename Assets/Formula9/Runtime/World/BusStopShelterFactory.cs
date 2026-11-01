using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    /// <summary>
    /// Generates static, low-poly bus shelters as batched Unity meshes. There are
    /// no downloaded prefab/FBX dependencies. Stops are grouped into spatial
    /// chunks so only nearby geometry needs to be drawn by the 3D camera.
    /// </summary>
    public sealed class BusStopShelterFactory : IDisposable
    {
        public struct Placement
        {
            public string StopId;
            public Vector3 Position;
            public Quaternion Rotation;

            public Placement(string stopId, Vector3 position, Quaternion rotation)
            {
                StopId = stopId;
                Position = position;
                Rotation = rotation;
            }
        }

        private const float ChunkSizeM = 500f;
        // Scale the complete shelter consistently around its ground-level origin.
        // Increase to 1.75f for extra-large shelters or lower to 1.25f if they overlap roads.
        public const float ShelterScale = 1.5f;
        private const int RoofMaterial = 0, FrameMaterial = 1, GlassMaterial = 2;
        private const int SeatMaterial = 3, SignMaterial = 4, PavementMaterial = 5, TactileMaterial = 6;
        private const int MaterialCount = 7;
        // Number of tile repetitions per metre on ordinary shelter faces.
        private static readonly float[] TextureRepeatPerMetre = { 0.9f, 1.6f, 0.35f, 1.8f, 1f, 1.1f, 2.5f };
        private readonly Material[] materials = new Material[MaterialCount];
        private readonly List<Mesh> meshes = new List<Mesh>();
        private GameObject activeRoot;
        private bool disposed;

        public BusStopShelterFactory()
        {
            // Built-in render pipeline: our custom PBR surface shader.
            // URP: Unity's physically based Lit shader with the same albedo, normal,
            // metallic and smoothness maps. ShaderLab surface shaders are not URP compatible.
            var useUrp = GraphicsSettings.currentRenderPipeline != null;
            var opaqueShader = useUrp
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Resources.Load<Shader>("Formula9/Shaders/BusStopSurface");
            if (opaqueShader == null)
                opaqueShader = Shader.Find(useUrp ? "Universal Render Pipeline/Lit" : "Standard");
            if (opaqueShader == null)
                throw new InvalidOperationException("No compatible opaque bus-stop shader was found.");

            // Transparent, lightly reflective glass. Same ShaderLab shader is usable
            // as a custom unlit transparent pass in Built-in and URP.
            var glassShader = useUrp
                ? Resources.Load<Shader>("Formula9/Shaders/BusStopGlassURP")
                    ?? Shader.Find("Formula9/BusStopGlassURP")
                    ?? opaqueShader
                : Resources.Load<Shader>("Formula9/Shaders/BusStopGlass")
                    ?? Shader.Find("Formula9/BusStopGlass")
                    ?? opaqueShader;

            materials[RoofMaterial] = MakeSurface(opaqueShader, "roof_metal", "roof_metal_normal",
                new Color(0.96f, 0.93f, 0.89f), 0.30f, 0.38f, 0.45f);
            materials[FrameMaterial] = MakeSurface(opaqueShader, "brushed_metal", "brushed_metal_normal",
                new Color(0.17f, 0.19f, 0.20f), 0.72f, 0.37f, 0.42f);
            materials[GlassMaterial] = MakeGlass(glassShader, "blue_glass");
            materials[SeatMaterial] = MakeSurface(opaqueShader, "brushed_metal", "brushed_metal_normal",
                new Color(0.95f, 0.90f, 0.87f), 0.28f, 0.45f, 0.30f);
            materials[SignMaterial] = MakeSurface(opaqueShader, "bus_stop_sign", null,
                Color.white, 0.03f, 0.65f, 0f);
            materials[PavementMaterial] = MakeSurface(opaqueShader, "paving", "paving_normal",
                new Color(0.77f, 0.78f, 0.76f), 0f, 0.13f, 0.72f);
            materials[TactileMaterial] = MakeSurface(opaqueShader, "tactile_yellow", "tactile_yellow_normal",
                Color.white, 0f, 0.29f, 0.75f);
        }

        private static Texture2D LoadTexture(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var texture = Resources.Load<Texture2D>("Formula9/Textures/" + file);
            if (texture == null)
                Debug.LogWarning("[BusStop] Missing texture Formula9/Textures/" + file);
            return texture;
        }

        private static void SetBaseColour(Material material, Color colour)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", colour);
        }

        private static Material MakeSurface(Shader shader, string diffuse, string normal,
            Color tint, float metallic, float smoothness, float bumpScale)
        {
            var material = new Material(shader) { name = "BusStop/" + diffuse };
            SetBaseColour(material, tint);
            var albedo = LoadTexture(diffuse);
            if (albedo != null)
            {
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", albedo);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", albedo);
            }
            var normalMap = LoadTexture(normal);
            if (normalMap != null && material.HasProperty("_BumpMap"))
            {
                material.SetTexture("_BumpMap", normalMap);
                if (material.HasProperty("_BumpScale")) material.SetFloat("_BumpScale", bumpScale);
                material.EnableKeyword("_NORMALMAP");
            }
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_MicroGrain")) material.SetFloat("_MicroGrain", 0.13f);
            return material;
        }

        private static Material MakeGlass(Shader shader, string textureFile)
        {
            var material = new Material(shader) { name = "BusStop/clear-glass" };
            var texture = LoadTexture(textureFile);
            if (texture != null)
            {
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            }
            var tint = new Color(0.78f, 0.86f, 0.88f, 0.38f);
            if (material.HasProperty("_Tint")) material.SetColor("_Tint", tint);
            SetBaseColour(material, tint);
            if (material.HasProperty("_Opacity")) material.SetFloat("_Opacity", 0.17f);
            if (material.HasProperty("_EdgeReflect")) material.SetFloat("_EdgeReflect", 0.48f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.90f);

            // Fallback transparent setup when the custom glass shader is missing.
            // Built-in Standard and URP Lit configure transparency differently.
            if (!material.HasProperty("_Opacity"))
            {
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
                if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
                if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
            }
            return material;
        }

        /// <summary>
        /// Creates one parent and a small set of multi-material meshes, rather
        /// than 15+ GameObjects for each LTA bus stop. Replaces any older batch.
        /// </summary>
        public void Build(Transform cityRoot, IReadOnlyList<Placement> locations)
        {
            if (disposed) return;
            Clear();
            activeRoot = new GameObject("LTA Bus Stop Shelters (3D)");
            activeRoot.transform.SetParent(cityRoot, false);
            if (locations == null || locations.Count == 0) return;

            var chunks = locations.GroupBy(p => (
                X: Mathf.FloorToInt(p.Position.x / ChunkSizeM),
                Z: Mathf.FloorToInt(p.Position.z / ChunkSizeM)));
            foreach (var chunk in chunks)
            {
                var chunkOrigin = new Vector3(chunk.Key.X * ChunkSizeM, 0, chunk.Key.Z * ChunkSizeM);
                var verts = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var triangles = new List<int>[MaterialCount];
                for (var i = 0; i < triangles.Length; i++) triangles[i] = new List<int>();
                foreach (var location in chunk)
                    AddShelter(location, chunkOrigin, verts, normals, uvs, triangles);

                var mesh = new Mesh
                {
                    name = $"LTA bus shelters {chunk.Key.X},{chunk.Key.Z}",
                    indexFormat = IndexFormat.UInt32
                };
                mesh.SetVertices(verts);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.subMeshCount = MaterialCount;
                for (var i = 0; i < MaterialCount; i++) mesh.SetTriangles(triangles[i], i, false);
                // Unity normal-mapped shaders require vertex tangents in addition to UV0.
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                meshes.Add(mesh);

                var holder = new GameObject($"Shelter chunk {chunk.Key.X},{chunk.Key.Z} ({chunk.Count()} stops)",
                    typeof(MeshFilter), typeof(MeshRenderer));
                holder.transform.SetParent(activeRoot.transform, false);
                holder.transform.localPosition = chunkOrigin;
                holder.GetComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = holder.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private static void AddShelter(Placement placement, Vector3 chunkOrigin,
            List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int>[] triangles)
        {
            var at = placement.Position - chunkOrigin;
            var rotation = placement.Rotation;

            void Box(string name, Vector3 center, Vector3 size, int material)
            {
                // Name is only descriptive; generated components are stored in
                // the chunk's combined mesh, not separate GameObjects.
                AddBox(vertices, normals, uvs, triangles[material], at, rotation,
                    center * ShelterScale, size * ShelterScale,
                    TextureRepeatPerMetre[material], material == SignMaterial);
            }

            // Shape loosely follows modern Singapore bus stops:
            // charcoal steel framing, long canopy with ribs, clear glazed back,
            // metal bench, a pale paved pad and yellow tactile strip.
            // All details are static and combined into chunk meshes.
            // The front faces local -Z (towards the road).
            Box("Concrete pad", new Vector3(0, 0.07f, 0),
                new Vector3(6.5f, 0.14f, 2.6f), PavementMaterial);
            Box("Yellow tactile paving", new Vector3(0, 0.15f, -1.14f),
                new Vector3(5.7f, 0.025f, 0.25f), TactileMaterial);
            Box("Canopy roof", new Vector3(0, 3.05f, 0),
                new Vector3(5.8f, 0.18f, 2.35f), RoofMaterial);
            Box("Canopy fascia", new Vector3(0, 2.88f, -1.16f),
                new Vector3(5.85f, 0.18f, 0.11f), FrameMaterial);
            Box("Rear fascia", new Vector3(0, 2.88f, 1.16f),
                new Vector3(5.85f, 0.15f, 0.11f), FrameMaterial);

            foreach (var x in new[] { -2.6f, 2.6f })
            {
                Box("Front column", new Vector3(x, 1.48f, -0.99f),
                    new Vector3(0.13f, 2.93f, 0.13f), FrameMaterial);
                Box("Back column", new Vector3(x, 1.48f, 0.98f),
                    new Vector3(0.13f, 2.93f, 0.13f), FrameMaterial);
            }
            // Recessed rafters under the roof cast a subtle architectural silhouette.
            foreach (var x in new[] { -2.2f, -1.1f, 0f, 1.1f, 2.2f })
                Box("Roof rib", new Vector3(x, 2.88f, -0.03f),
                    new Vector3(0.07f, 0.12f, 2.22f), FrameMaterial);

            // A row of separate glass panels and dark mullions, not one cyan wall.
            foreach (var x in new[] { -1.91f, -0.64f, 0.64f, 1.91f })
                Box("Clear rear glass", new Vector3(x, 1.72f, 0.99f),
                    new Vector3(1.20f, 1.96f, 0.027f), GlassMaterial);
            foreach (var x in new[] { -2.55f, -1.27f, 0f, 1.27f, 2.55f })
                Box("Rear mullion", new Vector3(x, 1.73f, 1.012f),
                    new Vector3(0.065f, 2.03f, 0.07f), FrameMaterial);
            Box("Rear window rail", new Vector3(0, 1.12f, 1.04f),
                new Vector3(5.2f, 0.065f, 0.085f), FrameMaterial);

            Box("Metal seat", new Vector3(0.15f, 0.66f, 0.35f),
                new Vector3(3.55f, 0.13f, 0.47f), SeatMaterial);
            Box("Bench back", new Vector3(0.15f, 1.08f, 0.63f),
                new Vector3(3.55f, 0.62f, 0.10f), SeatMaterial);
            foreach (var x in new[] { -1.17f, 1.47f })
                Box("Bench leg", new Vector3(x, 0.35f, 0.35f),
                    new Vector3(0.12f, 0.59f, 0.13f), FrameMaterial);

            Box("Stop sign post", new Vector3(-3.0f, 1.49f, -0.42f),
                new Vector3(0.11f, 2.96f, 0.11f), FrameMaterial);
            Box("Stop sign", new Vector3(-3.0f, 2.71f, -0.42f),
                new Vector3(0.72f, 0.43f, 0.10f), SignMaterial);

            // Short anti-vehicle bollards: dark steel with yellow reflective strips.
            foreach (var x in new[] { -2.15f, -0.72f, 0.72f, 2.15f })
            {
                Box("Bollard", new Vector3(x, 0.46f, -1.23f),
                    new Vector3(0.16f, 0.83f, 0.16f), FrameMaterial);
                Box("Bollard reflective band", new Vector3(x, 0.62f, -1.23f),
                    new Vector3(0.17f, 0.16f, 0.17f), TactileMaterial);
            }
        }

        /// <summary>
        /// Append a six-faced box with outward normals, winding, and tiled UV0s.
        /// Sign faces use one atlas-sized UV square so the bus icon remains legible.
        /// </summary>
        private static void AddBox(List<Vector3> vertices, List<Vector3> normals,
            List<Vector2> uvs, List<int> triangles,
            Vector3 origin, Quaternion rotation, Vector3 center, Vector3 size,
            float repeatsPerMetre, bool isSign)
        {
            void Face(Vector3 faceCenter, Vector3 u, Vector3 v, bool front = false, bool back = false)
            {
                var index = vertices.Count;
                var n = rotation * Vector3.Cross(u, v).normalized;
                var halfU = u * 0.5f;
                var halfV = v * 0.5f;
                vertices.Add(origin + rotation * (center + faceCenter - halfU - halfV));
                vertices.Add(origin + rotation * (center + faceCenter + halfU - halfV));
                vertices.Add(origin + rotation * (center + faceCenter + halfU + halfV));
                vertices.Add(origin + rotation * (center + faceCenter - halfU + halfV));
                for (var i = 0; i < 4; i++) normals.Add(n);

                if (isSign && front)
                {
                    // -Z face uses u=up, v=right. Swap UV to make the sign upright.
                    uvs.Add(new Vector2(0, 0));
                    uvs.Add(new Vector2(0, 1));
                    uvs.Add(new Vector2(1, 1));
                    uvs.Add(new Vector2(1, 0));
                }
                else if (isSign && back)
                {
                    uvs.Add(new Vector2(0, 0));
                    uvs.Add(new Vector2(1, 0));
                    uvs.Add(new Vector2(1, 1));
                    uvs.Add(new Vector2(0, 1));
                }
                else
                {
                    var uLength = isSign ? 1f : u.magnitude * repeatsPerMetre;
                    var vLength = isSign ? 1f : v.magnitude * repeatsPerMetre;
                    uvs.Add(new Vector2(0, 0));
                    uvs.Add(new Vector2(uLength, 0));
                    uvs.Add(new Vector2(uLength, vLength));
                    uvs.Add(new Vector2(0, vLength));
                }
                // Clockwise in Unity for this face layout; outward normals agree.
                triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
                triangles.Add(index); triangles.Add(index + 2); triangles.Add(index + 3);
            }

            Face(Vector3.right * (size.x / 2), Vector3.up * size.y, Vector3.forward * size.z);
            Face(Vector3.left * (size.x / 2), Vector3.forward * size.z, Vector3.up * size.y);
            Face(Vector3.up * (size.y / 2), Vector3.forward * size.z, Vector3.right * size.x);
            Face(Vector3.down * (size.y / 2), Vector3.right * size.x, Vector3.forward * size.z);
            Face(Vector3.forward * (size.z / 2), Vector3.right * size.x, Vector3.up * size.y,
                back: isSign);
            Face(Vector3.back * (size.z / 2), Vector3.up * size.y, Vector3.right * size.x,
                front: isSign);
        }

        private void Clear()
        {
            if (activeRoot != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(activeRoot);
                else UnityEngine.Object.DestroyImmediate(activeRoot);
                activeRoot = null;
            }
            foreach (var mesh in meshes)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                else UnityEngine.Object.DestroyImmediate(mesh);
            }
            meshes.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Clear();
            foreach (var material in materials)
            {
                if (material == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(material);
                else UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
