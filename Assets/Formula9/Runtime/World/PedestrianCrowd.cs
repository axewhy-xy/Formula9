using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Formula9.World
{
    // Where one person is and how they stand, for PedestrianCrowd to draw
    public struct PedestrianPose
    {
        // The feet, in the world
        public Vector3 Position;
        public Quaternion Rotation;
        public float ScaleM;
        // Which character, taken round the crowd's ModelCount
        public int Model;
        // Walk cycles completed so far, and whether they moved this tick (otherwise they stand)
        public float WalkPhase;
        public bool Moving;
    }

    // Draws a crowd of people, wherever something else (PedestrianLocalSim, or the backend's frames) says they are.
    // It does no simulation.
    //
    // Drawing stays cheap: each character model is baked once into a few static mesh poses (a walk cycle
    // and an idle pose), and every person is then one instance of a pose, drawn in batches. There are no
    // GameObjects, Animators or skinning per person. Without character models, tall and short boxes are drawn.
    public sealed class PedestrianCrowd : IDisposable
    {
        // Off: tall and short blue boxes. On: the character models in Assets/Animated Men Characters.
        private static readonly bool UseCharacterModels = false;
        private const string CatalogPath = "Assets/Formula9/Pedestrians/PedestrianModelCatalog.asset";
        // Poses baked per walk cycle; a person travels PedestrianLocalSim.StrideM in one
        private const int WalkFrames = 8;
        // Turn a model that comes out facing the wrong way (the characters are expected to face +Z)
        private const float ModelYawDeg = 0f;
        private const int BatchSize = 1023;
        private const float CullMarginM = 3f;

        private readonly List<Model> models = new List<Model>();
        private readonly List<UnityEngine.Object> ownedObjects = new List<UnityEngine.Object>();
        private readonly List<Plane[]> frustums = new List<Plane[]>();
        private readonly Matrix4x4[] batch = new Matrix4x4[BatchSize];
        // The model's own materials, as copies of the instancing template so a crowd draws in a few batches
        private readonly Dictionary<Material, Material> instancedMaterials = new Dictionary<Material, Material>();
        private int frustumCount;
        private bool modelsLoaded;
        private bool drawFailed;

        // One character: static poses, each made of one mesh per skinned part of the model
        private sealed class Model
        {
            public readonly List<Part> Parts = new List<Part>();
            public int WalkSlots = 1;
            public int IdleSlot = -1;
            public List<Matrix4x4>[] Matrices;
        }

        private sealed class Part
        {
            public Mesh[] Poses;
            public Material[] Materials;
        }

        // How many characters there are to choose from; at least one once the models are loaded
        public int ModelCount => models.Count;

        // Bakes the characters, once. Drawing does it too, so this is only needed to know ModelCount first.
        public void EnsureModels() => LoadModels();

        // Called once a frame, after the cameras are placed: draws everyone where the poses say, for the
        // cameras whose turn it is (all of them when none are given).
        public void Draw(IReadOnlyList<PedestrianPose> poses, IReadOnlyList<Camera> cameras = null)
        {
            if (drawFailed || poses == null || poses.Count == 0) return;
            LoadModels();
            if (models.Count == 0) return;
            try
            {
                DrawAll(poses, cameras);
            }
            catch (Exception error)
            {
                // Never let the crowd take the 3D view down with it
                drawFailed = true;
                Debug.LogWarning("[Pedestrians] Drawing stopped: " + error);
            }
        }

        public void Dispose()
        {
            models.Clear();
            modelsLoaded = false;
            for (var i = 0; i < ownedObjects.Count; i++) DestroyObject(ownedObjects[i]);
            ownedObjects.Clear();
            // Copies of the template that were just destroyed
            instancedMaterials.Clear();
        }

        private void DrawAll(IReadOnlyList<PedestrianPose> poses, IReadOnlyList<Camera> cameras)
        {
            SetFrustums(cameras);
            for (var m = 0; m < models.Count; m++)
                foreach (var list in models[m].Matrices) list.Clear();

            for (var i = 0; i < poses.Count; i++)
            {
                var pose = poses[i];
                if (cameras != null && !InView(pose.Position)) continue;
                var model = models[((pose.Model % models.Count) + models.Count) % models.Count];
                var slot = model.IdleSlot;
                if (pose.Moving || slot < 0)
                {
                    var cycle = pose.WalkPhase - Mathf.Floor(pose.WalkPhase);
                    slot = Mathf.Min(model.WalkSlots - 1, (int)(cycle * model.WalkSlots));
                }
                model.Matrices[slot].Add(Matrix4x4.TRS(pose.Position, pose.Rotation, Vector3.one * pose.ScaleM));
            }

            for (var m = 0; m < models.Count; m++)
            {
                var model = models[m];
                for (var slot = 0; slot < model.Matrices.Length; slot++)
                {
                    var matrices = model.Matrices[slot];
                    if (matrices.Count == 0) continue;
                    foreach (var part in model.Parts) DrawPose(part, slot, matrices);
                }
            }
        }

        private void DrawPose(Part part, int slot, List<Matrix4x4> matrices)
        {
            var mesh = part.Poses[slot];
            if (mesh == null) return;
            var subMeshes = Mathf.Min(mesh.subMeshCount, part.Materials.Length);
            for (var sub = 0; sub < subMeshes; sub++)
            {
                var material = part.Materials[sub];
                if (material == null) continue;
                if (!SystemInfo.supportsInstancing)
                {
                    for (var i = 0; i < matrices.Count; i++)
                        Graphics.DrawMesh(mesh, matrices[i], material, 0, null, sub, null, ShadowCastingMode.Off, false);
                    continue;
                }
                for (var start = 0; start < matrices.Count; start += BatchSize)
                {
                    var count = Mathf.Min(BatchSize, matrices.Count - start);
                    matrices.CopyTo(start, batch, 0, count);
                    Graphics.DrawMeshInstanced(mesh, sub, material, batch, count, null,
                        ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);
                }
            }
        }

        private void SetFrustums(IReadOnlyList<Camera> cameras)
        {
            frustumCount = 0;
            if (cameras == null) return;
            for (var i = 0; i < cameras.Count; i++)
            {
                var camera = cameras[i];
                if (camera == null || !camera.enabled) continue;
                if (frustumCount == frustums.Count) frustums.Add(new Plane[6]);
                GeometryUtility.CalculateFrustumPlanes(camera, frustums[frustumCount]);
                frustumCount++;
            }
        }

        // Whether any camera that draws this frame can see the point; false when none draws
        private bool InView(Vector3 position)
        {
            for (var f = 0; f < frustumCount; f++)
            {
                var planes = frustums[f];
                var inside = true;
                for (var p = 0; p < planes.Length; p++)
                {
                    if (planes[p].GetDistanceToPoint(position) < -CullMarginM)
                    {
                        inside = false;
                        break;
                    }
                }
                if (inside) return true;
            }
            return false;
        }

        private static void DestroyObject(UnityEngine.Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(item);
            else UnityEngine.Object.DestroyImmediate(item);
        }

        // ---------------------------------------------------------------------------------------------
        // Character models: baked once into static poses

        private void LoadModels()
        {
            if (modelsLoaded) return;
            modelsLoaded = true;
            var started = Time.realtimeSinceStartup;

            if (!UseCharacterModels)
            {
                AddModel(BoxModel());
                Debug.Log("[Pedestrians] Drawing blue boxes (UseCharacterModels is off).");
                return;
            }

            // The catalog is kept out of Resources while the models are off, so a build does not ship the
            // characters. To turn them on in a player build: move the catalog into Resources/Formula9, load it
            // here with Resources.Load, and set walkClip on each entry (clips are only found in the editor).
#if UNITY_EDITOR
            var catalog = AssetDatabase.LoadAssetAtPath<PedestrianModelCatalog>(CatalogPath);
            if (catalog != null && catalog.entries != null)
            {
                foreach (var entry in catalog.entries)
                {
                    if (entry == null || entry.prefab == null) continue;
                    var walk = entry.walkClip != null ? entry.walkClip : FindClip(entry.prefab, entry.walkClipName, "walk");
                    var idle = FindClip(entry.prefab, null, "idle");
                    AddModel(BakeModel(entry.prefab, walk, idle));
                }
            }
            if (models.Count == 0)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Animated Men Characters" }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;
                    AddModel(BakeModel(prefab, FindClip(prefab, "Walk", "walk"), FindClip(prefab, null, "idle")));
                }
            }
#endif
            if (models.Count == 0)
            {
                Debug.LogWarning("[Pedestrians] No character models could be baked; drawing boxes instead.");
                AddModel(BoxModel());
            }
            Debug.Log($"[Pedestrians] Models ready: {models.Count} in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
        }

        private void AddModel(Model model)
        {
            if (model == null || model.Parts.Count == 0) return;
            var slots = model.WalkSlots + (model.IdleSlot >= 0 ? 1 : 0);
            model.Matrices = new List<Matrix4x4>[slots];
            for (var i = 0; i < slots; i++) model.Matrices[i] = new List<Matrix4x4>(64);
            models.Add(model);
        }

        // The model's walk (or idle) clip: the named one, else one with the word in its name
        private static AnimationClip FindClip(GameObject prefab, string preferredName, string word)
        {
            if (prefab == null) return null;
            var clips = new List<AnimationClip>();
            var animator = prefab.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.runtimeAnimatorController != null)
                clips.AddRange(animator.runtimeAnimatorController.animationClips);
#if UNITY_EDITOR
            var path = AssetDatabase.GetAssetPath(prefab);
            if (!string.IsNullOrEmpty(path))
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (asset is AnimationClip clip) clips.Add(clip);
#endif
            AnimationClip loose = null;
            foreach (var clip in clips)
            {
                if (clip == null || clip.length < 0.2f || clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(preferredName) && clip.name.Equals(preferredName, StringComparison.OrdinalIgnoreCase)) return clip;
                if (clip.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (clip.isLooping) return clip;
                if (loose == null) loose = clip;
            }
            return loose;
        }

        // Poses the model while it stands at the origin, once per walk frame plus an idle pose, and keeps each
        // as a mesh scaled to a height of 1 with its feet on y = 0 and centred on x/z.
        private Model BakeModel(GameObject prefab, AnimationClip walk, AnimationClip idle)
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = "PedestrianBake";
            try
            {
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                var skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(false);
                if (skinned.Length == 0) return null;

                var model = new Model();
                var walkSlots = walk != null ? WalkFrames : 1;
                var slots = walkSlots + (idle != null ? 1 : 0);
                model.WalkSlots = walkSlots;
                model.IdleSlot = idle != null ? walkSlots : -1;
                foreach (var renderer in skinned)
                {
                    if (renderer.sharedMesh == null) continue;
                    model.Parts.Add(new Part { Poses = new Mesh[slots], Materials = InstancedMaterials(renderer.sharedMaterials) });
                }
                if (model.Parts.Count == 0) return null;

                var bounds = default(Bounds);
                var hasBounds = false;
                for (var slot = 0; slot < slots; slot++)
                {
                    if (slot < walkSlots)
                    {
                        if (walk != null) walk.SampleAnimation(instance, walk.length * slot / walkSlots);
                    }
                    else idle.SampleAnimation(instance, 0f);

                    var part = 0;
                    foreach (var renderer in skinned)
                    {
                        if (renderer.sharedMesh == null) continue;
                        var mesh = new Mesh { name = prefab.name + " pose " + slot };
                        ownedObjects.Add(mesh);
                        renderer.BakeMesh(mesh, true);
                        // Into the model's space (it stands at the origin, unscaled)
                        var rotation = renderer.transform.rotation;
                        var vertices = mesh.vertices;
                        var normals = mesh.normals;
                        var tangents = mesh.tangents;
                        for (var v = 0; v < vertices.Length; v++)
                            vertices[v] = rotation * vertices[v] + renderer.transform.position;
                        for (var n = 0; n < normals.Length; n++) normals[n] = rotation * normals[n];
                        for (var t = 0; t < tangents.Length; t++)
                        {
                            var direction = rotation * new Vector3(tangents[t].x, tangents[t].y, tangents[t].z);
                            tangents[t] = new Vector4(direction.x, direction.y, direction.z, tangents[t].w);
                        }
                        mesh.vertices = vertices;
                        if (normals.Length == vertices.Length) mesh.normals = normals;
                        if (tangents.Length == vertices.Length) mesh.tangents = tangents;
                        mesh.RecalculateBounds();
                        if (!hasBounds) { bounds = mesh.bounds; hasBounds = true; }
                        else bounds.Encapsulate(mesh.bounds);
                        model.Parts[part].Poses[slot] = mesh;
                        part++;
                    }
                }

                if (!hasBounds || bounds.size.y < 0.01f || !float.IsFinite(bounds.size.y)) return null;
                var scale = 1f / bounds.size.y;
                var origin = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                var yaw = Quaternion.Euler(0f, ModelYawDeg, 0f);
                foreach (var part in model.Parts)
                {
                    foreach (var mesh in part.Poses)
                    {
                        if (mesh == null) continue;
                        var vertices = mesh.vertices;
                        for (var v = 0; v < vertices.Length; v++) vertices[v] = yaw * ((vertices[v] - origin) * scale);
                        mesh.vertices = vertices;
                        if (ModelYawDeg != 0f)
                        {
                            var normals = mesh.normals;
                            for (var n = 0; n < normals.Length; n++) normals[n] = yaw * normals[n];
                            mesh.normals = normals;
                        }
                        mesh.RecalculateBounds();
                        mesh.UploadMeshData(true);
                    }
                }
                return model;
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[Pedestrians] Could not bake {prefab.name}: {error.Message}");
                return null;
            }
            finally
            {
                instance.SetActive(false);
                DestroyObject(instance);
            }
        }

        // The traffic's material asset, saved with instancing on: a player build strips a shader's instancing
        // variants unless some material asset uses them, and then instanced drawing shows nothing at all.
        // Copies of it keep instancing.
        private static Material InstancingTemplate()
        {
            var template = Resources.Load<Material>("Formula9/TrafficVehicle");
            if (template == null)
                Debug.LogError("[Pedestrians] The TrafficVehicle material is missing, so no pedestrians are drawn.");
            return template;
        }

        private Material[] InstancedMaterials(Material[] source)
        {
            var result = new Material[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var original = source[i];
                if (original == null) continue;
                if (!instancedMaterials.TryGetValue(original, out var copy))
                {
                    var template = InstancingTemplate();
                    if (template == null) continue;
                    copy = new Material(template) { name = original.name + " (pedestrian)" };
                    if (original.HasProperty("_MainTex")) copy.mainTexture = original.mainTexture;
                    if (original.HasProperty("_Color")) copy.color = original.color;
                    instancedMaterials[original] = copy;
                    ownedObjects.Add(copy);
                }
                result[i] = copy;
            }
            return result;
        }

        // Used when no character model can be baked: a tall thin blue box, scaled to each person's height
        private Model BoxModel()
        {
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (cube == null) return null;
            var mesh = UnityEngine.Object.Instantiate(cube);
            ownedObjects.Add(mesh);
            var vertices = mesh.vertices;
            for (var i = 0; i < vertices.Length; i++)
                vertices[i] = new Vector3(vertices[i].x * 0.18f, vertices[i].y + 0.5f, vertices[i].z * 0.18f);
            mesh.vertices = vertices;
            mesh.RecalculateBounds();

            var template = InstancingTemplate();
            if (template == null) return null;
            var material = new Material(template) { name = "Pedestrian box", color = new Color(0.22f, 0.45f, 0.95f, 1f) };
            ownedObjects.Add(material);
            var model = new Model();
            model.Parts.Add(new Part { Poses = new[] { mesh }, Materials = new[] { material } });
            return model;
        }
    }
}
