using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Plays a warning audio clip when the XR Rig is above any of the assigned caution planes (e.g. stairs).
///
/// Fixes vs. the original version:
///  - cautionPlanes now also matches child colliders. If you dragged a parent
///    GameObject (rather than the exact object holding the Collider), the
///    original Contains() check would silently never match. We now resolve
///    every assigned GameObject/Collider down to its full set of child
///    colliders once at Start (and whenever the list changes at runtime).
///  - Debug logs now show every raycast hit each check, not just matches, so
///    you can see in the console exactly what's being hit vs. what's expected.
///  - cautionLayerMask defaults to Everything; if the Inspector shows "Nothing"
///    for an older/prefab-created instance, re-select "Everything" (or your
///    specific layer) explicitly -- this is a common Unity gotcha, not a code bug,
///    but worth checking.
/// </summary>
public class CautionStairsAudio : MonoBehaviour
{
    [Header("Audio Settings")]
    [Tooltip("The audio clip to play when the XR rig is above a plane.")]
    public AudioClip warningAudio;

    [Tooltip("Delay in seconds before the audio can repeat after finishing.")]
    public float repeatDelay = 2.0f;

    [Header("References")]
    [Tooltip("The XR Rig / XR Origin Transform (usually the root of your XR setup).")]
    public Transform xrRig;

    [Tooltip("Optional: XR Camera (head) transform. If assigned, raycasts start from camera position for better accuracy.")]
    public Transform xrCamera;

    [Tooltip("List of colliders representing the caution areas (stairs, etc.). You can drag either the exact collider object OR a parent object -- child colliders are automatically included.")]
    public List<Collider> cautionPlanes = new List<Collider>();

    [Header("Detection Settings")]
    [Tooltip("How far down to cast the ray.")]
    public float maxRaycastDistance = 10f;

    [Tooltip("Vertical offset above the start position when doing raycast.")]
    public float raycastHeightOffset = 0.5f;

    [Tooltip("Check this to use bounding box check instead of raycast. Good for triggers or when you don't want physics layers.")]
    public bool useBoundsCheckInsteadOfRaycast = false;

    [Header("Layer Settings (Raycast mode)")]
    [Tooltip("LayerMask for raycast. Create a 'Caution' layer and put your planes on it, OR use Everything to start testing. NOTE: double-check this shows 'Everything' in the Inspector -- it can default to 'Nothing' on some prefab instances.")]
    public LayerMask cautionLayerMask = -1; // -1 = Everything

    [Header("Debug")]
    [Tooltip("Enable console logs when the rig enters a caution area or audio plays. Great for testing.")]
    public bool enableDebugLogs = true;

    [Tooltip("Log every raycast hit each frame (very verbose) -- use this to see exactly what's under the rig even when it doesn't match a caution plane.")]
    public bool logAllRaycastHits = false;

    private AudioSource audioSource;
    private bool isPlayingSequence = false;

    // Expanded set built from cautionPlanes: includes every collider in the
    // children of each assigned object, so dragging a parent GameObject still
    // works correctly.
    private readonly HashSet<Collider> _resolvedColliders = new HashSet<Collider>();
    private int _lastResolvedFromCount = -1;

    void Start()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f; // 2D global warning sound (recommended)

        if (warningAudio != null)
        {
            audioSource.clip = warningAudio;
        }
        else
        {
            Debug.LogWarning("CautionStairsAudio: No Warning Audio Clip assigned!", this);
        }

        RebuildResolvedColliders();

