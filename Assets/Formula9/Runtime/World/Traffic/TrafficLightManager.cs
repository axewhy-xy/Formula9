using System.Diagnostics;
using UnityEngine;

namespace Formula9
{
    public class TrafficLightManager : MonoBehaviour
    {
        [Header("References")]
        public GameObject trafficLightPrefab; // Assign your 3D traffic light prefab here
        public TextAsset jsonFile;            // Drag your toa-payoh-signals.json here in Inspector

        void Start()
        {
            if (jsonFile != null)
            {
                SpawnTrafficLights();
            }
            else
            {
                UnityEngine.Debug.LogError("JSON file not assigned to TrafficLightManager!");
            }
        }

        void SpawnTrafficLights()
        {
            // Parse the JSON data
            TrafficSignalList dataList = JsonUtility.FromJson<TrafficSignalList>(jsonFile.text);

            if (dataList == null || dataList.signals == null)
            {
                // Fallback or alternative parsing if the root is an array instead of an object
                // If JsonUtility fails on root arrays, you can use JsonUtility with a wrapper or Newtonsoft.Json.
                UnityEngine.Debug.LogWarning("Standard wrapper parsing failed or empty. Trying manual array parse...");
                ParseAsArray(jsonFile.text);
                return;
            }

            foreach (var entry in dataList.signals)
            {
                Vector3 spawnPos = new Vector3(entry.x, entry.y, entry.z);
                GameObject spawnedLight = Instantiate(trafficLightPrefab, spawnPos, Quaternion.identity);

                // Pass the timing parameters to the light controller
                TrafficLightController controller = spawnedLight.GetComponent<TrafficLightController>();
                if (controller != null)
                {
                    controller.greenTime = entry.greenDuration > 0 ? entry.greenDuration : 5f;
                    controller.yellowTime = entry.yellowDuration > 0 ? entry.yellowDuration : 2f;
                    controller.redTime = entry.redDuration > 0 ? entry.redDuration : 5f;
                }
            }
        }

        void ParseAsArray(string jsonString)
        {
            // Unity's JsonUtility doesn't natively parse top-level arrays. 
            // We wrap it using a helper trick or standard iteration if needed:
            string wrappedJson = "{\"signals\":" + jsonString + "}";
            TrafficSignalList dataList = JsonUtility.FromJson<TrafficSignalList>(wrappedJson);

            if (dataList != null && dataList.signals != null)
            {
                foreach (var entry in dataList.signals)
                {
                    Vector3 spawnPos = new Vector3(entry.x, entry.y, entry.z);
                    GameObject spawnedLight = Instantiate(trafficLightPrefab, spawnPos, Quaternion.identity);

                    TrafficLightController controller = spawnedLight.GetComponent<TrafficLightController>();
                    if (controller != null)
                    {
                        controller.greenTime = 5f;
                        controller.yellowTime = 2f;
                        controller.redTime = 5f;
                    }
                }
            }
        }
    }
}
