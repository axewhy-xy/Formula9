using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    internal static class LtaCityDividers
    {
        internal static void Build(string area, Transform roads, Mesh roadMesh, Shader surface,
            Action<Mesh> ownMesh, Action<Material> ownMaterial)
        {
            if (!string.Equals(area, "toa-payoh", StringComparison.OrdinalIgnoreCase)) return;

            var data = Resources.Load<TextAsset>("Formula9/RoadData/toa-payoh-dividers");
            if (data == null)
            {
                Debug.LogWarning("[CityView] Mapped divider data is missing.");
                return;
            }

            var holder = new GameObject("LTA mapped dividers");
            holder.transform.SetParent(roads, false);
            try
            {
                var map = JObject.Parse(data.text);
                if ((string)map["coordinate_space"] != "city-local-xz-metres" ||
                    !(map["tiles"] is JArray tiles) || tiles.Count == 0)
                    throw new FormatException("Invalid mapped divider data.");

                Material Make(string name, Color color)
                {
                    var mat = new Material(surface) { name = name, color = color, mainTexture = Texture2D.whiteTexture };
                    ownMaterial(mat);
                    if (mat.HasProperty("_StencilComp")) mat.SetInt("_StencilComp", (int)CompareFunction.Always);
                    if (mat.HasProperty("_StencilWriteMask")) mat.SetInt("_StencilWriteMask", 0);
                    if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 1);
                    return mat;
                }

                var concrete = Make("Mapped concrete kerbs", new Color(.66f, .66f, .64f));
                var islands = Make("Mapped concrete islands", new Color(.57f, .56f, .53f));
                var medians = Make("Mapped planted medians", new Color(.32f, .40f, .26f));
                var metal = Make("Mapped guard rails and railings", new Color(.56f, .57f, .58f));

                foreach (var tile in tiles)
                {
                    var origin = tile["origin_m"];
                    var child = new GameObject("Mapped divider tile");
                    child.transform.SetParent(holder.transform, false);
                    child.transform.localPosition = new Vector3((float)origin[0], 0f, (float)origin[1]);
                    new LtaMappedDividerTile(child.transform).Build((JObject)tile["kerbs"],
                        tile["divider_barriers"] as JArray, concrete, islands, medians, metal, ownMesh);
                }

                // Keep the original road's recessed gap faces beneath the surveyed dividers.
                var pavement = new List<int>(roadMesh.GetTriangles(RoadMesh.AsphaltLayer));
                pavement.AddRange(roadMesh.GetTriangles(RoadMesh.ConcreteMedianLayer));
                pavement.AddRange(roadMesh.GetTriangles(RoadMesh.GreenMedianLayer));
                roadMesh.SetTriangles(pavement, RoadMesh.AsphaltLayer, false);
                roadMesh.SetTriangles(Array.Empty<int>(), RoadMesh.ConcreteMedianLayer, false);
                roadMesh.SetTriangles(Array.Empty<int>(), RoadMesh.GreenMedianLayer, false);
                Debug.Log($"[CityView] Mapped dividers loaded on {tiles.Count} tiles.");
            }
            catch (Exception error)
            {
                holder.SetActive(false);
                WorldBuilder.Remove(holder);
                Debug.LogWarning($"[CityView] Mapped dividers failed: {error}");
            }
        }
    }
}
