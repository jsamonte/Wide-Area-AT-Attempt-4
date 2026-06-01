using System.IO;
using UnityEngine;
using UnityEngine.Android; // Required for runtime permission on Android/ML2

[System.Serializable]
public class GameData
{
    public string playerName;
    public int playerScore;
    public bool isGameOver;
}

public class JsonSaver : MonoBehaviour
{
    private string savePath;

    private void Awake()
    {
        // Safe persistent path on ANY device (including ML2)
        savePath = Path.Combine(Application.persistentDataPath, "gamesave.json");
        
        // Guarantee the folder exists
        Directory.CreateDirectory(Application.persistentDataPath);

        // ML2 = Android → request permission if needed
        if (Application.platform == RuntimePlatform.Android)
        {
            RequestStoragePermission();
        }
        else
        {
            SaveGameData();
        }
    }

    private void RequestStoragePermission()
    {
        if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageWrite))
        {
            Permission.RequestUserPermission(Permission.ExternalStorageWrite);
            // Small delay so permission dialog can be accepted
            Invoke(nameof(SaveGameData), 1.5f);
        }
        else
        {
            SaveGameData();
        }
    }

    public void SaveGameData()
    {
        try
        {
            GameData data = new GameData
            {
                playerName = "Alex",
                playerScore = 950,
                isGameOver = false
            };

            string json = JsonUtility.ToJson(data, true); // pretty-printed
            File.WriteAllText(savePath, json);

            Debug.Log($"✅ JSON SAVED SUCCESSFULLY!\nPath: {savePath}");
            Debug.Log($"📄 Content preview:\n{json}");

            // Immediate verification
            LoadAndLogGameData();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"❌ SAVE FAILED: {ex.Message}\nPath attempted: {savePath}");
        }
    }

    public void LoadAndLogGameData()
    {
        try
        {
            if (File.Exists(savePath))
            {
                string json = File.ReadAllText(savePath);
                GameData loaded = JsonUtility.FromJson<GameData>(json);
                Debug.Log($"📖 LOADED & VERIFIED → Name: {loaded.playerName} | Score: {loaded.playerScore} | Game Over: {loaded.isGameOver}");
            }
            else
            {
                Debug.LogWarning("⚠️ File does not exist yet.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"❌ LOAD ERROR: {ex.Message}");
        }
    }

    // Call this from a UI button, gaze event, or anywhere for manual testing
    public void TestSaveLoad()
    {
        SaveGameData();
    }
}