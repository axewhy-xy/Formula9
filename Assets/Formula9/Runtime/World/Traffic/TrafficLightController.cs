using UnityEngine;
using System.Collections;

namespace Formula9
{
    public class TrafficLightController : MonoBehaviour
    {
        public enum LightState { Red, Yellow, Green }
        [Header("Current State")]
        public LightState currentState = LightState.Red;

        [Header("Timings (Seconds)")]
        public float greenTime = 5f;
        public float yellowTime = 2f;
        public float redTime = 5f;

        [Header("Visual Elements (Materials or GameObjects)")]
        public Renderer lightRenderer; // Assign the mesh renderer of your light bulb
        public Material redMat;
        public Material yellowMat;
        public Material greenMat;

        void Start()
        {
            StartCoroutine(TrafficLightDecisionTreeRoutine());
        }

        IEnumerator TrafficLightDecisionTreeRoutine()
        {
            while (true)
            {
                // Decision Tree / State Evaluation
                switch (currentState)
                {
                    case LightState.Red:
                        SetLightVisual(LightState.Red);
                        yield return new WaitForSeconds(redTime);
                        // Decision transition: Red -> Green
                        currentState = LightState.Green;
                        break;

                    case LightState.Green:
                        SetLightVisual(LightState.Green);
                        yield return new WaitForSeconds(greenTime);
                        // Decision transition: Green -> Yellow
                        currentState = LightState.Yellow;
                        break;

                    case LightState.Yellow:
                        SetLightVisual(LightState.Yellow);
                        yield return new WaitForSeconds(yellowTime);
                        // Decision transition: Yellow -> Red
                        currentState = LightState.Red;
                        break;
                }
            }
        }

        void SetLightVisual(LightState state)
        {
            if (lightRenderer == null) return;

            // Simple decision logic to switch materials based on active state
            switch (state)
            {
                case LightState.Red:
                    if (redMat != null) lightRenderer.material = redMat;
                    break;
                case LightState.Yellow:
                    if (yellowMat != null) lightRenderer.material = yellowMat;
                    break;
                case LightState.Green:
                    if (greenMat != null) lightRenderer.material = greenMat;
                    break;
            }
        }
    }
}
