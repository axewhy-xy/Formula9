//using System.Collections.Generic;
//using System.IO;
//using UnityEngine;

//namespace Formula9
//{
//    public class TrafficLightManager : MonoBehaviour
//    {
//        public static TrafficLightManager Instance;

//        private List<HaloTrafficLight> registeredLights = new List<HaloTrafficLight>();

//        void Awake()
//        {
//            if (Instance == null) Instance = this;
//            else Destroy(gameObject);
//        }

//        public void RegisterTrafficLight(HaloTrafficLight lightScript, string roadCode)
//        {
//            if (!registeredLights.Contains(lightScript))
//            {
//                registeredLights.Add(lightScript);
//            }
//        }

//        void Start()
//        {
//            // Start the global traffic light cycle loop
//            StartCoroutine(TrafficLightCycleRoutine());
//        }

//        IEnumerator TrafficLightCycleRoutine()
//        {
//            while (true)
//            {
//                // Phase 1: RED
//                SetAllLightsState(LightState.Red);
//                yield return new WaitForSeconds(30f);

//                // Phase 3: YELLOW / AMBER
//                SetAllLightsState(LightState.Yellow);
//                yield return new WaitForSeconds(3f);

//                // Phase 2: GREEN
//                SetAllLightsState(LightState.Green);
//                yield return new WaitForSeconds(30f);
//            }
//        }

//        void SetAllLightsState(bool red, bool yellow, bool green)
//        {
//            foreach (var light in registeredLights)
//            {
//                if (light != null)
//                {
//                    // Calls the built-in method from HaloTrafficLight.cs
//                    light.OnLightStateChanged(red, yellow, green);
//                }
//            }
//        }
//    }
//}