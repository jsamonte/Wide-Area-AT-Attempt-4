using UnityEngine;

public class MapCameraFollow : MonoBehaviour
{
    [Tooltip("The object the camera should follow (e.g., the Main Camera or XR Origin).")]
    public Transform targetToFollow;

    [Tooltip("The fixed height (Y-axis) the camera should stay at.")]
    public float fixedHeight = 10f;

    [Tooltip("If true, the camera will always look straight down (90 degrees on the X axis).")]
    public bool forceLookDown = true;

    void Start()
    {
        // If the prefab was spawned at runtime, it might not have a scene target assigned.
        // Automatically find the player's head (Main Camera) to follow.
        if (targetToFollow == null && Camera.main != null)
        {
            targetToFollow = Camera.main.transform;
        }
    }

    void LateUpdate()
    {
        if (targetToFollow != null)
        {
            // Follow the target's X and Z, but lock the Y to the fixed height
            transform.position = new Vector3(targetToFollow.position.x, fixedHeight, targetToFollow.position.z);
        }

        if (forceLookDown)
        {
            // Force the camera to look straight down (90 degrees on the X axis)
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
