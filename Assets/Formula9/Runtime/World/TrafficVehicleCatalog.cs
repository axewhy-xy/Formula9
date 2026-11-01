using System;
using UnityEngine;

namespace Formula9.World
{
    // Which imported vehicle prefab stands in for each traffic model id (car, bus, truck, ...).
    public sealed class TrafficVehicleCatalog : ScriptableObject
    {
        public Entry[] entries = Array.Empty<Entry>();

        [Serializable]
        public sealed class Entry
        {
            public string modelId;
            public GameObject prefab;
            // Turns a model that is not authored facing +Z
            public float yawDegrees;
            // Whether its bodywork takes the colour the traffic sample gives each vehicle. Off for a
            // model whose texture is its own paint, like a taxi's livery.
            public bool paintable;
            // Used in place of the prefab's own material, for one whose paint would not take a colour
            public Material material;
        }
    }
}
