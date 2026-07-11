using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections.Generic;

public class TrialManager : MonoBehaviour
{
    [Header("Script References")]
    [Tooltip("Drag the Spawner object here")]
    public RandomSpawner spawner;
    [Tooltip("Drag the EyeAndHeadTracker object here")]
    public EyeAndHeadTracker tracker;

    [Header("Trial Configuration")]
    [Tooltip("Define the exact order of Pools you want to use for your trials.")]
    public List<RandomSpawner.PoolSelection> trialOrder = new List<RandomSpawner.PoolSelection>()
    {
        RandomSpawner.PoolSelection.Pool1,
        RandomSpawner.PoolSelection.Pool2,
        RandomSpawner.PoolSelection.Pool3,
        RandomSpawner.PoolSelection.Pool4
    };

    [Header("UI Text Configuration")]
    [Tooltip("Drag your Main Text object here (Text (TMP)).")]
    public GameObject mainInstructionsObject;
    
    [Tooltip("Drag the Text object that is a child of your Button here.")]
    public GameObject buttonTextObject;

    private int currentTrialIndex = 0;
    private bool studiesComplete = false;

    private void Start()
    {
        // Safety checks to ensure other scripts don't start automatically
        if (spawner != null) spawner.spawnOnAwake = false;
        if (tracker != null) tracker.recordOnAwake = false;

        // Register to listen for the end of a trial
        if (tracker != null)
        {
            tracker.OnAllTargetsDestroyed.AddListener(OnTrialFinished);
        }

        UpdateHUDForCurrentState();
    }

    private void UpdateHUDForCurrentState()
    {
        // Make sure HUD is visible
        gameObject.SetActive(true);

        if (currentTrialIndex >= trialOrder.Count)
        {
            // All trials complete!
            studiesComplete = true;
            UpdateText(mainInstructionsObject, "Studies complete.\nPlease close the application.");
            UpdateText(buttonTextObject, "Done");
            
            // Force a final JSON save just in case
            if (tracker != null) tracker.PauseRecording(); 
            return;
        }

        // Build the string to show the full order
        string orderString = "Trial Order: ";
        for (int i = 0; i < trialOrder.Count; i++)
        {
            orderString += trialOrder[i].ToString();
            if (i < trialOrder.Count - 1) orderString += " -> ";
        }

        string nextPool = trialOrder[currentTrialIndex].ToString();
        string mainMsg = "";

        if (currentTrialIndex == 0)
        {
            mainMsg = $"{orderString}\n\nClick Start to begin Trial 1 ({nextPool}).";
        }
        else
        {
            mainMsg = $"Trial {currentTrialIndex} Complete!\n\nNext: Trial {currentTrialIndex + 1} ({nextPool}).\nClick Start when ready.";
        }

        UpdateText(mainInstructionsObject, mainMsg);
        UpdateText(buttonTextObject, $"Start Trial {currentTrialIndex + 1}");
    }

    /// <summary>
    /// Link this method to your UI Button's OnClick() event.
    /// </summary>
    public void OnStartButtonClicked()
    {
        if (studiesComplete) return; // Do nothing if finished

        Debug.Log($"TrialManager: Starting Trial {currentTrialIndex + 1}");

        // 1. Hide the HUD
        gameObject.SetActive(false);

        // 2. Clear old objects if any exist
        if (spawner != null)
        {
            spawner.DestroyAllSpawnedObjects();
            
            // 3. Set the pool for this specific trial
            spawner.selectedPool = trialOrder[currentTrialIndex];
            
            // 4. Spawn!
            spawner.SpawnObjects();
        }

        // 5. Tell the tracker to find the new targets and start recording
        if (tracker != null)
        {
            tracker.RefreshTargetList();
            tracker.ResumeRecording();
        }
    }

    private void OnTrialFinished()
    {
        Debug.Log($"TrialManager: Trial {currentTrialIndex + 1} finished.");

        // 1. Pause the JSON recording (and save it)
        if (tracker != null)
        {
            tracker.PauseRecording();
        }

        // 2. Instantly clean up the room
        if (spawner != null)
        {
            spawner.DestroyAllSpawnedObjects();
        }

        // 3. Increment trial and show HUD again
        currentTrialIndex++;
        UpdateHUDForCurrentState();
    }

    private void UpdateText(GameObject obj, string msg)
    {
        if (obj == null) return;
        
        // Try TextMeshPro first
        var tmp = obj.GetComponent<TMPro.TMP_Text>();
        if (tmp != null) 
        { 
            tmp.text = msg; 
            return; 
        }

        // Fallback to Legacy Text
        var legacy = obj.GetComponent<UnityEngine.UI.Text>();
        if (legacy != null) 
        { 
            legacy.text = msg; 
            return; 
        }
    }
}
