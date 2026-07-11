using UnityEngine;
using TMPro;
using System.Collections.Generic;

public class SequenceManager : MonoBehaviour
{
    [Header("Script References")]
    public RandomSpawner spawner;
    public EyeAndHeadTracker tracker;
    [Tooltip("Drag the Gem prefab here to spawn during the Tutorial Phase")]
    public GameObject tutorialTargetPrefab;

    [Header("UI References")]
    public GameObject mainInstructionsObject;
    
    [Header("Sequence Buttons")]
    [Tooltip("Drag your 4 Sequence Buttons here. Button Element 0 will transform into the Start button automatically.")]
    public GameObject[] sequenceButtons; 

    [Tooltip("Drag the Text child of each button into this array (Element 0 must be the text for Sequence Button 1).")]
    public GameObject[] sequenceButtonTexts;

    [Header("Pool 9Baseline Objects")]
    public GameObject pool1_9Baseline;
    public GameObject pool2_9Baseline;
    public GameObject pool3_9Baseline;
    public GameObject pool4_9Baseline;

    // Internal State
    private int currentSequenceIndex = -1;
    private int currentTrialIndex = 0;
    private bool sequenceComplete = false;
    private bool isTutorialPhase = false;

    // Hardcoded Sequences based on your prompt
    // True = Wireframe ON. False = Wireframe OFF.
    // Index 0,1 are Dusk. Index 2,3 are Night.
    private int[,] sequencePools = new int[4, 4] {
        { 1, 2, 3, 4 }, // Sequence 1
        { 2, 1, 3, 4 }, // Sequence 2
        { 1, 2, 4, 3 }, // Sequence 3
        { 2, 1, 4, 3 }  // Sequence 4
    };

    private bool[,] sequenceWireframes = new bool[4, 4] {
        { false, true, false, true }, // Sequence 1
        { true, false, false, true }, // Sequence 2
        { false, true, true, false }, // Sequence 3
        { true, false, true, false }  // Sequence 4
    };

