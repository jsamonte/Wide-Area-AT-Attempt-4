using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Authoring-time exporter for the per-pool layout descriptives reported in the paper
/// (Table "tab:pools").
///
/// WHY THIS EXISTS. RandomSpawner places objects by dart throwing with an exclusion radius
/// that RELAXES on demand (RandomSpawner.cs, the maxFailsBeforeDistanceReduction loop), so
/// the spacing a layout actually achieves is a property of that layout rather than a bound
/// the algorithm guarantees. Pool is a design factor in the counterbalancing, which means
/// the four layouts have to be shown comparable in dispersion rather than assumed to be.
/// That is a measurement, and this writes it.
///
/// It also dumps every spawned position, so the layouts are archived as data rather than
/// existing only as a seed plus a mesh that could later be re-cleaned.
///
/// WHAT IT DOES NOT DO: save the scene. It spawns each pool in turn, measures, clears up,
/// and restores the spawner's original selectedPool. The scene is left dirty because
/// selectedPool was written and reverted; do not save it, just undo or reload.
/// </summary>
public static class LayoutStatsExporter
{
    private const string StatsPath = "analysis/design/layout_stats.csv";
    private const string PositionsPath = "analysis/design/layout_positions.csv";

    [MenuItem("Tools/Study/Export Layout Stats (all pools)")]
    public static void ExportStatsOnly() => Run(withIdealPaths: false);

    [MenuItem("Tools/Study/Export Layout Stats + Ideal Paths (all pools, slow)")]
    public static void ExportStatsAndPaths() => Run(withIdealPaths: true);

