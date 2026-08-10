using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The surfaces a participant may WALK ACROSS but on which no object is ever SPAWNED.
///
/// WHY THIS IS ITS OWN COMPONENT. The study has two different regions that are easy to conflate
/// because both are "the floor":
///
///   * WHERE OBJECTS SPAWN  -- RandomSpawner.targetPlanes. FROZEN. Its contents feed the total
///     area that sets the dart-throwing exclusion radius, and the list length feeds the random
///     draw sequence, so adding one collider there regenerates every pool's layout from the same
///     seed. Participants have already run against the current layouts; changing that list
///     silently invalidates their data.
///
///   * WHERE PARTICIPANTS WALK -- the union of the above with everything listed here. Corridors,
///     stairs, and gap-filling planes belong in this second group: a participant crosses them
///     constantly, but no target or recall object should sit on them.
///
/// Keeping the walk-only surfaces in a separate component, on a separate GameObject, means the
/// spawner's list is never the convenient place to drop a new piece of floor. The walkable region
/// is a strict superset of the spawn region by construction, which is the invariant that matters:
/// there can never be a spawn surface a route is forbidden to cross.
///
/// USAGE. Put this on an empty child of the walkable geometry (e.g. "Walk Only Surfaces"), drag
/// the colliders in, and reference it from GemOptimalPathCalculator. Colliders listed here need
/// only a collider -- no renderer -- and their GameObjects must be ENABLED whenever the path
/// calculation is run, because the grid finds them by raycast.
/// </summary>
public class WalkableAreaSet : MonoBehaviour
{
    [Tooltip("Colliders a participant may walk across but on which nothing spawns. NEVER add these to RandomSpawner's Target Planes -- that list is frozen, and changing it regenerates every pool's layout from the same seed.")]
    public List<Collider> walkOnlySurfaces = new List<Collider>();

    /// <summary>The non-null, de-duplicated surfaces. Returns an empty list rather than null so
    /// callers can union unconditionally.</summary>
    public List<Collider> GetSurfaces()
    {
        var result = new List<Collider>();
        if (walkOnlySurfaces == null) return result;

        foreach (Collider c in walkOnlySurfaces)
        {
            if (c != null && !result.Contains(c)) result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// Flags a collider that is in BOTH this list and the spawner's. That is not merely redundant:
    /// it means someone treated the two regions as interchangeable, which is the mistake this
    /// component exists to prevent, and it is worth catching in the Inspector rather than in the
    /// data six weeks later.
    /// </summary>
    private void OnValidate()
    {
        var spawner = FindObjectOfType<RandomSpawner>();
        if (spawner == null || spawner.targetPlanes == null || walkOnlySurfaces == null) return;

        foreach (Collider c in walkOnlySurfaces)
        {
            if (c != null && spawner.targetPlanes.Contains(c))
            {
                Debug.LogWarning($"WalkableAreaSet: '{c.name}' is in BOTH this walk-only list and " +
                                 $"RandomSpawner's Target Planes. Objects will spawn on it, and its area " +
                                 $"is already changing the layout seeds' output. Remove it from Target " +
                                 $"Planes unless you intend objects to spawn there.", this);
            }
        }
    }
}
