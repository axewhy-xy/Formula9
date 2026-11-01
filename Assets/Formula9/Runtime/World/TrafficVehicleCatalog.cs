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
        }
    }
}
