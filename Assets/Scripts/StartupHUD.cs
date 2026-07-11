using UnityEngine;

public class StartupHUD : MonoBehaviour
{
    [Header("Scripts to Start")]
    [Tooltip("Drag the GameObject with the RandomSpawner script here.")]
    public RandomSpawner spawner;
    
    [Tooltip("Drag the GameObject with the EyeAndHeadTracker script here.")]
    public EyeAndHeadTracker tracker;

    [Header("UI Text Configuration")]
    [Tooltip("Drag your Main Text object here (Text (TMP)).")]
    public GameObject mainInstructionsObject;
    
    [TextArea(2, 4)]
    [Tooltip("The text to display on the main HUD.")]
    public string mainInstructionsMessage = "Select begin after the all markers scanned";

    [Tooltip("Drag the Text object that is a child of your Button here.")]
    public GameObject buttonTextObject;
    
    [Tooltip("The text to display on the button.")]
    public string buttonMessage = "Start Trial 1";

    private void Start()
    {
        // Automatically set the text when the game starts
        UpdateText(mainInstructionsObject, mainInstructionsMessage);
        UpdateText(buttonTextObject, buttonMessage);
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

        // Fallback to Legacy Text (since you used a Legacy Button)
        var legacy = obj.GetComponent<UnityEngine.UI.Text>();
        if (legacy != null) 
        { 
            legacy.text = msg; 
            return; 
        }
    }

    /// <summary>
    /// Link this method to your UI Button's OnClick() event.
    /// </summary>
    public void OnStartButtonClicked()
    {
        Debug.Log("StartupHUD: Start button clicked! Initializing scene...");

        if (spawner != null)
        {
            spawner.SpawnObjects();
        }
        else
        {
            Debug.LogWarning("StartupHUD: RandomSpawner is not assigned!");
        }

        if (tracker != null)
        {
            tracker.ResumeRecording();
        }
        else
        {
            Debug.LogWarning("StartupHUD: EyeAndHeadTracker is not assigned!");
        }

        // Hide this Canvas HUD instead of destroying it, so it can be brought back later if needed
        gameObject.SetActive(false);
    }
}