        if (enableDebugLogs)
        {
            Debug.Log($"CautionStairsAudio: Start complete. cautionPlanes={cautionPlanes.Count} resolvedColliders(incl. children)={_resolvedColliders.Count} mode={(useBoundsCheckInsteadOfRaycast ? "Bounds" : "Raycast")} layerMask={cautionLayerMask.value}", this);
            if (_resolvedColliders.Count == 0)
            {
                Debug.LogWarning("CautionStairsAudio: No colliders resolved from cautionPlanes! Make sure the assigned objects (or their children) actually have Collider components.", this);
            }
        }
    }

    /// <summary>
    /// Expands cautionPlanes into every collider found on those objects AND
    /// their children, so it doesn't matter whether you dragged the exact
    /// collider object or a parent container.
    /// </summary>
    private void RebuildResolvedColliders()
    {
        _resolvedColliders.Clear();
        foreach (var c in cautionPlanes)
        {
            if (c == null) continue;
            _resolvedColliders.Add(c);
            foreach (var child in c.GetComponentsInChildren<Collider>(true))
            {
                if (child != null) _resolvedColliders.Add(child);
            }
        }
        _lastResolvedFromCount = cautionPlanes.Count;
    }

    void Update()
    {
        if (xrRig == null)
        {
            if (enableDebugLogs) Debug.LogWarning("CautionStairsAudio: xrRig not assigned -- nothing will be detected.", this);
            return;
        }
        if (cautionPlanes == null || cautionPlanes.Count == 0)
        {
            if (enableDebugLogs) Debug.LogWarning("CautionStairsAudio: cautionPlanes list is empty -- nothing to detect.", this);
            return;
        }

        // Keep the resolved set in sync if the list was edited at runtime (e.g. in the Inspector during play mode).
        if (cautionPlanes.Count != _lastResolvedFromCount)
        {
            RebuildResolvedColliders();
        }

        bool isAbovePlane = CheckIfAboveCautionPlane();

        if (isAbovePlane && !isPlayingSequence)
        {
            if (enableDebugLogs)
                Debug.Log("CautionStairsAudio: XR Rig is above a caution plane -> starting audio sequence.", this);

            StartCoroutine(PlayAudioSequence());
        }
    }

    private bool CheckIfAboveCautionPlane()
    {
        if (useBoundsCheckInsteadOfRaycast)
        {
            // Bounds mode (good for triggers or no physics).
            // IMPORTANT: use the camera position (falling back to the rig) --
            // on most XR Origin setups the rig root stays near the origin while
            // the camera moves within it as the user physically walks, so
            // checking xrRig.position alone never changes and this check would
            // never trigger.
            Vector3 rigPos = (xrCamera != null) ? xrCamera.position : xrRig.position;

            if (logAllRaycastHits)
            {
                Debug.Log($"CautionStairsAudio: bounds check at pos={rigPos} against {_resolvedColliders.Count} collider(s).", this);
            }

            foreach (var plane in _resolvedColliders)
            {
                if (plane == null) continue;

                Bounds bounds = plane.bounds;

                // XZ containment + forgiving Y range (handles stairs height differences)
                if (rigPos.x >= bounds.min.x && rigPos.x <= bounds.max.x &&
                    rigPos.z >= bounds.min.z && rigPos.z <= bounds.max.z &&
                    rigPos.y >= bounds.min.y - 2f && rigPos.y <= bounds.max.y + 5f)
                {
                    if (enableDebugLogs) Debug.Log($"CautionStairsAudio: Bounds match on '{plane.name}'.", this);
                    return true;
                }
            }
        }
        else
        {
            // Raycast mode (recommended) - with LayerMask + trigger support
            Vector3 basePos = (xrCamera != null) ? xrCamera.position : xrRig.position;
            Vector3 rayStart = basePos + Vector3.up * raycastHeightOffset;

            RaycastHit[] hits = Physics.RaycastAll(
                rayStart,
                Vector3.down,
                maxRaycastDistance + raycastHeightOffset,
                cautionLayerMask,
                QueryTriggerInteraction.Collide   // makes it work with trigger colliders too
            );

            if (logAllRaycastHits && hits.Length > 0)
            {
                Debug.Log($"CautionStairsAudio: raycast from {rayStart} hit {hits.Length} collider(s): {string.Join(", ", hits.Select(h => h.collider.name))}", this);
            }

            foreach (var hit in hits)
            {
                if (_resolvedColliders.Contains(hit.collider))
                {
                    if (enableDebugLogs) Debug.Log($"CautionStairsAudio: Raycast match on '{hit.collider.name}'.", this);
                    return true;
                }
            }
        }

        return false;
    }

    private IEnumerator PlayAudioSequence()
    {
        isPlayingSequence = true;

        if (enableDebugLogs)
            Debug.Log("CautionStairsAudio: Playing warning audio now.", this);

        if (warningAudio != null && audioSource != null)
        {
            audioSource.clip = warningAudio; // in case it was changed at runtime
            audioSource.Play();
            yield return new WaitForSeconds(warningAudio.length);
        }
        else
        {
            if (enableDebugLogs)
                Debug.LogWarning("CautionStairsAudio: warningAudio or audioSource is null -- no sound will play, but the sequence/delay still runs.", this);
        }

        yield return new WaitForSeconds(repeatDelay);

        isPlayingSequence = false;

        if (enableDebugLogs)
            Debug.Log("CautionStairsAudio: Sequence finished. Will repeat if still above plane.", this);
    }

    // Draws the raycast in the Scene view (only meaningful in raycast mode) so you can
    // visually confirm where the check is actually looking, independent of the console logs.
    private void OnDrawGizmosSelected()
    {
        Transform basis = xrCamera != null ? xrCamera : xrRig;
        if (basis == null) return;

        if (useBoundsCheckInsteadOfRaycast)
        {
            // Show exactly which point is being tested against the plane bounds.
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(basis.position, 0.05f);
            return;
        }

        Vector3 rayStart = basis.position + Vector3.up * raycastHeightOffset;
        Vector3 rayEnd = rayStart + Vector3.down * (maxRaycastDistance + raycastHeightOffset);
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(rayStart, rayEnd);
        Gizmos.DrawSphere(rayStart, 0.03f);
    }
}