    private void Start()
    {
        if (spawner != null) spawner.spawnOnAwake = false;
        if (tracker != null) tracker.recordOnAwake = false;
        if (tracker != null) tracker.OnAllTargetsDestroyed.AddListener(OnTrialFinished);

        // Set up the unified button clicks in code and initialize their names
        for (int i = 0; i < sequenceButtons.Length; i++)
        {
            if (sequenceButtons[i] != null)
            {
                int index = i; // local copy for closure
                
                // Name the buttons "Sequence 1", "Sequence 2", etc.
                SetButtonText(index, $"Sequence {index + 1}");

                var btn = sequenceButtons[i].GetComponent<UnityEngine.UI.Button>();
                if (btn == null) btn = sequenceButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);

                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(() => OnUnifiedButtonClicked(index));
                }
                else
                {
                    Debug.LogWarning($"SequenceManager: Could not find Button component for Sequence Button {i}.");
                }
            }
        }

        ShowMenu("Please scan all ArUco Markers and Select a Sequence to start.");
    }

    private void OnUnifiedButtonClicked(int buttonIndex)
    {
        if (currentSequenceIndex == -1)
        {
            // 1. FIRST CLICK: We are picking a sequence
            SelectSequence(buttonIndex);
        }
        else
        {
            // 2. SUBSEQUENT CLICKS: Only Button 0 is visible and it acts as the Start button
            if (buttonIndex == 0)
            {
                OnStartButtonClicked();
            }
        }
    }

    private void SelectSequence(int index)
    {
        currentSequenceIndex = index;
        currentTrialIndex = 0;
        sequenceComplete = false;

        // Hide buttons 1, 2, and 3 (leave Button 0 visible)
        for (int i = 1; i < sequenceButtons.Length; i++)
        {
            if (sequenceButtons[i] != null)
            {
                var btn = sequenceButtons[i].GetComponent<UnityEngine.UI.Button>();
                if (btn == null) btn = sequenceButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);
                
                if (btn != null) btn.gameObject.SetActive(false);
                else sequenceButtons[i].SetActive(false); // Fallback
            }
        }
        // Start Tutorial Phase instead of going straight to the first trial
        StartTutorialPhase();
    }

    private void StartTutorialPhase()
    {
        isTutorialPhase = true;
        gameObject.SetActive(false); // Hide HUD during tutorial

        if (tutorialTargetPrefab != null)
        {
            Transform userTransform = Camera.main != null ? Camera.main.transform : transform;
            Vector3 userPos = userTransform.position;

            // Spawn 4 targets in a cross pattern around the user at 1.5m
            Vector3[] offsets = new Vector3[]
            {
                new Vector3(0, 0, 1.5f),   // Front
                new Vector3(0, 0, -1.5f),  // Back
                new Vector3(1.5f, 0, 0),   // Right
                new Vector3(-1.5f, 0, 0)   // Left
            };

            foreach (var offset in offsets)
            {
                Vector3 spawnPos = userPos + (userTransform.rotation * offset);
                spawnPos.y = userPos.y; // Keep at eye level

                GameObject t = Instantiate(tutorialTargetPrefab, spawnPos, Quaternion.identity);
                t.transform.LookAt(userPos);
                t.transform.Rotate(90f, 0f, 0f, Space.Self); 
                
                t.tag = "DwellDestroyTarget";
                
                // Force into the layer the tracker uses (grabbed from spawner if possible)
                if (spawner != null)
                {
                    int layerId = LayerMask.NameToLayer(spawner.targetLayer);
                    if (layerId > -1) t.layer = layerId;
                }
            }
        }
        else
        {
            Debug.LogError("SequenceManager: Cannot start tutorial phase because tutorialTargetPrefab is missing!");
            isTutorialPhase = false;
            UpdateMenuForNextTrial();
            return;
        }

        if (tracker != null)
        {
            tracker.RefreshTargetList();
            // Optional: You could call tracker.ResumeRecording() here if you want to record the tutorial
        }
    }

    private void UpdateMenuForNextTrial()
    {
        gameObject.SetActive(true); // Show HUD

        if (currentTrialIndex >= 4)
        {
            sequenceComplete = true;
            ShowMenu($"Sequence {currentSequenceIndex + 1} Complete!\nPlease close the application or restart.");
            SetButtonText(0, "Done");
            SetFirstButtonInteractable(false);
            if (tracker != null) tracker.PauseRecording();
            return;
        }

        string timeOfDay = (currentTrialIndex < 2) ? "Dusk" : "Night";
        int poolNum = sequencePools[currentSequenceIndex, currentTrialIndex];

        if (currentTrialIndex == 0)
        {
            ShowMenu($"Sequence {currentSequenceIndex + 1} Selected.\n\nPlease wait until the researcher approves Trial 1 ({timeOfDay} - Pool {poolNum}).");
        }
        else
        {
            ShowMenu($"Trial {currentTrialIndex} Complete!\n\nPlease wait until the researcher approves the next trial:\nTrial {currentTrialIndex + 1} ({timeOfDay} - Pool {poolNum}).");
        }

        SetButtonText(0, $"Start Trial {currentTrialIndex + 1}");
        SetFirstButtonInteractable(true);
    }

    private void OnStartButtonClicked()
    {
        if (sequenceComplete || currentSequenceIndex == -1) return;

        gameObject.SetActive(false); // Hide HUD

        int poolNum = sequencePools[currentSequenceIndex, currentTrialIndex];
        bool useWireframe = sequenceWireframes[currentSequenceIndex, currentTrialIndex];
        string timeOfDay = (currentTrialIndex < 2) ? "Dusk" : "Night";

        // 1. Clear old objects and spawn new ones
        if (spawner != null)
        {
            spawner.DestroyAllSpawnedObjects();
            spawner.selectedPool = (RandomSpawner.PoolSelection)(poolNum - 1); 
            spawner.SpawnObjects();
        }

        // 2. Set Wireframes
        SetWireframeActive(pool1_9Baseline, false);
        SetWireframeActive(pool2_9Baseline, false);
        SetWireframeActive(pool3_9Baseline, false);
        SetWireframeActive(pool4_9Baseline, false);

        if (poolNum == 1) SetWireframeActive(pool1_9Baseline, useWireframe);
        if (poolNum == 2) SetWireframeActive(pool2_9Baseline, useWireframe);
        if (poolNum == 3) SetWireframeActive(pool3_9Baseline, useWireframe);
        if (poolNum == 4) SetWireframeActive(pool4_9Baseline, useWireframe);

        // 3. Start JSON Tracker and log marker
        if (tracker != null)
        {
            tracker.RefreshTargetList();
            tracker.ResumeRecording();
            string wireframeText = useWireframe ? "Wireframe" : "Zero Wireframe";
            tracker.LogMarker($"Trial {currentTrialIndex + 1} ({timeOfDay}): Pool {poolNum} + {wireframeText}");
        }
    }

    private void OnTrialFinished()
    {
        if (isTutorialPhase)
        {
            isTutorialPhase = false;
            UpdateMenuForNextTrial(); // Show the menu for Trial 1
            return;
        }

        if (tracker != null)
        {
            tracker.LogMarker($"Trial {currentTrialIndex + 1} Ended.");
            tracker.PauseRecording();
        }

        if (spawner != null) spawner.DestroyAllSpawnedObjects();

        currentTrialIndex++;
        UpdateMenuForNextTrial();
    }

    private void SetWireframeActive(GameObject baselineObj, bool active)
    {
        if (baselineObj != null)
        {
            MeshRenderer renderer = baselineObj.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.enabled = active;
        }
    }

    private void ShowMenu(string msg)
    {
        gameObject.SetActive(true);
        if (mainInstructionsObject != null)
        {
            var tmp = mainInstructionsObject.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (tmp != null) { tmp.text = msg; return; }

            var legacy = mainInstructionsObject.GetComponentInChildren<UnityEngine.UI.Text>(true);
            if (legacy != null) { legacy.text = msg; return; }
        }
    }

    private void SetButtonText(int index, string msg)
    {
        if (index < 0 || index >= sequenceButtons.Length) return;

        GameObject textObj = null;
        
        // Use explicit reference if provided, otherwise fallback to finding it dynamically
        if (sequenceButtonTexts != null && sequenceButtonTexts.Length > index && sequenceButtonTexts[index] != null)
        {
            textObj = sequenceButtonTexts[index];
        }
        else if (sequenceButtons != null && sequenceButtons.Length > index && sequenceButtons[index] != null)
        {
            textObj = sequenceButtons[index];
        }
        
        if (textObj == null) return;

        var tmp = textObj.GetComponent<TMPro.TMP_Text>();
        if (tmp == null) tmp = textObj.GetComponentInChildren<TMPro.TMP_Text>(true);
        if (tmp != null) { tmp.text = msg; return; }

        var legacy = textObj.GetComponent<UnityEngine.UI.Text>();
        if (legacy == null) legacy = textObj.GetComponentInChildren<UnityEngine.UI.Text>(true);
        if (legacy != null) { legacy.text = msg; return; }
    }

    private void SetFirstButtonInteractable(bool interactable)
    {
        if (sequenceButtons.Length == 0 || sequenceButtons[0] == null) return;
        var btn = sequenceButtons[0].GetComponent<UnityEngine.UI.Button>();
        if (btn == null) btn = sequenceButtons[0].GetComponentInParent<UnityEngine.UI.Button>(true);
        if (btn != null) btn.interactable = interactable;
    }
}
