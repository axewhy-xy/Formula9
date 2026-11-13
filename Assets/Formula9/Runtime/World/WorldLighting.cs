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
    //     This class sets up the world's lighting from Resources/Formula9/world_look.json: a warm
    //     sun with soft shadows, light from the sky, horizon and ground, and the city's sky.
    public static class WorldLighting
    {
        // -----------------------------------------------------------------------------------
        // Function: Apply
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sets the ambient light, sky, haze and shadow distance and adds
        //     the sun. The project's shadow settings come back when the sun is destroyed.
        //
        // Args:
        //     parent (Transform): Where the light is placed in the hierarchy.
        //
        // Returns:
        //     Light: The directional light.
        public static Light Apply(Transform parent)
        {
            var look = WorldLook.Default;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = WorldLook.Color(look.Ambient.Sky) * look.Ambient.Intensity;
            RenderSettings.ambientEquatorColor = WorldLook.Color(look.Ambient.Horizon) * look.Ambient.Intensity;
            RenderSettings.ambientGroundColor = WorldLook.Color(look.Ambient.Ground) * look.Ambient.Intensity;
            RenderSettings.fog = look.Haze.Enabled;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = look.Haze.Density;
            RenderSettings.fogColor = WorldLook.Color(look.Haze.Color);
            Shader.SetGlobalFloat("_Formula9ShadowDarkness", look.GroundShadowDarkness);
            var shader = Shader.Find("Formula9/Sky");
            if (shader != null)
            {
                var sky = new Material(shader) { name = "City sky", hideFlags = HideFlags.DontSave };
                sky.SetColor("_Top", WorldLook.Color(look.Sky.Top));
                sky.SetColor("_Horizon", WorldLook.Color(look.Sky.Horizon));
                sky.SetColor("_Below", WorldLook.Color(look.Sky.Below));
                sky.SetColor("_CloudColor", WorldLook.Color(look.Sky.CloudColor));
                sky.SetFloat("_CloudCover", look.Sky.CloudCover);
                RenderSettings.skybox = sky;
            }
            var light = new GameObject("World Light").AddComponent<Light>();
            light.transform.SetParent(parent, false);
            light.type = LightType.Directional;
            light.color = WorldLook.Color(look.Sun.Color);
            light.intensity = look.Sun.Intensity;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = look.Sun.ShadowStrength;
            light.transform.rotation = Quaternion.Euler(look.Sun.PitchDeg, look.Sun.YawDeg, 0);
            RenderSettings.sun = light;
            if (!System.Enum.TryParse(look.Shadows.Resolution, true, out ShadowResolution resolution)) resolution = ShadowResolution.High;
            light.gameObject.AddComponent<ShadowSettingsRestorer>().Apply(look.Shadows.DistanceM, look.Shadows.Cascades, resolution);
            return light;
        }

        // Puts the project's shadow distance and cascades back when the world's sun goes, so the
        // world's longer shadows do not leak into the project's quality settings
        private sealed class ShadowSettingsRestorer : MonoBehaviour
        {
            private float distance;
            private int cascades;
            private ShadowResolution resolution;
            private ShadowProjection projection;

            public void Apply(float distanceM, int cascadeCount, ShadowResolution shadowResolution)
            {
                distance = QualitySettings.shadowDistance;
                cascades = QualitySettings.shadowCascades;
                resolution = QualitySettings.shadowResolution;
                projection = QualitySettings.shadowProjection;
                QualitySettings.shadowDistance = distanceM;
                QualitySettings.shadowCascades = cascadeCount;
                QualitySettings.shadowResolution = shadowResolution;
                // Stable fit keeps shadow edges from crawling as the camera moves
                QualitySettings.shadowProjection = ShadowProjection.StableFit;
            }

            private void OnDestroy()
            {
                QualitySettings.shadowDistance = distance;
                QualitySettings.shadowCascades = cascades;
                QualitySettings.shadowResolution = resolution;
                QualitySettings.shadowProjection = projection;
            }
        }
    }
}
