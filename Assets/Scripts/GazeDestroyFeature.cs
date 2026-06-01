using System.Linq;
using UnityEngine;
using Logger = LearnXR.Core.Logger;

public class GazeDestroyFeature : MonoBehaviour
{
    [SerializeField] private LayerMask layersToIncludeWithRay;
    
    // Set to 4.0f to meet the 4-second requirement
    [SerializeField] [Range(1.0f, 10.0f)] private float minGazeTimeOverTarget = 4.0f;

    // private variables
    private MeshRenderer[] targetRenderers;
    private float gazeOverTargetTracker;
    
    // Reusing the exact shader properties from your provided code
    private readonly int fillProgressProperty = Shader.PropertyToID("_FillProgress");
    private const float MIN_FILL_RANGE = -0.6f;
    private const float MAX_FILL_RANGE = 0.6f;
    
    private void Awake()
    {
        // We will find all 8 prefabs by using a custom Unity Tag
        var allTargets = GameObject.FindGameObjectsWithTag("GazeDestroyTarget");
        targetRenderers = allTargets
            .Select(n => n.GetComponent<MeshRenderer>())
            .ToArray();
    }
    
    void Update()
    {
        // Uses the exact permission and tracking check from your GazeInputManager
        if (!GazeInputManager.Instance.EyeTrackingPermissionGranted) return;
        
        var gazePosition = GazeInputManager.Instance.GazePosition;
        var gazeRotation = GazeInputManager.Instance.GazeRotation;

        // Cast a ray from the eyes
        if (Physics.Raycast(gazePosition, gazeRotation * Vector3.forward, out RaycastHit hitInfo, 10.0f, layersToIncludeWithRay))
        {
            var renderer = hitInfo.collider.GetComponent<MeshRenderer>();
            
            // Check if we hit one of our 8 prefabs and it hasn't been destroyed yet
            if (renderer != null && targetRenderers.Contains(renderer))
            {
                gazeOverTargetTracker += Time.deltaTime;
                
                // Drive the same shader effect visually
                float progress = gazeOverTargetTracker / minGazeTimeOverTarget;
                float fillAmount = ConvertPercentageToRange(progress);
                renderer.material.SetFloat(fillProgressProperty, fillAmount);
                
                ClearAllFillings(renderer.gameObject);
                
                // Destroy the prefab after 4 seconds of continuous gaze
                if (gazeOverTargetTracker >= minGazeTimeOverTarget)
                {
                    Logger.Instance.LogInfo($"{renderer.gameObject.name} gazed at for 4 seconds - Destroying...");
                    
                    // Remove the destroyed object from our tracking array to prevent errors
                    targetRenderers = targetRenderers.Where(r => r != renderer).ToArray();
                    
                    // Delete the prefab
                    Destroy(renderer.gameObject);
                    
                    // Reset the gaze tracker for the next object
                    gazeOverTargetTracker = 0;
                }
            }
            else
            {
                gazeOverTargetTracker = 0;
                ClearAllFillings();
            }
        }
        else
        {
            gazeOverTargetTracker = 0;
            ClearAllFillings();
        }
    }
    
    // Exact same math used to convert the 0-100% time into the shader's valid range
    private float ConvertPercentageToRange(float percentage)
    {
        float rangeSize = MAX_FILL_RANGE - (MIN_FILL_RANGE);
        float convertedValue = (percentage * rangeSize) - MAX_FILL_RANGE;
        return convertedValue;
    }

    // Zeroes out the shader for any object we look away from
    private void ClearAllFillings(GameObject gameObjectToExclude = null)
    {
        float zeroPercent = ConvertPercentageToRange(0);
        foreach (var currentRenderer in targetRenderers)
        {
            // Added a null check here just in case Unity cleans up a destroyed object slowly
            if (currentRenderer != null && currentRenderer.gameObject != gameObjectToExclude)
            {
                currentRenderer.material.SetFloat(fillProgressProperty, zeroPercent);
            }
        }
    }
}