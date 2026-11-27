using System;
using UnityEngine;

namespace Formula9.World
{
    // Runtime catalog for pedestrian character prefabs, so player builds can load them without editor APIs.
    [CreateAssetMenu(menuName = "Formula9/Pedestrian Model Catalog")]
    public sealed class PedestrianModelCatalog : ScriptableObject
    {
        public Entry[] entries = Array.Empty<Entry>();

        [Serializable]
        public sealed class Entry
        {
            public GameObject prefab;
            public AnimationClip walkClip;
            public AnimationClip idleClip;
            public string walkClipName = "Walk";
        }
    }
}
