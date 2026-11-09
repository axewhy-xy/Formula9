/*
 * Builds the Windows player.
 *
 * Author: Lau Xiang Yang
 * Date Created: 06/11/2026
 * Last Modified: 06/11/2026
 *
 * Description:
 *     Editor tool used by Tools/build_executable.py, which then puts the backend beside the player.
 *     Headless: Unity.exe -batchmode -quit -projectPath . -executeMethod
 *     Formula9.EditorTools.PlayerBuild.BuildWindows -buildPath Builds/Formula9/Formula9.exe
 */

using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Formula9.EditorTools
{
    // -----------------------------------------------------------------------------------
    // Class: PlayerBuild
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class builds the client as a 64-bit Windows player.
    //
    //     Attributes: DefaultPath is where the player goes when no -buildPath is given.
    public static class PlayerBuild
    {
        public const string DefaultPath = "Builds/Formula9/Formula9.exe";

        // -----------------------------------------------------------------------------------
        // Function: BuildWindows
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds the enabled scenes in the build settings to the path after
        //     -buildPath on the command line, or DefaultPath. In batch mode a failed build exits
        //     the editor with code 1, so the calling script sees it.
        //
        // Throws:
        //     InvalidOperationException: If the build fails outside batch mode.
        public static void BuildWindows()
        {
            var path = ArgumentAfter("-buildPath") ?? DefaultPath;
            var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0)
            {
                Fail("No scenes are enabled in the build settings; run Formula 9 > Rebuild client scene.");
                return;
            }
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });
            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
            {
                Fail($"Player build {summary.result} with {summary.totalErrors} error(s); see the log above.");
                return;
            }
            Debug.Log($"Built {path}: {summary.totalSize / (1024 * 1024)} MB in {summary.totalTime.TotalSeconds:F0} s");
        }

        private static void Fail(string message)
        {
            Debug.LogError(message);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            else throw new InvalidOperationException(message);
        }

        private static string ArgumentAfter(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
