using UnityEngine;

public class PrefabXZDistanceScaler : MonoBehaviour
{
    [Tooltip("The first reference point inside the prefab.")]
    public Transform point1;

    [Tooltip("The second reference point inside the prefab.")]
    public Transform point2;

    [Tooltip("The desired physical distance on the XZ plane between the two points (in meters).")]
    public float targetXZDistanceInMeters = 1.0f;

    [ContextMenu("Apply Scale to Match XZ Distance")]
    public void AdjustScale()
    {
        if (point1 == null || point2 == null)
        {
            Debug.LogError("Please assign both point1 and point2 in the inspector.");
            return;
        }

        if (targetXZDistanceInMeters <= 0)
        {
            Debug.LogWarning("Target distance must be greater than zero.");
            return;
        }

        // Get the world positions of the two points
        Vector3 p1 = point1.position;
        Vector3 p2 = point2.position;

        // Flatten the positions on the Y axis to only measure horizontal XZ distance
        p1.y = 0;
        p2.y = 0;

        // Calculate the current horizontal distance between the points
        float currentXZDistance = Vector3.Distance(p1, p2);

        if (currentXZDistance <= 0.0001f)
        {
            Debug.LogError("The points are exactly on top of each other on the XZ plane. Cannot calculate a valid scale.");
            return;
        }

        // Calculate the scale factor required to make current distance match target distance
        float scaleFactor = targetXZDistanceInMeters / currentXZDistance;

        // Apply uniform scaling to the entire prefab
        transform.localScale = transform.localScale * scaleFactor;
        
        Debug.Log($"[PrefabXZDistanceScaler] Scale applied!\n" +
                  $"Old XZ Distance: {currentXZDistance:F4}m\n" +
                  $"New XZ Distance: {targetXZDistanceInMeters:F4}m\n" +
                  $"Scale Factor Multiplier: {scaleFactor:F4}");
    }
}
