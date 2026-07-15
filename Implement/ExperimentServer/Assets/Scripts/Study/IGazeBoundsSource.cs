using UnityEngine;

namespace ARCockpit.Study
{
    /// <summary>
    /// An element profile that knows how big its element is, so the gaze hitbox can be derived from the
    /// same value that draws the element instead of being typed in a second time on the GazeTarget.
    ///
    /// This exists because the elements are TUNABLE: the map's hologram radius and the NavBall's globe
    /// radius are dev-window knobs that get changed on the headset mid-session. A hardcoded hitbox would
    /// quietly stop matching its element the first time either is tuned, and gaze would be attributed to
    /// a box floating where the element used to be. Nothing about that failure is visible in the data,
    /// which makes it exactly the kind of bug that ruins a study quietly.
    ///
    /// Implemented by MapProfile and NavBallProfile. The size lives in the profile; this just exposes it.
    /// </summary>
    public interface IGazeBoundsSource
    {
        /// <summary>True for a globe (the NavBall), false for a box (the map, a panel).</summary>
        bool GazeHitIsSphere { get; }

        /// <summary>Half-extents of the element in meters. For a sphere only x is read, as the radius.</summary>
        Vector3 GazeHitHalfExtents { get; }

        /// <summary>
        /// Center of the hitbox in the element's local meters, for elements whose visual is not centered on
        /// their pivot. The NavBall needs this: its readouts (speed, vertical speed, heading, waypoint) are
        /// stacked ABOVE the globe, so a hitbox centered on the globe misses every one of them and gaze at
        /// the numbers is recorded as gaze at nothing. Return Vector3.zero when the element is centered.
        /// </summary>
        Vector3 GazeHitCenter { get; }
    }
}
