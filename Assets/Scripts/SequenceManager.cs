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
    [Tooltip("Drag your 4 Sequence Buttons here.")]
    public GameObject[] sequenceButtons; 

    [Tooltip("Drag the Text child of each sequence button into this array.")]
    public GameObject[] sequenceButtonTexts;

    [Header("Start Button")]
    [Tooltip("Drag your dedicated Start Trial button here.")]
    public GameObject startButton;
    [Tooltip("Drag the Text child of the Start button here.")]
    public GameObject startButtonText;

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
                    btn.onClick.AddListener(() => SelectSequence(index));
                }
                else
                {
                    Debug.LogWarning($"SequenceManager: Could not find Button component for Sequence Button {i}.");
                }
            }
        }

        // Set up Start button
        if (startButton != null)
        {
            var sBtn = startButton.GetComponent<UnityEngine.UI.Button>();
            if (sBtn == null) sBtn = startButton.GetComponentInParent<UnityEngine.UI.Button>(true);
            if (sBtn != null)
            {
                sBtn.onClick.RemoveAllListeners();
                sBtn.onClick.AddListener(() => OnStartButtonClicked());
            }
            startButton.SetActive(false); // Hide until needed
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
        if (currentSequenceIndex != -1) return; // Prevent double-fire on sequence selection

        currentSequenceIndex = index;
        currentTrialIndex = 0;
        sequenceComplete = false;

        // Hide ALL sequence buttons
        for (int i = 0; i < sequenceButtons.Length; i++)
        {
            if (sequenceButtons[i] != null)
            {
                var btn = sequenceButtons[i].GetComponent<UnityEngine.UI.Button>();
                if (btn == null) btn = sequenceButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);
                
                if (btn != null) btn.gameObject.SetActive(false);
                else sequenceButtons[i].SetActive(false); // Fallback
            }
        }
        
        // Ensure start button is hidden
        if (startButton != null) startButton.SetActive(false);

        // Start Tutorial Phase instead of going straight to the first trial
        StartTutorialPhase();
    }

    private void StartTutorialPhase()
    {
        isTutorialPhase = true;
        gameObject.SetActive(false); // Hide HUD during tutorial

        if (ArucoMarkerManager.Instance != null)
            ArucoMarkerManager.Instance.DestroyMarkerTrackers();

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
            tracker.StartNewTrialRecording("Tutorial");
        }
    }

    private void UpdateMenuForNextTrial()
    {
        gameObject.SetActive(true); // Show HUD

        if (currentTrialIndex >= 4)
        {
            sequenceComplete = true;
            ShowMenu($"Sequence {currentSequenceIndex + 1} Complete!\nPlease close the application or restart.");
            if (startButton != null) startButton.SetActive(false);
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

        if (startButton != null)
        {
            startButton.SetActive(true);
            SetButtonTextSingle(startButtonText, startButton, $"Start Trial {currentTrialIndex + 1}");
            SetButtonInteractable(startButton, true);
        }
    }

    private void OnStartButtonClicked()
    {
        if (sequenceComplete || currentSequenceIndex == -1) return;
        if (!gameObject.activeSelf) return; // Anti-double-fire guard!

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
            tracker.StartNewTrialRecording($"Trial_{currentTrialIndex + 1}");
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

    private void SetButtonInteractable(GameObject buttonObj, bool interactable)
    {
        if (buttonObj == null) return;
        var btn = buttonObj.GetComponent<UnityEngine.UI.Button>();
        if (btn == null) btn = buttonObj.GetComponentInParent<UnityEngine.UI.Button>(true);
        if (btn != null) btn.interactable = interactable;
    }

    private void SetButtonTextSingle(GameObject textObj, GameObject fallbackObj, string msg)
    {
        GameObject target = textObj != null ? textObj : fallbackObj;
        if (target == null) return;

        var tmp = target.GetComponent<TMPro.TMP_Text>();
        if (tmp == null) tmp = target.GetComponentInChildren<TMPro.TMP_Text>(true);
        if (tmp != null) { tmp.text = msg; return; }

        var legacy = target.GetComponent<UnityEngine.UI.Text>();
        if (legacy == null) legacy = target.GetComponentInChildren<UnityEngine.UI.Text>(true);
        if (legacy != null) { legacy.text = msg; return; }
    }
}
