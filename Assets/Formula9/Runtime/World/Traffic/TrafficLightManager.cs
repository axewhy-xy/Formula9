using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using HealthbarGames;
using System.Security.Cryptography;

namespace Formula9
{
    public class TrafficLightManager : MonoBehaviour
    {
        private static TrafficLightManager _instance;
        public static TrafficLightManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    // Automatically find an existing one in the scene
                    _instance = FindObjectOfType<TrafficLightManager>();

                    // If still not found, create a new GameObject on the fly automatically!
                    if (_instance == null)
                    {
                        GameObject managerObj = new GameObject("TrafficLightManager");
                        _instance = managerObj.AddComponent<TrafficLightManager>();
                        // Optional: keep it alive or parent it under your app structure if needed
                        DontDestroyOnLoad(managerObj);
                    }
                }
                return _instance;
            }
        }

        // Structure to hold registered light data along with its JSON parameters
        private class ManagedLight
        {
            public HaloTrafficLight lightScript;
            public string junctionId;
            public float offsetS;
            public int phase;
        }

        private List<ManagedLight> registeredLights = new List<ManagedLight>();

        private float cycleS, greenS, yellowS, allRedS;

        public void InitializeTiming(float cycle, float green, float yellow, float allRed)
        {
            cycleS = cycle;
            greenS = green;
            yellowS = yellow;
            allRedS = allRed;
            Debug.LogWarning($"[TrafficLightManager] Timing initialized successfully: Cycle={cycleS}s, Green={greenS}s, Yellow={yellowS}s");
        }

        void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
                return;
            }
        }

        public void RegisterTrafficLight(HaloTrafficLight lightScript, string junctionId, float offsetS, int phase)
        {
            if (lightScript != null)
            {
                registeredLights.Add(new ManagedLight
                {
                    lightScript = lightScript,
                    junctionId = junctionId,
                    offsetS = offsetS,
                    phase = phase
                });
            }
        }

        void Start()
        {
            StartCoroutine(GlobalTrafficLoop());
        }

        IEnumerator GlobalTrafficLoop()
        {
            while (true)
            {
                float currentTime = Time.time;

                foreach (var item in registeredLights)
                {
                    if (item.lightScript == null) continue;

                    // Use lowercase instance variables parsed from the JSON timing config
                    float localTime = (currentTime + item.offsetS) % cycleS;
                    bool red = false, yellow = false, green = false;

                    if (item.phase == 1)
                    {
                        if (localTime < greenS) green = true;
                        else if (localTime < greenS + yellowS) yellow = true; // <--- Changed YellowS to yellowS
                        else red = true;
                    }
                    else
                    {
                        float halfCycle = cycleS / 2f;
                        float phase2Time = (localTime + halfCycle) % cycleS;

                        if (phase2Time < greenS) green = true;
                        else if (phase2Time < greenS + yellowS) yellow = true; // <--- Changed YellowS to yellowS
                        else red = true;
                    }

                    item.lightScript.OnLightStateChanged(red, yellow, green);
                }

                yield return null; // Update every frame to smoothly sync time offsets
            }
        }
    }
}