/*
 * Starts the backend that ships beside a Windows build.
 *
 * Author: Lau Xiang Yang
 * Date Created: 06/11/2026
 * Last Modified: 06/11/2026
 *
 * Description:
 *     Part of the Unity client's backend layer. Tools/build_executable.py puts a portable Python and
 *     the backend in a Backend folder next to Formula9.exe. When the player opens, this starts that
 *     backend hidden on the default address and stops it when the player quits; the backend also
 *     stops by itself if the player crashes. Nothing happens in the editor, in a build without the
 *     Backend folder, when FORMULA9_BACKEND_URL points elsewhere, or when a backend is already
 *     listening on the default port (for example one started with run.bat).
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Formula9.Backend
{
    // -----------------------------------------------------------------------------------
    // Class: BundledBackend
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class owns the backend process a Windows build starts for itself.
    //
    //     Attributes: FolderName is the folder beside the executable that holds the backend.
    public static class BundledBackend
    {
        public const string FolderName = "Backend";

        private static Process process;

        // -----------------------------------------------------------------------------------
        // Function: StartWithPlayer
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function runs once before the first scene loads, in Windows builds only, and
        //     starts the bundled backend from the folder that holds the executable.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StartWithPlayer()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // A Windows player's dataPath is <build>/Formula9_Data.
            TryStart(Path.GetDirectoryName(Application.dataPath));
#endif
        }

        // -----------------------------------------------------------------------------------
        // Function: TryStart
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function starts the backend in playerFolder's Backend folder, unless another
        //     backend is configured or already listening, and stops it when the application quits.
        //     Its output goes to Backend/logs/backend.log.
        //
        // Args:
        //     playerFolder (string): The folder that holds Formula9.exe.
        //
        // Returns:
        //     bool: True if it started a backend.
        public static bool TryStart(string playerFolder)
        {
            if (process != null) return false;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FORMULA9_BACKEND_URL"))) return false;
            var folder = Path.Combine(playerFolder, FolderName);
            var python = Path.Combine(folder, "python", "python.exe");
            var entry = Path.Combine(folder, "serve.py");
            if (!File.Exists(python) || !File.Exists(entry)) return false;

            var address = new Uri(BackendEndpoint.DefaultUrl);
            if (IsListening(address.Host, address.Port))
            {
                Debug.Log($"A backend is already running at {address}; using it instead of the bundled one.");
                return false;
            }

            var log = Path.Combine(folder, "logs", "backend.log");
            var info = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{entry}\" --host {address.Host} --port {address.Port} " +
                            $"--parent-pid {Process.GetCurrentProcess().Id} --log \"{log}\"",
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Python settings from the user's own environment must not change the bundled one.
            info.EnvironmentVariables.Remove("PYTHONHOME");
            info.EnvironmentVariables.Remove("PYTHONPATH");
            info.EnvironmentVariables.Remove("VIRTUAL_ENV");
            info.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            try
            {
                process = Process.Start(info);
            }
            catch (Exception error)
            {
                Debug.LogError($"Could not start the bundled backend: {error.Message}");
                return false;
            }
            Application.quitting += Stop;
            Debug.Log($"Started the bundled backend at {address} (process {process.Id}); log: {log}");
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Function: Stop
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function stops the backend this class started, if it is still running.
        public static void Stop()
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception error)
            {
                Debug.LogWarning($"Could not stop the bundled backend: {error.Message}");
            }
            process.Dispose();
            process = null;
        }

        // -----------------------------------------------------------------------------------
        // Function: IsListening
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks whether something accepts connections on host:port.
        //
        // Args:
        //     host (string): The host to try.
        //     port (int): The port to try.
        //
        // Returns:
        //     bool: True if a connection was accepted within half a second.
        private static bool IsListening(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                return client.ConnectAsync(host, port).Wait(500) && client.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
