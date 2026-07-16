using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CautionStairsAudio : MonoBehaviour
{
    [Header("Audio Settings")]
    [Tooltip("The audio clip to play when the XR rig is above a plane.")]
    public AudioClip warningAudio;
    
    [Tooltip("Delay in seconds before the audio repeats after finishing.")]
    public float repeatDelay = 2.0f;

    [Header("References")]
    [Tooltip("The XR Rig or Player Transform.")]
    public Transform xrRig;
    
    [Tooltip("List of planes (Colliders) representing the caution areas.")]
    public List<Collider> cautionPlanes = new List<Collider>();

    [Header("Detection Settings")]
    [Tooltip("How far down to cast a ray to check for the planes.")]
    public float maxRaycastDistance = 10f;
    
    [Tooltip("Check this if you want to use vertical bounding boxes instead of Raycasts. Useful if your planes are triggers or don't have physics enabled.")]
    public bool useBoundsCheckInsteadOfRaycast = false;

    private AudioSource audioSource;
    private bool isPlayingSequence = false;

    void Start()
    {
        // Automatically add and setup an AudioSource component
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.clip = warningAudio;
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f; // 0 is 2D (plays everywhere equally). Change to 1 for 3D spatial audio.
    }

    void Update()
    {
        // Check if we are currently above any of the specified planes
        bool isAbovePlane = CheckIfAboveCautionPlane();

        // If we are above a plane and not already in the middle of our audio/delay sequence, start it
        if (isAbovePlane && !isPlayingSequence)
        {
            StartCoroutine(PlayAudioSequence());
        }
    }

    private bool CheckIfAboveCautionPlane()
    {
        if (xrRig == null || cautionPlanes.Count == 0) return false;

        if (useBoundsCheckInsteadOfRaycast)
        {
            // Option A: Checks if the XR Rig is within the X/Z bounds of the colliders.
            // We use a more forgiving Y check in case the rig is at the bottom of the stairs or slightly above.
            Vector3 rigPos = xrRig.position;
            foreach (var plane in cautionPlanes)
            {
                if (plane == null) continue;

                Bounds bounds = plane.bounds;
                
                // Allow the rig to be anywhere from slightly below the minimum Y of the stairs to well above the maximum Y.
                if (rigPos.x >= bounds.min.x && rigPos.x <= bounds.max.x &&
                    rigPos.z >= bounds.min.z && rigPos.z <= bounds.max.z &&
                    rigPos.y >= bounds.min.y - 1.0f && rigPos.y <= bounds.max.y + 3.0f)
                {
                    return true;
                }
            }
        }
        else
        {
            // Option B (Default): Raycast down from slightly above the XR rig to see if any caution plane is directly below.
            // Start slightly above to ensure we don't start the raycast inside the collider if the rig is at Y=0.
            Vector3 rayStart = xrRig.position + Vector3.up * 0.5f;
            RaycastHit[] hits = Physics.RaycastAll(rayStart, Vector3.down, maxRaycastDistance + 0.5f);
            foreach (var hit in hits)
            {
                if (cautionPlanes.Contains(hit.collider))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private IEnumerator PlayAudioSequence()
    {
        isPlayingSequence = true;

        if (warningAudio != null)
        {
            audioSource.Play();
            
            // Wait for the audio clip to finish playing entirely
            yield return new WaitForSeconds(warningAudio.length);
        }

        // Wait for the set amount of seconds after the audio finishes
        yield return new WaitForSeconds(repeatDelay);

        // Sequence is finished. 
        // The Update loop will re-check if the XR Rig is still above a plane and restart this if true.
        isPlayingSequence = false;
    }
}
