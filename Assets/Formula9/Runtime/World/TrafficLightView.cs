using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using UnityEngine;
using static System.Net.Mime.MediaTypeNames;

namespace Formula9.World
{
    // Mimics CityView architecture to fetch and render traffic lights on the 3D map.
    public sealed class TrafficLightView : IDisposable
    {
        private readonly Transform root;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly List<GameObject> trafficLightObjects = new List<GameObject>();

        public int TrafficLightCount { get; private set; }

        public TrafficLightView(Transform parent)
        {
            root = new GameObject("TrafficLights").transform;
            root.SetParent(parent, false);
        }

        private T Own<T>(T item) where T : UnityEngine.Object
        {
            owned.Add(item);
            return item;
        }

        public async Task LoadAsync(CityClient client, string area, GameObject trafficLightPrefab, CancellationToken cancellation)
        {
            if (trafficLightPrefab == null)
            {
                UnityEngine.Debug.LogWarning("Traffic light prefab is missing. Cannot render traffic lights.");
                return;
            }

            List<CitySignalTrafficLight> signals;
            try
            {
                signals = await client.GetSignalTrafficLightAsync(area, cancellation);
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                UnityEngine.Debug.LogWarning("Failed to load traffic signals from backend: " + error.Message);
                return;
            }

            cancellation.ThrowIfCancellationRequested();
            TrafficLightCount = signals.Count;

            foreach (var signal in signals)
            {
                // Map backend coordinates (XM, YM) to Unity 3D world space (X, Y, Z)
                var position = new Vector3((float)signal.XM, 0f, (float)signal.YM);

                // Set rotation based on the facing heading from the backend data
                var rotation = Quaternion.Euler(0f, (float)signal.FacingHeadingDeg, 0f);

                // Instantiate the prefab as a child of the TrafficLights root container
                var lightObj = UnityEngine.Object.Instantiate(trafficLightPrefab, position, rotation, root);
                lightObj.name = $"TrafficLight_{signal.Id ?? signal.Junction}";

                trafficLightObjects.Add(lightObj);
            }

            UnityEngine.Debug.Log($"Successfully rendered {TrafficLightCount} traffic lights into the 3D map.");
        }

        public void SetVisible(bool visible)
        {
            if (root != null) root.gameObject.SetActive(visible);
        }

        public void Dispose()
        {
            if (root != null) WorldBuilder.Remove(root.gameObject);

            foreach (var item in owned)
            {
                if (item != null)
                {
                    if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(item);
                    else UnityEngine.Object.DestroyImmediate(item);
                }
            }
            owned.Clear();
            trafficLightObjects.Clear();
        }
    }
}
