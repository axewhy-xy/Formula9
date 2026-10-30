using Formula9.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.EditorTools
{
    // Builds the client scene and its UI panel settings from code, so they never need hand edits.
    // Run from the menu, or headless:
    //   Unity.exe -batchmode -quit -projectPath . -executeMethod Formula9.EditorTools.SceneBuilder.Build
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Formula9.unity";
        private const string PanelSettingsPath = "Assets/Formula9/UI/Formula9PanelSettings.asset";
        private const string ThemePath = "Assets/Formula9/UI/Formula9Theme.tss";
        private const string LayoutPath = "Assets/Formula9/UI/Formula9App.uxml";

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
            var app = new GameObject("Formula 9 App");
            var document = app.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = layout;
            app.AddComponent<Formula9App>();
            EditorSceneManager.SaveScene(scene, ScenePath);

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"Formula 9: built {ScenePath}");
        }
    }
}