    private static void Run(bool withIdealPaths)
    {
        RandomSpawner spawner = FindSceneSpawner();
        if (spawner == null)
        {
            Debug.LogError("LayoutStatsExporter: no RandomSpawner found in any loaded scene. Open Application.unity first.");
            return;
        }

        var calculator = spawner.GetComponent<GemOptimalPathCalculator>();
        if (withIdealPaths && calculator == null)
        {
            Debug.LogWarning("LayoutStatsExporter: no GemOptimalPathCalculator on the spawner; ideal paths will be skipped.");
        }

        // Restored in the finally block: this menu item must not leave the study scene
        // pointing at a different pool than the operator left it on.
        RandomSpawner.PoolSelection originalPool = spawner.selectedPool;

        var statRows = new List<string>();
        var posRows = new List<string>();

        try
        {
            // Geometry is a property of the walkable mesh, not of any one pool, so it is
            // measured once and repeated across rows for readability in the paper table.
            float bboxArea = BoundingFootprintArea(spawner.targetPlanes);
            float meshArea = WalkableMeshArea(spawner.targetPlanes);

            foreach (RandomSpawner.PoolSelection pool in System.Enum.GetValues(typeof(RandomSpawner.PoolSelection)))
            {
                int poolNum = (int)pool + 1;
                EditorUtility.DisplayProgressBar("Exporting layout stats", $"Pool {poolNum}", poolNum / 4f);

                // Diff the children rather than reading them all: the spawner GameObject also
                // parents the legacy placeholder transforms, which are neither targets nor
                // recall objects and would otherwise be counted as spawned output.
                var before = new HashSet<Transform>(spawner.transform.Cast<Transform>());

                spawner.selectedPool = pool;
                spawner.SpawnObjects();

                var spawned = spawner.transform.Cast<Transform>().Where(t => !before.Contains(t)).ToList();

                var targets = new List<Vector3>();
                var recall = new List<Vector3>();
                foreach (Transform t in spawned)
                {
                    // GazeLoggableObject.category is what RandomSpawner itself stamps, so it
                    // is the authoritative split. Tag is the fallback for anything that
                    // predates that component.
                    var loggable = t.GetComponent<GazeLoggableObject>();
                    string category = loggable != null ? loggable.category : (t.gameObject.tag == "DwellDestroyTarget" ? "Target" : "RecallObject");

                    if (category == "Target") targets.Add(t.position);
                    else recall.Add(t.position);

                    posRows.Add(string.Join(",", new[]
                    {
                        poolNum.ToString(CultureInfo.InvariantCulture),
                        category,
                        Csv(t.name),
                        F(t.position.x), F(t.position.y), F(t.position.z)
                    }));
                }

                var all = targets.Concat(recall).ToList();
                float[] nn = NearestNeighbourDistancesXZ(all);

                int n = all.Count;
                float r0 = n > 0 ? Mathf.Sqrt(bboxArea / n) * 0.7f : 0f;

                statRows.Add(string.Join(",", new[]
                {
                    poolNum.ToString(CultureInfo.InvariantCulture),
                    spawner.ActiveSeed.HasValue ? spawner.ActiveSeed.Value.ToString(CultureInfo.InvariantCulture) : "",
                    targets.Count.ToString(CultureInfo.InvariantCulture),
                    recall.Count.ToString(CultureInfo.InvariantCulture),
                    F(nn.Length > 0 ? nn.Min() : 0f),
                    F(Median(nn)),
                    F(nn.Length > 0 ? nn.Average() : 0f),
                    F(nn.Length > 0 ? nn.Max() : 0f),
                    F(bboxArea),
                    F(meshArea),
                    F(r0),
                    F(spawner.spawnHeightOffset),
                    System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                }));

                if (withIdealPaths && calculator != null)
                {
                    // Writes its own analysis/design/ideal_paths.csv row, keyed by pool. It
                    // reads selectedPool off the spawner, which is currently this pool.
                    calculator.CalculateExactShortestPath();
                }

                spawner.DestroyAllSpawnedObjects();
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            spawner.selectedPool = originalPool;
            spawner.DestroyAllSpawnedObjects();
        }

        Write(StatsPath,
            "pool,seed,n_targets,n_recall,nn_min_m,nn_median_m,nn_mean_m,nn_max_m,bbox_footprint_m2,walkable_mesh_m2,initial_radius_m,spawn_height_offset_m,computed_utc",
            statRows);
        Write(PositionsPath, "pool,category,name,x,y,z", posRows);

        Debug.Log($"LayoutStatsExporter: wrote {StatsPath} and {PositionsPath}. " +
                  "The scene is dirty because selectedPool was set and reverted -- do NOT save it.");
        AssetDatabase.Refresh();
    }

    private static RandomSpawner FindSceneSpawner()
    {
        foreach (var s in Resources.FindObjectsOfTypeAll<RandomSpawner>())
        {
            // Excludes prefab assets and preview-stage copies, which have no valid scene.
            if (s.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(s)) return s;
        }
        return null;
    }

    /// <summary>Sum of the colliders' axis-aligned XZ footprints -- the quantity RandomSpawner
    /// itself uses to seed its exclusion radius. It over-states the walkable region whenever
    /// that region is not rectangular, which is why the true mesh area is reported beside it.</summary>
    private static float BoundingFootprintArea(List<Collider> planes)
    {
        float area = 0f;
        if (planes == null) return area;
        foreach (var p in planes)
        {
            if (p == null) continue;
            Vector3 size = p.bounds.size;
            area += size.x * size.z;
        }
        return area;
    }

    /// <summary>True walkable area, as the ground-plane projection of the collider mesh's
    /// triangles. Projected rather than measured on the surface, so it is comparable with the
    /// footprint above and with the planar path measures in the analysis.</summary>
    private static float WalkableMeshArea(List<Collider> planes)
    {
        float area = 0f;
        if (planes == null) return area;

        foreach (var p in planes)
        {
            var mc = p as MeshCollider;
            if (mc == null || mc.sharedMesh == null) continue;

            Mesh mesh = mc.sharedMesh;
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            Transform tf = mc.transform;

            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = tf.TransformPoint(verts[tris[i]]);
                Vector3 b = tf.TransformPoint(verts[tris[i + 1]]);
                Vector3 c = tf.TransformPoint(verts[tris[i + 2]]);

                // |cross| / 2 of the XZ projection: a vertical wall triangle projects to zero
                // area, which is the correct contribution to a walkable footprint.
                float cross = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
                area += Mathf.Abs(cross) * 0.5f;
            }
        }
        return area;
    }

    /// <summary>For each point, the ground-plane distance to its closest neighbour. Planar to
    /// match the exclusion test's practical effect on a walkable surface and the planar path
    /// measures used in the analysis.</summary>
    private static float[] NearestNeighbourDistancesXZ(List<Vector3> pts)
    {
        int n = pts.Count;
        if (n < 2) return new float[0];

        var result = new float[n];
        for (int i = 0; i < n; i++)
        {
            float best = float.MaxValue;
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                float dx = pts[i].x - pts[j].x;
                float dz = pts[i].z - pts[j].z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < best) best = d;
            }
            result[i] = best;
        }
        return result;
    }

    private static float Median(float[] values)
    {
        if (values.Length == 0) return 0f;
        var sorted = (float[])values.Clone();
        System.Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) * 0.5f;
    }

    private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

    private static string Csv(string s) => s.Contains(",") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static void Write(string relativePath, string header, List<string> rows)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string path = Path.Combine(projectRoot, relativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = new List<string> { header };
            lines.AddRange(rows);
            File.WriteAllLines(path, lines);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"LayoutStatsExporter: could not write {path} -- {e.Message}");
        }
    }
}
