/*
 * The lighting of the training world.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 26/11/2026
 *
 * Description:
 *     Part of the Unity client's 3D world. Trilight ambient light and one soft-shadowed directional
 *     light, so the blockout materials read as their authored colours instead of washing out under
 *     Unity's default sky lighting. DayCycle moves and colours them through the day.
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
        //     the sun. The project's shadow settings and the scene's sky, ambient light and fog
        //     come back when the sun is destroyed.
        //
        // Args:
        //     parent (Transform): Where the light is placed in the hierarchy.
        //
        // Returns:
        //     Light: The directional light.
        public static Light Apply(Transform parent)
        {
            var look = WorldLook.Default;
            var light = new GameObject("World Light").AddComponent<Light>();
            light.transform.SetParent(parent, false);
            // Taken before anything changes, and put back when the sun goes
            var restorer = light.gameObject.AddComponent<WorldSettingsRestorer>();
            restorer.Capture();
            Material sky = null;
            var shader = Shader.Find("Formula9/Sky");
            if (shader != null)
            {
                sky = new Material(shader) { name = "City sky", hideFlags = HideFlags.DontSave };
                RenderSettings.skybox = sky;
                restorer.Own(sky);
            }
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            RenderSettings.sun = light;
            // The sun, sky, ambient light and haze for the time of day and weather, set now and each frame
            light.gameObject.AddComponent<DayCycle>().Init(light, sky, look);
            if (!System.Enum.TryParse(look.Shadows.Resolution, true, out ShadowResolution resolution)) resolution = ShadowResolution.High;
            // Set outright, so a low quality level (shadows off) still shows the sun's shadows
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = look.Shadows.DistanceM;
            QualitySettings.shadowCascades = look.Shadows.Cascades;
            QualitySettings.shadowResolution = resolution;
            // Stable fit keeps shadow edges from crawling as the camera moves
            QualitySettings.shadowProjection = ShadowProjection.StableFit;
            // Facades seen side-on stay sharp: low quality levels turn off the textures' own anisotropic filtering
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            return light;
        }

        // Puts the project's shadow and texture filtering settings and the scene's sky, ambient light, fog and sun back
        // when the world's sun goes, so none of the world's look leaks into the project or a saved scene.
        // ExecuteAlways, because Unity only calls OnDestroy in Edit Mode (the EditMode render tests) with it.
        [ExecuteAlways]
        private sealed class WorldSettingsRestorer : MonoBehaviour
        {
            private bool captured;
            private ShadowQuality shadows;
            private float distance;
            private int cascades;
            private ShadowResolution resolution;
            private ShadowProjection projection;
            private AnisotropicFiltering anisotropic;
            private Material skybox;
            private AmbientMode ambientMode;
            private Color ambientSky, ambientEquator, ambientGround;
            private bool fog;
            private FogMode fogMode;
            private float fogDensity;
            private Color fogColor;
            private Light sun;
            private Material ownedSky;

            public void Capture()
            {
                shadows = QualitySettings.shadows;
                distance = QualitySettings.shadowDistance;
                cascades = QualitySettings.shadowCascades;
                resolution = QualitySettings.shadowResolution;
                projection = QualitySettings.shadowProjection;
                anisotropic = QualitySettings.anisotropicFiltering;
                skybox = RenderSettings.skybox;
                ambientMode = RenderSettings.ambientMode;
                ambientSky = RenderSettings.ambientSkyColor;
                ambientEquator = RenderSettings.ambientEquatorColor;
                ambientGround = RenderSettings.ambientGroundColor;
                fog = RenderSettings.fog;
                fogMode = RenderSettings.fogMode;
                fogDensity = RenderSettings.fogDensity;
                fogColor = RenderSettings.fogColor;
                sun = RenderSettings.sun;
                captured = true;
            }

            // The sky material made for the world, destroyed with the sun
            public void Own(Material sky) => ownedSky = sky;

            private void OnDestroy()
            {
                if (!captured) return;
                QualitySettings.shadows = shadows;
                QualitySettings.shadowDistance = distance;
                QualitySettings.shadowCascades = cascades;
                QualitySettings.shadowResolution = resolution;
                QualitySettings.shadowProjection = projection;
                QualitySettings.anisotropicFiltering = anisotropic;
                // Only if nothing else has taken over the sky since
                if (ownedSky == null || RenderSettings.skybox == ownedSky) RenderSettings.skybox = skybox;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientSkyColor = ambientSky;
                RenderSettings.ambientEquatorColor = ambientEquator;
                RenderSettings.ambientGroundColor = ambientGround;
                RenderSettings.fog = fog;
                RenderSettings.fogMode = fogMode;
                RenderSettings.fogDensity = fogDensity;
                RenderSettings.fogColor = fogColor;
                RenderSettings.sun = sun;
                // The unlit ground at full brightness again
                Shader.SetGlobalVector("_Formula9GroundDim", Vector4.zero);
                if (ownedSky != null)
                {
                    if (Application.isPlaying) Destroy(ownedSky);
                    else DestroyImmediate(ownedSky);
                }
            }
        }
    }
}
