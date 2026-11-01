using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Formula9.Backend;
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
            public int offsetS;
            public int phase;
        }

        private List<ManagedLight> registeredLights = new List<ManagedLight>();

        private CitySignalTiming timing = new CitySignalTiming();
        private Func<double> clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public void InitializeTiming(CitySignalTiming signalTiming)
        {
            timing = signalTiming;
            Debug.LogWarning($"[TrafficLightManager] Timing initialized successfully: Green={timing.GreenS}s, Yellow={timing.YellowS}s, AllRed={timing.AllRedS}s");
        }

        // The lights show what traffic obeys, so they read the clock traffic runs on
        public void SetClock(Func<double> signalClock) => clock = signalClock;

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

        public void RegisterTrafficLight(HaloTrafficLight lightScript, string junctionId, int offsetS, int phase)
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
                double currentTime = clock();

                // Lights go when their city does
                registeredLights.RemoveAll(item => item.lightScript == null);
                foreach (var item in registeredLights)
                {
                    var color = timing.ColorAt(currentTime, item.offsetS, item.phase);
                    item.lightScript.OnLightStateChanged(color == SignalColor.Red, color == SignalColor.Yellow, color == SignalColor.Green);
                }

                yield return null; // Update every frame to smoothly sync time offsets
            }
        }
    }
}