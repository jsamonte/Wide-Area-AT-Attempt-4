using UnityEngine;

/// <summary>
/// Labels an object for the gaze logger in <see cref="EyeAndHeadTracker"/>.
///
/// The tracker records every collider the eye ray lands on, but on its own it can only
/// report the GameObject's runtime name (which for spawned prefabs is "Something(Clone)")
/// and an "Other" category. Attach this to the root of anything you want reported under a
/// meaningful name and grouping — gems, recall objects, signage, the map, the wireframe.
///
/// RandomSpawner attaches this automatically to everything it spawns, so for a normal trial
/// you only need to add it by hand to fixed scene objects you care about.
/// </summary>
public class GazeLoggableObject : MonoBehaviour
{
    [Tooltip("Grouping used in the JSON summary's per-category rollup. Common values: Target, RecallObject, Other.")]
    public string category = "Other";

    [Tooltip("Name reported in the logs. Leave empty to use this GameObject's name.")]
    public string displayName = "";

    /// <summary>The name the tracker should log for this object.</summary>
    public string ResolvedName => string.IsNullOrEmpty(displayName) ? gameObject.name : displayName;
}
