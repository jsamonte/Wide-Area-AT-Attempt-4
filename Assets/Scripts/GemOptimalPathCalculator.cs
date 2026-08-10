using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
#if UNITY_EDITOR
using System.IO;
#endif

/// <summary>
/// Authoring-time calculator for a pool's ideal collection route.
///
/// The 20 targets in a pool are identical and all present from the start, so a participant
/// may collect them in any order. The shortest possible route is therefore an OPEN
/// travelling-salesman tour -- free start, free end, every target visited once -- which is
/// what Held-Karp computes exactly below. That number is the denominator of the locomotion
/// efficiency ratio reported in the analysis; the numerator is the participant's measured
/// head path (head_path_xz_m in analysis/R/02_reduce_gaze.R).
///
/// WHY IT WRITES A FILE. Logging to the console made this a number someone had to read off
/// the screen and retype, which is exactly the kind of hand-carried constant the rest of the
/// pipeline exists to avoid. It now writes analysis/design/ideal_paths.csv, keyed by pool,
/// and 12_join.R reads it. Rerunning for a pool overwrites that pool's row and nothing else.
///
/// WORKFLOW, per pool: set RandomSpawner.selectedPool -> "Preview Spawn In Editor" ->
/// "Calculate Exact Shortest Path" on this component. Repeat for all four pools.
///
/// DISTANCE MODE. StraightLine measures point-to-point and therefore passes through buildings
/// and ignores stairs -- a lower bound on any route a participant could actually walk, not a
/// navigable one. WalkableNavMesh instead measures along a baked NavMesh, so the route respects
/// building footprints and follows ramps and stairs at their true slope. Held-Karp is agnostic
/// to where the distances come from; only the matrix changes.
///
/// LIMIT THAT SURVIVES EITHER MODE, which the paper states rather than hides: the tour assumes
/// the target positions are known in advance, which the participant's are not -- search is the
/// task. The ratio is therefore a relative efficiency index comparable ACROSS conditions on the
/// same pool, never an absolute claim that a participant walked N times further than they needed
/// to. Switching to WalkableNavMesh removes the straight-line caveat and nothing else, so the
/// methodology text must be updated to match whichever mode produced the shipped CSV.
/// </summary>
public class GemOptimalPathCalculator : MonoBehaviour
{
    public enum DistanceMode
    {
        /// <summary>Euclidean point-to-point. Ignores walkability entirely.</summary>
        StraightLine,
        /// <summary>Geodesic distance along a baked NavMesh.</summary>
        WalkableNavMesh,
        /// <summary>Shortest path across a grid sampled directly from the spawn colliders.</summary>
        WalkableGrid
    }

    [Header("How distance between targets is measured")]
    [Tooltip("StraightLine ignores walls and stairs. WalkableNavMesh measures along a baked NavMesh, so the tour only uses ground a participant could actually walk. Requires a bake: mark the walkable surfaces Navigation Static, then Window > AI > Navigation > Bake.")]
    [SerializeField] private DistanceMode distanceMode = DistanceMode.StraightLine;

    [Tooltip("How far a target may sit from the NavMesh and still be snapped onto it. Targets are spawned slightly above the floor and then raised so their base meets the ground, so this must exceed the tallest target's offset. Too large and a target on an unreachable island snaps silently to the wrong surface.")]
    [SerializeField] private float navMeshSampleRadius = 3f;

    [Header("Walkable grid (WalkableGrid mode)")]
    [Tooltip("Grid resolution in metres. Smaller is more accurate and much slower: cost grows with the square. 0.5 m over a 170 m site is ~115,000 cells and solves in seconds.")]
    [SerializeField] private float gridCellSize = 0.5f;

    [Tooltip("Largest height difference between neighbouring cells that still counts as connected. Stops the route stepping between a raised area and the ground beside it, while staying above a stair riser so real stairs remain traversable.")]
    [SerializeField] private float maxStepHeight = 0.5f;

    [Tooltip("Safety ceiling on total cells. A bounds/cell-size combination that exceeds this aborts rather than freezing the editor for minutes.")]
    [SerializeField] private int maxGridCells = 4000000;

    [Tooltip("The WalkableAreaSet holding surfaces a participant may walk across but on which nothing spawns. Left empty, one is found automatically in the scene. Those surfaces are unioned with RandomSpawner's Target Planes to form the region a route may cross -- never the region objects spawn in.")]
    [SerializeField] private WalkableAreaSet walkOnlySurfaces;

