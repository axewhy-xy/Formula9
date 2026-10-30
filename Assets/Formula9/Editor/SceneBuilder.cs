/*
 * Builds the Unity client's scene and UI panel settings from code.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Editor tool, so the scene and panel settings never need hand edits. Run it from the menu
 *     (Formula 9 > Rebuild client scene), or headless: Unity.exe -batchmode -quit -projectPath .
 *     -executeMethod Formula9.EditorTools.SceneBuilder.Build
 */

using Formula9.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.EditorTools
{
    // -----------------------------------------------------------------------------------
    // Class: SceneBuilder
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class rebuilds the client scene.
    //
    //     Attributes: ScenePath is where the scene is saved.
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Formula9.unity";
        private const string PanelSettingsPath = "Assets/Formula9/UI/Formula9PanelSettings.asset";
        private const string ThemePath = "Assets/Formula9/UI/Formula9Theme.tss";
        private const string LayoutPath = "Assets/Formula9/UI/Formula9App.uxml";

        // -----------------------------------------------------------------------------------
        // Function: Build
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates or updates the panel settings (theme, scaling to a 1600 x 900
        //     reference), builds a scene with a camera that draws nothing (the UI covers the screen
        //     and the world view renders into its own texture) and the Formula 9 App object, saves
        //     it as the only build scene, switches to Linear colour space (glTF colours are linear;
        //     Gamma washes the blockout materials out) and includes the glTFast shaders.
        //
        // Throws:
        //     InvalidOperationException: If the theme or the layout is missing.
        [MenuItem("Formula 9/Rebuild client scene")]
        public static void Build()
        {
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            }
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath)
                                    ?? throw new System.InvalidOperationException($"Missing {ThemePath}");
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1600, 900);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            EditorUtility.SetDirty(panel);

            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath)
                         ?? throw new System.InvalidOperationException($"Missing {LayoutPath}");

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            camera.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
            camera.GetComponent<Camera>().backgroundColor = new Color(0.1f, 0.1f, 0.12f);
            // The UI covers the screen and the world view renders into its own texture.
            camera.GetComponent<Camera>().cullingMask = 0;
            var app = new GameObject("Formula 9 App");
            var document = app.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = layout;
            app.AddComponent<Formula9App>();
            EditorSceneManager.SaveScene(scene, ScenePath);

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            // glTF colours are linear; Gamma colour space washes the blockout materials out.
            PlayerSettings.colorSpace = ColorSpace.Linear;
            IncludeGltfShaders();
            AssetDatabase.SaveAssets();
            Debug.Log($"Formula 9: built {ScenePath}");
        }

        // -----------------------------------------------------------------------------------
        // Function: IncludeGltfShaders
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function adds glTFast's shaders to Always Included Shaders. glTFast creates its
        //     materials at runtime, so a player build only has its shaders when they are always
        //     included; otherwise imported models render magenta.
        //
        // Throws:
        //     InvalidOperationException: If a glTFast shader cannot be found.
        private static void IncludeGltfShaders()
        {
            var settings = new SerializedObject(AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset"));
            var list = settings.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in new[] { "glTF/PbrMetallicRoughness", "glTF/PbrSpecularGlossiness", "glTF/Unlit" })
            {
                var shader = Shader.Find(name) ?? throw new System.InvalidOperationException($"glTFast shader {name} not found");
                var present = false;
                for (var i = 0; i < list.arraySize; i++) present |= list.GetArrayElementAtIndex(i).objectReferenceValue == shader;
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
