using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Persists per-localization-space room calibrations expressed as a list of
/// anchor-relative offsets. Each saved space has N anchor entries; each entry
/// records the virtualTwin's pose expressed in the local frame of that anchor.
///
/// On load, each recovered anchor produces an independent prediction of where
/// the room should be placed; the predictions are combined (median position +
/// median yaw) so a single drifting anchor cannot pull the whole room.
///
/// File: <persistentDataPath>/roomAnchorCalibration.json
/// </summary>
public static class RoomAnchorCalibrationStore
{
    public static string FilePath =>
        Path.Combine(Application.persistentDataPath, "roomAnchorCalibration.json");

    [System.Serializable]
    public class AnchorEntry
    {
        public string mapPosId;          // ML2 storage UUID for the anchor
        public Vector3 twinLocalPos;     // virtualTwin position in the anchor's local frame (legacy median path)
        public Quaternion twinLocalRot;  // virtualTwin rotation in the anchor's local frame (legacy median path)
        public Vector3 anchorWorldPos;   // anchor.transform.position at save time (Kabsch path)
    }

    [System.Serializable]
    public class ArucoOffset
    {
        public int markerId;            // ArUco marker ID this offset is anchored to
        public Vector3 twinLocalPos;    // virtualTwin position in the marker's local frame
        public Quaternion twinLocalRot; // virtualTwin rotation in the marker's local frame
        // Marker pose in the virtualTwin's local frame at save time. Used to
        // render a green ground-truth square at the marker's saved location
        // (parented to the twin, so it moves rigidly with the room). On reload
        // the green square should land exactly where the printed marker is —
        // visual proof that save/load is correct.
        public Vector3 markerInTwinLocalPos;
        public Quaternion markerInTwinLocalRot = Quaternion.identity;
    }

    [System.Serializable]
    public class SpaceCalib
    {
        public string spaceId;
        public string spaceName;
        public List<AnchorEntry> anchors = new List<AnchorEntry>();
        // virtualTwin world pose at save time. Combined with anchorWorldPos
        // entries, this lets the loader run a rigid-fit (Kabsch) on reload.
        public Vector3 roomWorldPos;
        public Quaternion roomWorldRot = Quaternion.identity;
        public bool hasWorldPose;        // false for old saves before this field existed
        // ArUco marker offsets. When any of these markers is currently visible the
        // ArUco snap overrides the spatial-anchor snap.
        public List<ArucoOffset> arucoOffsets = new List<ArucoOffset>();
    }

    [System.Serializable]
    private class Wrapper
    {
        public List<SpaceCalib> spaces = new List<SpaceCalib>();
    }

    public static SpaceCalib Get(string spaceId)
    {
        if (string.IsNullOrEmpty(spaceId)) return null;
        return LoadAll().Find(s => s.spaceId == spaceId);
    }

    public static void Save(SpaceCalib calib)
    {
        if (calib == null || string.IsNullOrEmpty(calib.spaceId)) return;
        var list = LoadAll();
        list.RemoveAll(s => s.spaceId == calib.spaceId);
        list.Add(calib);
        WriteAll(list);
    }

    public static void Remove(string spaceId)
    {
        if (string.IsNullOrEmpty(spaceId)) return;
        var list = LoadAll();
        if (list.RemoveAll(s => s.spaceId == spaceId) > 0)
            WriteAll(list);
    }

    static List<SpaceCalib> LoadAll()
    {
        if (!File.Exists(FilePath)) return new List<SpaceCalib>();
        try
        {
            var w = JsonUtility.FromJson<Wrapper>(File.ReadAllText(FilePath));
            return w?.spaces ?? new List<SpaceCalib>();
        }
        catch { return new List<SpaceCalib>(); }
    }

    static void WriteAll(List<SpaceCalib> list)
    {
        var w = new Wrapper { spaces = list };
        File.WriteAllText(FilePath, JsonUtility.ToJson(w, true));
    }
}