    [Header("Which children count as targets")]
    [Tooltip("Only children carrying this tag are treated as targets. RandomSpawner tags targets 'DwellDestroyTarget' and leaves recall objects Untagged, so this is what keeps the 15 recall objects out of the tour (and keeps the count under the Held-Karp ceiling).")]
    [SerializeField] private string targetTag = "DwellDestroyTarget";

    [Tooltip("If no tagged child is found, fall back to treating every direct child as a target. Off by default: a silent fallback would compute a tour over the wrong object set and still write a number to the CSV.")]
    [SerializeField] private bool fallbackToAllChildren = false;

    [Header("CSV export")]
    [Tooltip("Pool this layout belongs to. Ignored if a RandomSpawner on this GameObject already says which pool is selected.")]
    [SerializeField, Range(1, 4)] private int poolOverride = 1;

    [Tooltip("Written relative to the project root (the folder containing Assets/). The analysis pipeline reads exactly this path.")]
    [SerializeField] private string csvRelativePath = "analysis/design/ideal_paths.csv";

    [Tooltip("Off computes and logs the tour without touching the CSV. Useful when checking a layout you are not committing to.")]
    [SerializeField] private bool writeCsv = true;

    // Held-Karp is O(n^2 * 2^n) in time and allocates a float[2^n, n] table. 20 targets is
    // ~1-2 s and ~160 MB; past 22 the editor stalls long enough to look like a hang.
    private const int MaxExactNodes = 22;

    [ContextMenu("Calculate Exact Shortest Path")]
    public void CalculateExactShortestPath()
    {
        List<Transform> gemList = CollectTargets();
        int n = gemList.Count;

        if (n < 2)
        {
            Debug.LogWarning($"GemOptimalPathCalculator: found {n} target(s) under '{name}'. " +
                             $"Spawn a pool first (RandomSpawner -> Preview Spawn In Editor).");
            return;
        }

        if (n > MaxExactNodes)
        {
            Debug.LogError($"Too many targets ({n}). Exact calculation is mathematically too intensive for >{MaxExactNodes} nodes on the main thread.");
            return;
        }

        Debug.Log($"Calculating absolute shortest path for {n} targets... (this might freeze the editor for a second)");

        // 1. Gather positions and precalculate distances.
        // The tour is solved in the ground plane, because the analysis numerator it feeds
        // (head_path_xz_m) is also planar: a head-mounted sensor at 60 Hz accumulates real
        // vertical travel from every step and head bob, which is locomotion noise rather than
        // route choice. The same tour's 3D length is reported alongside so the pairing can be
        // redone in 3D later without re-solving.
        Vector2[] pos = new Vector2[n];
        Vector3[] pos3 = new Vector3[n];
        Transform[] gems = new Transform[n];
        for (int i = 0; i < n; i++)
        {
            gems[i] = gemList[i];
            pos3[i] = gems[i].position;
            pos[i] = new Vector2(pos3[i].x, pos3[i].z);
        }

        // In WalkableNavMesh mode the tour is solved over on-mesh positions, so the targets
        // themselves are snapped down onto walkable ground first. pos3 is left alone: it is
        // what the straight-line comparison and the 3D report are measured from.
        Vector3[] navPos3 = pos3;
        if (distanceMode == DistanceMode.WalkableNavMesh)
        {
            EnsureNavMeshRegistered();
            if (!SnapToNavMesh(pos3, gems, out navPos3)) return;
        }

        float[,] dist;
        // Only the grid mode produces a 3D length as a by-product of the same search. The
        // other two recover it after the tour is known, from the pairs actually visited.
        float[,] gridDist3d = null;

        if (distanceMode == DistanceMode.WalkableGrid)
        {
            if (!BuildGridDistanceMatrix(pos3, gems, out dist, out gridDist3d)) return;
        }
        else
        {
            dist = new float[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (i == j) { dist[i, j] = 0f; continue; }

                    if (distanceMode == DistanceMode.StraightLine)
                    {
                        dist[i, j] = Vector2.Distance(pos[i], pos[j]);
                    }
                    else
                    {
                        // A pair the NavMesh cannot connect is an authoring error, not a long
                        // route: it means a target sits on ground that is not joined to the rest
                        // of the walkable surface. Aborting is the point -- a partial path would
                        // return the distance to wherever the search gave up, which is shorter
                        // than the truth and indistinguishable from a valid answer.
                        if (!TryNavMeshDistanceXZ(navPos3[i], navPos3[j], out float d))
                        {
                            Debug.LogError($"GemOptimalPathCalculator: no complete NavMesh route between " +
                                           $"'{gems[i].name}' and '{gems[j].name}'. Either the bake does not " +
                                           $"cover both, or they sit on disconnected islands. Nothing was written.");
                            return;
                        }
                        dist[i, j] = d;
                    }
                }
            }
        }

