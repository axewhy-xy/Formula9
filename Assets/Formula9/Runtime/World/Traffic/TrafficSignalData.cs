using UnityEngine;

namespace Formula9
{
    [System.Serializable]
    public class TrafficSignalEntry
    {
        public string signalId;
        public float x;
        public float y;
        public float z;
        public float greenDuration;
        public float yellowDuration;
        public float redDuration;
    }

    [System.Serializable]
    public class TrafficSignalList
    {
        public TrafficSignalEntry[] signals;
    }
}
