/*
 * The lighting of the training world.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's 3D world. One fixed setup (flat ambient light and one soft-
 *     shadowed directional light, no fog), so the blockout materials read as their authored colours
 *     instead of washing out under Unity's default sky lighting.
 */

using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // -----------------------------------------------------------------------------------
    // Class: WorldLighting
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class sets up the world's lighting.
    public static class WorldLighting
    {
        // -----------------------------------------------------------------------------------
        // Function: Apply
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sets flat ambient light, turns fog off and adds the directional light.
        //
        // Args:
        //     parent (Transform): Where the light is placed in the hierarchy.
        //
        // Returns:
        //     Light: The directional light.
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