        // 2. Setup Dynamic Programming state
        int maxMask = 1 << n;
        float[,] dp = new float[maxMask, n];
        int[,] parent = new int[maxMask, n];

        for (int i = 0; i < maxMask; i++)
        {
            for (int j = 0; j < n; j++)
            {
                dp[i, j] = float.MaxValue;
                parent[i, j] = -1;
            }
        }

        // 3. Initialize starting points (any node can be a starting point)
        for (int i = 0; i < n; i++)
        {
            dp[1 << i, i] = 0f;
        }

        // 4. Run Held-Karp DP
        for (int mask = 1; mask < maxMask; mask++)
        {
            for (int i = 0; i < n; i++)
            {
                // If the i-th node is in this mask and reachable
                if ((mask & (1 << i)) != 0 && dp[mask, i] != float.MaxValue)
                {
                    for (int j = 0; j < n; j++)
                    {
                        // If the j-th node is NOT in the mask, we can transition to it
                        if ((mask & (1 << j)) == 0)
                        {
                            int nextMask = mask | (1 << j);
                            float newDist = dp[mask, i] + dist[i, j];

                            if (newDist < dp[nextMask, j])
                            {
                                dp[nextMask, j] = newDist;
                                parent[nextMask, j] = i;
                            }
                        }
                    }
                }
            }
        }

        // 5. Find the minimum distance spanning all nodes
        float minDist = float.MaxValue;
        int endNode = -1;
        int finalMask = maxMask - 1;

        for (int i = 0; i < n; i++)
        {
            if (dp[finalMask, i] < minDist)
            {
                minDist = dp[finalMask, i];
                endNode = i;
            }
        }

        // 6. Reconstruct the optimal path
        List<string> pathNames = new List<string>();
        List<int> pathOrder = new List<int>();
        int currMask = finalMask;
        int currNode = endNode;

        while (currNode != -1)
        {
            pathNames.Add(gems[currNode].name);
            pathOrder.Add(currNode);
            int prevNode = parent[currMask, currNode];
            currMask = currMask ^ (1 << currNode); // remove the current node from mask
            currNode = prevNode;
        }

        pathNames.Reverse(); // Path is traced backwards from the end node
        pathOrder.Reverse();

        // The same visiting order measured two other ways. Both are reported, neither is
        // optimised for: the 3D length so the pairing can be redone in 3D later without
        // re-solving, and the straight-line length so the cost the walkable constraint adds
        // is visible as a ratio rather than having to be taken on trust.
        float dist3d = 0f;
        float straightXz = 0f;
        for (int i = 1; i < pathOrder.Count; i++)
        {
            int a = pathOrder[i - 1], b = pathOrder[i];
            straightXz += Vector2.Distance(pos[a], pos[b]);

            if (distanceMode == DistanceMode.WalkableGrid)
            {
                dist3d += gridDist3d[a, b];
            }
            else if (distanceMode == DistanceMode.WalkableNavMesh &&
                     TryNavMeshPath(navPos3[a], navPos3[b], out _, out float seg3d))
            {
                dist3d += seg3d;
            }
            else
            {
                dist3d += Vector3.Distance(pos3[a], pos3[b]);
            }
        }

        int pool = ResolvePool();
        string method;
        switch (distanceMode)
        {
            case DistanceMode.WalkableNavMesh: method = "held_karp_open_tour_navmesh_geodesic_xz"; break;
            case DistanceMode.WalkableGrid: method = $"held_karp_open_tour_walkable_grid_{gridCellSize}m_xz"; break;
            default: method = "held_karp_open_tour_euclidean_xz"; break;
        }

        Debug.Log($"--- Optimal Shortest Path Found (pool {pool}, {distanceMode}) ---");
        Debug.Log($"Path Sequence: {string.Join(" -> ", pathNames)}");
        Debug.Log($"Total Exact Shortest Path Length (XZ only): {minDist}");
        Debug.Log($"Same tour measured in 3D: {dist3d}");
        if (distanceMode != DistanceMode.StraightLine)
        {
            float detour = straightXz > 0f ? minDist / straightXz : 0f;
            Debug.Log($"Straight-line length of the same order: {straightXz} (walkable route is {detour:F2}x that)");
        }

