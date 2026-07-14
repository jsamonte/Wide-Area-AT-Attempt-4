using System.Collections.Generic;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;

namespace ARCockpit.Core
{
    /// <summary>
    /// One printed ArUco tag a MarkerAnchor accepts: its ID and the printed edge length (meters) used to
    /// size the detector so the reported distance is correct.
    /// </summary>
    public struct MarkerSpec
    {
        public int markerId;
        public float markerLengthMeters;
    }

    /// <summary>
    /// Implemented by the per-element config assets (MapProfile, NavBallProfile) so a MarkerAnchor reads
    /// WHICH ArUco IDs (and their printed sizes and dictionary) place its element from the one profile
    /// asset, instead of duplicating that list on the scene component. This is the "one source of truth"
    /// rule applied to markers: the profile already owns the size presets, so it owns the IDs too.
    /// </summary>
    public interface IMarkerSource
    {
        /// <summary>Dictionary the tags were generated from (IDs up to 249 need a 250-size dictionary).</summary>
        ArucoType MarkerDictionary { get; }

        /// <summary>Append every accepted (id, printed length) this source defines to <paramref name="into"/>.</summary>
        void CollectMarkers(List<MarkerSpec> into);

        /// <summary>Position offset from the marker, in meters, in the marker's frame.</summary>
        UnityEngine.Vector3 MarkerPositionOffset { get; }

        /// <summary>Rotation offset (Euler) from the marker.</summary>
        UnityEngine.Vector3 MarkerEulerOffset { get; }
    }
}
