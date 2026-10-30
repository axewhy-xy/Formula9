using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // One fixed lighting setup for the training world, so the blockout materials read as their
    // authored colours instead of washing out under Unity's default sky lighting.
    public static class WorldLighting
    {
        public static Light Apply(Transform parent)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.32f, 0.33f, 0.35f);
            RenderSettings.fog = false;
            var light = new GameObject("World Light").AddComponent<Light>();
            light.transform.SetParent(parent, false);
            light.type = LightType.Directional;
            light.intensity = 0.75f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);
            return light;
        }
    }
}