        if (writeCsv) WriteCsvRow(pool, n, minDist, dist3d, straightXz, method);
    }

    // ---------------------------------------------------------------------------------
    // Walkable grid
    //
    // WHY THIS EXISTS ALONGSIDE THE NAVMESH MODE. A NavMesh bake is a pile of editor state --
    // package, surface component, collection scope, layer mask, voxel size, agent profile --
    // and every one of those can silently produce an empty or wrong surface. This mode has
    // none of it. It samples the SAME colliders RandomSpawner places objects on, using the
    // SAME downward raycast, so the region a route may cross is by construction the region
    // objects may spawn in. Nothing to configure means nothing to misconfigure, and the whole
    // definition travels with the scene rather than with a baked asset.
    //
    // THE APPROXIMATION, stated rather than hidden: paths run between cell centres on an
    // 8-connected grid, so a straight diagonal run is measured up to ~8% long. That is
    // immaterial here because the ratio is compared across conditions within a pool, where
    // the denominator is identical and any bias cancels. It is not a metric survey.
    // ---------------------------------------------------------------------------------

    private struct WalkableGridData
    {
        public int nx, nz;
        public float cell;
        public float minX, minZ;
        public bool[] walkable;
        public float[] height;
    }

    /// <summary>
    /// Full pairwise shortest-path matrix across the walkable grid, in the ground plane, with
    /// the 3D length of each of those same paths carried alongside. Returns false and explains
    /// itself on any condition that would otherwise yield a plausible-but-wrong number.
    /// </summary>
    private bool BuildGridDistanceMatrix(Vector3[] pos3, Transform[] gems, out float[,] dist, out float[,] dist3d)
    {
        int n = pos3.Length;
        dist = new float[n, n];
        dist3d = new float[n, n];

        // The walkable region is read off the spawner rather than authored twice. If the two
        // ever disagreed, objects could spawn where the route is not allowed to go.
        var spawner = GetComponent<RandomSpawner>();
        if (spawner == null || spawner.targetPlanes == null || spawner.targetPlanes.Count == 0)
        {
            Debug.LogError("GemOptimalPathCalculator: WalkableGrid mode needs the RandomSpawner's Target Planes " +
                           "on this same GameObject to define the walkable surface. Nothing was written.");
            return false;
        }

        // Spawn surfaces first, then the walk-only ones. The union is the region a route may
        // cross; Target Planes alone is the region an object may occupy.
        var surfaces = new List<Collider>(spawner.targetPlanes);

        WalkableAreaSet walkOnly = walkOnlySurfaces != null ? walkOnlySurfaces : FindObjectOfType<WalkableAreaSet>();
        int walkOnlyCount = 0;
        if (walkOnly != null)
        {
            foreach (Collider extra in walkOnly.GetSurfaces())
            {
                if (!surfaces.Contains(extra)) { surfaces.Add(extra); walkOnlyCount++; }
            }
        }

        // Printed every run so the two regions are visible in the log rather than having to be
        // reconstructed from two Inspectors later.
        Debug.Log($"GemOptimalPathCalculator: walkable region = {spawner.targetPlanes.Count} spawn " +
                  $"surface(s) + {walkOnlyCount} walk-only surface(s)." +
                  (walkOnly == null ? " No WalkableAreaSet found; routes may only cross spawn surfaces." : ""));

        if (!BuildWalkableGrid(surfaces, out WalkableGridData grid)) return false;

        // Snap each target onto the grid. Targets sit above the floor (they are raised so their
        // base meets the ground), so this is a planar lookup with an outward search for the
        // nearest walkable cell.
        int[] cellOf = new int[n];
        var stranded = new List<string>();
        for (int i = 0; i < n; i++)
        {
            cellOf[i] = NearestWalkableCell(grid, pos3[i]);
            if (cellOf[i] < 0) stranded.Add(gems[i].name);
        }

        if (stranded.Count > 0)
        {
            Debug.LogError($"GemOptimalPathCalculator: {stranded.Count} of {n} targets have no walkable grid cell " +
                           $"near them: {string.Join(", ", stranded)}. The spawn surface and the grid disagree, " +
                           $"which should be impossible -- check gridCellSize is not larger than a narrow path. " +
                           $"Nothing was written.");
            return false;
        }

        // One Dijkstra per target over the whole grid. n runs is far cheaper than n^2 pair
        // searches and gives every pair in the same pass.
        for (int i = 0; i < n; i++)
        {
            DijkstraFrom(grid, cellOf[i], out float[] costXz, out float[] cost3d);

            for (int j = 0; j < n; j++)
            {
                if (i == j) { dist[i, j] = 0f; dist3d[i, j] = 0f; continue; }

                float d = costXz[cellOf[j]];
                if (float.IsInfinity(d))
                {
                    Debug.LogError($"GemOptimalPathCalculator: '{gems[i].name}' and '{gems[j].name}' are not " +
                                   $"connected across the walkable surface -- they sit on separate islands. " +
                                   $"Add a plane bridging the gap under Walkable Zone, or raise maxStepHeight " +
                                   $"if they are joined by a step taller than {maxStepHeight} m. Nothing was written.");
                    return false;
                }

                dist[i, j] = d;
                dist3d[i, j] = cost3d[cellOf[j]];
            }
        }

        return true;
    }

    /// <summary>
    /// Samples the spawn colliders into a walkability grid by casting straight down at each cell
    /// centre -- deliberately the same test RandomSpawner uses to place an object, so a cell is
    /// walkable exactly when an object could have spawned there.
    /// </summary>
    private bool BuildWalkableGrid(List<Collider> planes, out WalkableGridData grid)
    {
        grid = default;

        bool haveBounds = false;
        Bounds combined = new Bounds();
        foreach (Collider c in planes)
        {
            if (c == null) continue;
            if (!haveBounds) { combined = c.bounds; haveBounds = true; }
            else combined.Encapsulate(c.bounds);
        }

        if (!haveBounds)
        {
            Debug.LogError("GemOptimalPathCalculator: every entry in Target Planes is null. Nothing was written.");
            return false;
        }

        if (gridCellSize <= 0f)
        {
            Debug.LogError("GemOptimalPathCalculator: gridCellSize must be greater than zero. Nothing was written.");
            return false;
        }

        int nx = Mathf.Max(1, Mathf.CeilToInt(combined.size.x / gridCellSize));
        int nz = Mathf.Max(1, Mathf.CeilToInt(combined.size.z / gridCellSize));

        if ((long)nx * nz > maxGridCells)
        {
            Debug.LogError($"GemOptimalPathCalculator: a {gridCellSize} m grid over these bounds needs " +
                           $"{(long)nx * nz:N0} cells, above the {maxGridCells:N0} ceiling. Raise gridCellSize. " +
                           $"Nothing was written.");
            return false;
        }

        grid.nx = nx;
        grid.nz = nz;
        grid.cell = gridCellSize;
        grid.minX = combined.min.x;
        grid.minZ = combined.min.z;
        grid.walkable = new bool[nx * nz];
        grid.height = new float[nx * nz];

        float rayTop = combined.max.y + 1f;
        float rayLength = combined.size.y + 2f;
        int walkableCount = 0;

        for (int gz = 0; gz < nz; gz++)
        {
            for (int gx = 0; gx < nx; gx++)
            {
                float wx = grid.minX + (gx + 0.5f) * gridCellSize;
                float wz = grid.minZ + (gz + 0.5f) * gridCellSize;
                var ray = new Ray(new Vector3(wx, rayTop, wz), Vector3.down);

                // Highest hit wins, so an overhang cannot pull the walkable height down to a
                // surface underneath it.
                bool hitAny = false;
                float bestY = float.NegativeInfinity;
                foreach (Collider c in planes)
                {
                    if (c == null) continue;
                    if (c.Raycast(ray, out RaycastHit hit, rayLength) && hit.point.y > bestY)
                    {
                        bestY = hit.point.y;
                        hitAny = true;
                    }
                }

                int idx = gz * nx + gx;
                grid.walkable[idx] = hitAny;
                grid.height[idx] = hitAny ? bestY : 0f;
                if (hitAny) walkableCount++;
            }
        }

        Debug.Log($"GemOptimalPathCalculator: walkable grid {nx} x {nz} at {gridCellSize} m " +
                  $"({walkableCount:N0} of {nx * nz:N0} cells walkable, " +
                  $"{walkableCount * gridCellSize * gridCellSize:N0} m2).");

        if (walkableCount == 0)
        {
            Debug.LogError("GemOptimalPathCalculator: no cell in the grid hit a spawn collider. " +
                           "Nothing was written.");
            return false;
        }

        return true;
    }

    /// <summary>The walkable cell containing this position, or the nearest one in an outward ring
    /// search. Returns -1 if nothing walkable lies within a few cells.</summary>
    private int NearestWalkableCell(WalkableGridData grid, Vector3 world)
    {
        int cx = Mathf.Clamp(Mathf.FloorToInt((world.x - grid.minX) / grid.cell), 0, grid.nx - 1);
        int cz = Mathf.Clamp(Mathf.FloorToInt((world.z - grid.minZ) / grid.cell), 0, grid.nz - 1);

        if (grid.walkable[cz * grid.nx + cx]) return cz * grid.nx + cx;

        // A target can land just outside a cell centre's sample near a boundary. Four rings is
        // plenty at any sane cell size, and staying small keeps this from silently teleporting
        // a target across a gap.
        for (int r = 1; r <= 4; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r) continue;   // ring only
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= grid.nx || z >= grid.nz) continue;
                    if (grid.walkable[z * grid.nx + x]) return z * grid.nx + x;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Dijkstra across the 8-connected walkable grid from one cell. Cost is ground-plane
    /// distance, matching the analysis numerator (head_path_xz_m); the 3D length of that same
    /// path is accumulated in parallel but never optimised for, so it describes the planar
    /// route rather than a different, shorter one.
    /// </summary>
    private void DijkstraFrom(WalkableGridData grid, int start, out float[] costXz, out float[] cost3d)
    {
        int cells = grid.nx * grid.nz;
        costXz = new float[cells];
        cost3d = new float[cells];
        for (int i = 0; i < cells; i++) { costXz[i] = float.PositiveInfinity; cost3d[i] = float.PositiveInfinity; }

        costXz[start] = 0f;
        cost3d[start] = 0f;

        var heap = new MinHeap(cells);
        heap.Push(start, 0f);
        var settled = new bool[cells];

        float straight = grid.cell;
        float diagonal = grid.cell * Mathf.Sqrt(2f);

        while (heap.TryPop(out int cur, out float curCost))
        {
            if (settled[cur]) continue;
            settled[cur] = true;

            int cx = cur % grid.nx;
            int cz = cur / grid.nx;

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;

                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= grid.nx || z >= grid.nz) continue;

                    int nb = z * grid.nx + x;
                    if (!grid.walkable[nb] || settled[nb]) continue;

                    // A height jump larger than a step means these cells are stacked, not
                    // adjacent -- a wall edge or a balcony over the ground below it.
                    float rise = Mathf.Abs(grid.height[nb] - grid.height[cur]);
                    if (rise > maxStepHeight) continue;

                    float stepXz = (dx != 0 && dz != 0) ? diagonal : straight;
                    float next = curCost + stepXz;
                    if (next < costXz[nb])
                    {
                        costXz[nb] = next;
                        cost3d[nb] = cost3d[cur] + Mathf.Sqrt(stepXz * stepXz + rise * rise);
                        heap.Push(nb, next);
                    }
                }
            }
        }
    }

    /// <summary>Binary min-heap keyed on cost. Hand-rolled because System.Collections.Generic
    /// .PriorityQueue is .NET 6 and this project is on the Unity 2022 runtime.</summary>
    private class MinHeap
    {
        private int[] _items;
        private float[] _keys;
        private int _count;

        public MinHeap(int capacity)
        {
            capacity = Mathf.Max(16, capacity / 4);
            _items = new int[capacity];
            _keys = new float[capacity];
        }

        public void Push(int item, float key)
        {
            if (_count == _items.Length)
            {
                System.Array.Resize(ref _items, _count * 2);
                System.Array.Resize(ref _keys, _count * 2);
            }

            _items[_count] = item;
            _keys[_count] = key;
            int i = _count++;

            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_keys[parent] <= _keys[i]) break;
                Swap(parent, i);
                i = parent;
            }
        }

        public bool TryPop(out int item, out float key)
        {
            if (_count == 0) { item = -1; key = 0f; return false; }

            item = _items[0];
            key = _keys[0];
            _count--;
            _items[0] = _items[_count];
            _keys[0] = _keys[_count];

            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, smallest = i;
                if (l < _count && _keys[l] < _keys[smallest]) smallest = l;
                if (r < _count && _keys[r] < _keys[smallest]) smallest = r;
                if (smallest == i) break;
                Swap(smallest, i);
                i = smallest;
            }

            return true;
        }

        private void Swap(int a, int b)
        {
            (_items[a], _items[b]) = (_items[b], _items[a]);
            (_keys[a], _keys[b]) = (_keys[b], _keys[a]);
        }
    }

    /// <summary>
    /// Pushes every baked NavMeshSurface into the navigation system before it is queried.
    ///
    /// WHY THIS IS NEEDED. A NavMeshSurface holds its bake as an asset and only hands it to the
    /// navigation system through AddData(), which it calls from OnEnable. In Play mode that
    /// happens for free. In the Editor it does not reliably happen at all, so NavMesh.SamplePosition
    /// finds nothing and every target reports as off-mesh even though the bake is sitting on disk
    /// and drawing correctly in the Scene view. This is an authoring-time tool, so it registers the
    /// data itself rather than depending on when a component last enabled.
    ///
    /// The triangle count is logged deliberately: it is the one number that distinguishes "no
    /// NavMesh exists" from "a NavMesh exists but does not reach the targets", and those two
    /// failures need completely different fixes.
    /// </summary>
    private void EnsureNavMeshRegistered()
    {
        NavMeshSurface[] surfaces = FindObjectsOfType<NavMeshSurface>();
        int withData = 0;

        foreach (NavMeshSurface surface in surfaces)
        {
            if (surface.navMeshData == null) continue;

            // Remove-then-add rather than add alone: adding data that is already registered
            // stacks a second overlapping instance, and the duplicate silently changes which
            // surface a query lands on.
            surface.RemoveData();
            surface.AddData();
            withData++;
        }

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        int triangles = tri.indices.Length / 3;

        if (triangles == 0)
        {
            Debug.LogError($"GemOptimalPathCalculator: found {surfaces.Length} NavMeshSurface(s), " +
                           $"{withData} carrying baked data, but the navigation system reports zero " +
                           $"triangles. Bake the surface (NavMeshSurface -> Bake) before calculating.");
        }
        else
        {
            Debug.Log($"GemOptimalPathCalculator: registered {withData} of {surfaces.Length} " +
                      $"NavMeshSurface(s); navigation system reports {triangles} triangles.");
        }
    }

    /// <summary>
    /// Projects every target onto the baked NavMesh. Targets are spawned a few millimetres above
    /// the floor and then raised so their visible base meets the ground, so their transforms sit
    /// above walkable surface by a variable amount and cannot be used as path endpoints directly.
    /// Fails loudly rather than skipping a target: a tour over 19 of 20 targets is a plausible
    /// number computed over the wrong object set, which is the failure this whole tool is built
    /// to avoid.
    /// </summary>
    private bool SnapToNavMesh(Vector3[] pos3, Transform[] gems, out Vector3[] snapped)
    {
        snapped = new Vector3[pos3.Length];
        var unreachable = new List<string>();

        for (int i = 0; i < pos3.Length; i++)
        {
            if (NavMesh.SamplePosition(pos3[i], out NavMeshHit hit, navMeshSampleRadius, NavMesh.AllAreas))
            {
                snapped[i] = hit.position;
            }
            else
            {
                unreachable.Add(gems[i].name);
            }
        }

        if (unreachable.Count > 0)
        {
            Debug.LogError($"GemOptimalPathCalculator: {unreachable.Count} of {pos3.Length} targets are not " +
                           $"within {navMeshSampleRadius} m of any NavMesh: {string.Join(", ", unreachable)}. " +
                           $"Either no NavMesh is baked, the bake does not cover the whole walkable area, or " +
                           $"the sample radius is too small. Nothing was written.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Walkable route between two on-mesh points, measured both in the ground plane and in 3D.
    /// Returns false unless the path is complete: <c>PathPartial</c> means the search stopped
    /// short, and its length is a shorter-than-true number that would otherwise be indis-
    /// tinguishable from a valid one.
    /// </summary>
    private bool TryNavMeshPath(Vector3 a, Vector3 b, out float lengthXz, out float length3d)
    {
        lengthXz = 0f;
        length3d = 0f;

        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path)) return false;
        if (path.status != NavMeshPathStatus.PathComplete) return false;

        Vector3[] corners = path.corners;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 p = corners[i - 1], q = corners[i];
            // Measured in the ground plane for the same reason the straight-line mode is:
            // the numerator it divides into (head_path_xz_m) is planar, so a denominator
            // that accumulated stair rise would not be comparable with it.
            lengthXz += Vector2.Distance(new Vector2(p.x, p.z), new Vector2(q.x, q.z));
            length3d += Vector3.Distance(p, q);
        }
        return true;
    }

    private bool TryNavMeshDistanceXZ(Vector3 a, Vector3 b, out float distance)
        => TryNavMeshPath(a, b, out distance, out _);

    /// <summary>
    /// The children that count as targets. Recall objects share the same parent and must not
    /// enter the tour: they are scenery for the memory task, not things the participant is
    /// asked to walk to.
    /// </summary>
    private List<Transform> CollectTargets()
    {
        List<Transform> found = new List<Transform>();
        foreach (Transform child in transform)
        {
            // String comparison rather than CompareTag: CompareTag throws if the tag in the
            // Inspector field is not defined in the TagManager, which turns a typo into an
            // exception instead of an empty result and the warning below.
            if (child.gameObject.tag == targetTag) found.Add(child);
        }

        if (found.Count == 0 && fallbackToAllChildren)
        {
            Debug.LogWarning($"GemOptimalPathCalculator: no child tagged '{targetTag}'. Falling back to all {transform.childCount} direct children.");
            foreach (Transform child in transform) found.Add(child);
        }

        return found;
    }

    /// <summary>
    /// The pool the spawner actually has selected wins over the Inspector field, so a route
    /// can never be filed under a pool it was not computed from.
    /// </summary>
    private int ResolvePool()
    {
        var spawner = GetComponent<RandomSpawner>();
        if (spawner == null) return poolOverride;

        switch (spawner.selectedPool)
        {
            case RandomSpawner.PoolSelection.Pool1: return 1;
            case RandomSpawner.PoolSelection.Pool2: return 2;
            case RandomSpawner.PoolSelection.Pool3: return 3;
            case RandomSpawner.PoolSelection.Pool4: return 4;
            default: return poolOverride;
        }
    }

    private void WriteCsvRow(int pool, int nTargets, float idealXz, float ideal3d, float straightXz, string method)
    {
#if UNITY_EDITOR
        var spawner = GetComponent<RandomSpawner>();
        // ActiveSeed is null when useFixedSeed is off, in which case the layout is not
        // reproducible and neither is this route. Record the absence rather than a seed that
        // was never applied.
        string seed = (spawner != null && spawner.ActiveSeed.HasValue)
            ? spawner.ActiveSeed.Value.ToString(CultureInfo.InvariantCulture)
            : "";

        // straight_line_xz_m is the same visiting order measured without the walkable
        // constraint. It is not what the analysis divides by -- 12_join.R reads
        // ideal_path_xz_m -- but it makes the cost of the constraint auditable per pool, and
        // it is what tells you whether a switch of distanceMode actually changed anything.
        const string header = "pool,n_targets,seed,ideal_path_xz_m,ideal_path_3d_m,straight_line_xz_m,method,computed_utc";
        string row = string.Join(",", new[]
        {
            pool.ToString(CultureInfo.InvariantCulture),
            nTargets.ToString(CultureInfo.InvariantCulture),
            seed,
            idealXz.ToString("F4", CultureInfo.InvariantCulture),
            ideal3d.ToString("F4", CultureInfo.InvariantCulture),
            straightXz.ToString("F4", CultureInfo.InvariantCulture),
            method,
            System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string path = Path.Combine(projectRoot, csvRelativePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Read-modify-write keyed on pool. Appending blindly would leave two rows for one
            // pool and let the join silently duplicate every trial in that pool.
            int expectedFields = header.Split(',').Length;
            List<string> lines = new List<string>();
            List<int> stalePools = new List<int>();
            if (File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("pool,")) continue;               // old header
                    if (line.StartsWith(pool + ",")) continue;            // this pool's old row

                    // A row written before a column was added would leave the file ragged, and
                    // readr would either fail the run or silently shift every field after the
                    // gap. Dropping it forces that pool to be recomputed, which is the only
                    // honest recovery: its number came from a different tool version.
                    if (line.Split(',').Length != expectedFields)
                    {
                        if (int.TryParse(line.Split(',')[0], out int stale)) stalePools.Add(stale);
                        continue;
                    }
                    lines.Add(line);
                }
            }

            if (stalePools.Count > 0)
            {
                Debug.LogWarning($"GemOptimalPathCalculator: dropped {stalePools.Count} row(s) written by an " +
                                 $"older version of this tool (pool(s) {string.Join(", ", stalePools)}). " +
                                 $"Re-run the calculator on those pools before reducing data.");
            }
            lines.Add(row);
            lines.Sort();                                                  // pool order, stable diffs

            List<string> outLines = new List<string> { header };
            outLines.AddRange(lines);
            File.WriteAllLines(path, outLines);

            Debug.Log($"GemOptimalPathCalculator: wrote pool {pool} to {path}");
            UnityEditor.AssetDatabase.Refresh();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"GemOptimalPathCalculator: could not write {path} -- {e.Message}");
        }
#else
        Debug.LogWarning("GemOptimalPathCalculator: CSV export is an Editor-only authoring step.");
#endif
    }
}
