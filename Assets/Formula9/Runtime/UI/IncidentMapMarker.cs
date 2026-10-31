using UnityEngine;

/// <summary>
/// Visual-only map marker. Feed it map_marker.center.x_m/y_m from an
/// OPTIONS_READY or INCIDENT_DETECTED event. Does not alter simulation state.
/// The caller must convert backend planar coordinates to the scene's map space.
/// </summary>
public class IncidentMapMarker : MonoBehaviour
{
    [SerializeField] private GameObject markerPrefab;
    [SerializeField] private Transform markerParent;
    [SerializeField] private bool mapUsesXZ = true;
    [SerializeField] private float height = 0.5f;

    public GameObject Show(string incidentId, float sceneX, float sceneY)
    {
        if (markerPrefab == null)
        {
            Debug.LogWarning("[INCIDENT MAP] Assign markerPrefab in Inspector");
            return null;
        }
        Vector3 position = mapUsesXZ
            ? new Vector3(sceneX, height, sceneY)
            : new Vector3(sceneX, sceneY, height);
        var marker = Instantiate(markerPrefab, position, Quaternion.identity,
            markerParent != null ? markerParent : transform);
        marker.name = "Incident-" + incidentId;
        Debug.Log($"[INCIDENT MAP] Placed {incidentId} at {position}");
        return marker;
    }
}
