/*
 * Starts the backend that ships beside a Windows build.
 *
 * Author: Lau Xiang Yang
 * Date Created: 06/11/2026
 * Last Modified: 10/11/2026
 *
 * Description:
 *     Part of the Unity client's backend layer. Tools/build_executable.py puts a portable Python and
 *     the backend in a Backend folder next to Formula9.exe. When the player opens, this runs that
 *     backend's serve.py hidden on the backend address, passing the player's process ID. serve.py
 *     decides the rest (Tools/bundled_backend.py): every open player shares one backend, which shuts
 *     down cleanly once the last of them has closed, even after a crash, and a backend already
 *     listening on that port (for example one started with run.bat) is used instead. Requests wait
 *     for the backend to start listening (WhenReadyAsync). Nothing happens in the editor, in a build
 *     without the Backend folder, or when FORMULA9_BACKEND_URL points to another machine.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Formula9.Backend
{
    // -----------------------------------------------------------------------------------
    // Class: BundledBackend
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class starts the backend a Windows build ships with and tells requests when it is
    //     ready.
    //
    //     Attributes: FolderName is the folder beside the executable that holds the backend;
    //     ReadyTimeout is how long requests wait for it to start listening.
    public static class BundledBackend
    {
        public const string FolderName = "Backend";
        public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

        private static Process process;
        private static Task ready = Task.CompletedTask;

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
        //     This function runs serve.py from playerFolder's Backend folder on the address
        //     BackendEndpoint gives, unless that address is on another machine. serve.py either
        //     joins the backend other players already share or becomes it. Its output is appended
        //     to Backend/logs/backend.log.
        //
        // Args:
        //     playerFolder (string): The folder that holds Formula9.exe.
        //
        // Returns:
        //     bool: True if it ran serve.py.
        public static bool TryStart(string playerFolder)
        {
            if (process != null) return false;
            var folder = Path.Combine(playerFolder, FolderName);
            var python = Path.Combine(folder, "python", "python.exe");
            var entry = Path.Combine(folder, "serve.py");
            if (!File.Exists(python) || !File.Exists(entry)) return false;

            var address = BackendEndpoint.ConfiguredUrl();
            if (!address.IsLoopback || address.AbsolutePath != "/")
            {
                Debug.Log($"The backend is {address}, not a local one; the bundled backend is not started.");
                return false;
            }

            var log = Path.Combine(folder, "logs", "backend.log");
            var info = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{entry}\" --host {address.DnsSafeHost} --port {address.Port} " +
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
            Process started;
            try
            {
                started = Process.Start(info);
            }
            catch (Exception error)
            {
                Debug.LogError($"Could not start the bundled backend: {error.Message}");
                return false;
            }
            process = started;
            var host = address.DnsSafeHost;
            var port = address.Port;
            ready = Task.Run(() => WaitUntilListening(started, host, port));
            // serve.py stops the backend once every player using it has closed, so quitting only
            // lets go of the handle; killing it here would skip its clean shutdown.
            Application.quitting += Release;
            Debug.Log($"Started the bundled backend for {address} (serve.py is process {started.Id}); log: {log}");
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Function: WhenReadyAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function waits until the bundled backend this player started is listening, it
        //     gave up, or ReadyTimeout passed. It returns at once when no bundled backend was
        //     started, such as in the editor.
        //
        // Args:
        //     cancellation (CancellationToken): Stops waiting.
        //
        // Returns:
        //     Task: Completes when requests can be sent.
        //
        // Throws:
        //     OperationCanceledException: When cancelled.
        public static async Task WhenReadyAsync(CancellationToken cancellation = default)
        {
            var waiting = ready;
            if (waiting.IsCompleted) return;
            var cancelled = new TaskCompletionSource<bool>();
            using (cancellation.Register(() => cancelled.TrySetResult(true)))
                await Task.WhenAny(waiting, cancelled.Task);
            cancellation.ThrowIfCancellationRequested();
        }

        // -----------------------------------------------------------------------------------
        // Function: Release
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function lets go of the serve.py process handle without stopping it.
        private static void Release()
        {
            process?.Dispose();
            process = null;
        }

        // -----------------------------------------------------------------------------------
        // Function: WaitUntilListening
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function blocks until host:port accepts connections, serve.py exits with an
        //     error, or ReadyTimeout passes. serve.py also exits, without an error, when it hands
        //     the player to a backend that is already running, so that exit does not end the wait.
        //
        // Args:
        //     serve (Process): The serve.py process.
        //     host (string): The backend host.
        //     port (int): The backend port.
        private static void WaitUntilListening(Process serve, string host, int port)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < ReadyTimeout)
            {
                if (IsListening(host, port)) return;
                try
                {
                    if (serve.HasExited && serve.ExitCode != 0) return;
                }
                catch (Exception)
                {
                    return; // released at quit
                }
                Thread.Sleep(250);
            }
            Debug.LogWarning($"The bundled backend did not answer within {ReadyTimeout.TotalSeconds:0} seconds; " +
                             "see Backend/logs/backend.log.");
        }

        // -----------------------------------------------------------------------------------
        // Function: IsListening
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks whether something accepts connections on host:port. uvicorn
        //     only listens once the app has started, so a connection means requests can be served.
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
