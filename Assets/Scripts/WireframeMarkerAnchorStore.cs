using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// JSON-file-backed persistence for WireframeAlignment's per-marker anchor
/// mappings (spatial-anchor mapPosId -> ArUco marker ID), mirroring the
/// pattern used by RoomAnchorCalibrationStore for CalibrationManager.
///
/// Replaces the old PlayerPrefs-based storage, which is fragile (wiped on
/// app data clear, has a platform-dependent size cap, and isn't
/// human-inspectable for debugging).
///
/// File: <persistentDataPath>/wireframeArucoAnchorMappings.json
///
/// One-time migration: if no JSON file exists yet but the legacy PlayerPrefs
/// key from the old implementation is present, its contents are imported and
/// written out as the new file, so existing calibrations aren't lost on
/// upgrade.
/// </summary>
public static class WireframeMarkerAnchorStore
{
    public static string FilePath =>
        Path.Combine(Application.persistentDataPath, "wireframeArucoAnchorMappings.json");

    private const string LegacyPlayerPrefsKey = "ArucoToSpatialAnchorMappings";

    [System.Serializable]
    public class AnchorMapping
    {
        public string mapPosId;
        public ulong arucoID;
    }

    [System.Serializable]
    private class Wrapper
    {
        public List<AnchorMapping> mappings = new List<AnchorMapping>();
    }

    /// <summary>Load the full mapPosId -> arucoID table.</summary>
    public static Dictionary<string, ulong> LoadAll()
    {
        if (!File.Exists(FilePath))
        {
            var migrated = TryMigrateFromPlayerPrefs();
            if (migrated != null) return migrated;
            return new Dictionary<string, ulong>();
        }

        try
        {
            var w = JsonUtility.FromJson<Wrapper>(File.ReadAllText(FilePath));
            var dict = new Dictionary<string, ulong>();
            if (w?.mappings != null)
            {
                foreach (var m in w.mappings)
                {
                    if (!string.IsNullOrEmpty(m.mapPosId)) dict[m.mapPosId] = m.arucoID;
                }
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, ulong>();
        }
    }

    /// <summary>Overwrite the stored table with the given mappings.</summary>
    public static void SaveAll(Dictionary<string, ulong> mappings)
    {
        var w = new Wrapper
        {
            mappings = (mappings ?? new Dictionary<string, ulong>())
                .Select(kvp => new AnchorMapping { mapPosId = kvp.Key, arucoID = kvp.Value })
                .ToList()
        };
        File.WriteAllText(FilePath, JsonUtility.ToJson(w, true));
    }

    static Dictionary<string, ulong> TryMigrateFromPlayerPrefs()
    {
        if (!PlayerPrefs.HasKey(LegacyPlayerPrefsKey)) return null;
        try
        {
            string json = PlayerPrefs.GetString(LegacyPlayerPrefsKey);
            var w = JsonUtility.FromJson<Wrapper>(json);
            var dict = new Dictionary<string, ulong>();
            if (w?.mappings != null)
            {
                foreach (var m in w.mappings)
                {
                    if (!string.IsNullOrEmpty(m.mapPosId)) dict[m.mapPosId] = m.arucoID;
                }
            }

            if (dict.Count > 0)
            {
                SaveAll(dict);
                Debug.Log($"[Console] WIREFRAME-MIGRATE: imported {dict.Count} legacy PlayerPrefs anchor mapping(s) into {FilePath}");
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }
}
