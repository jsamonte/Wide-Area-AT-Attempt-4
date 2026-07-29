using System.Collections.Generic;
using UnityEngine;
using Microsoft.MixedReality.WorldLocking.Core;

/// <summary>
/// An <see cref="IOrienter"/> that takes each pin's yaw from the ArUco marker's OWN measured
/// rotation, instead of inferring it from the relative positions of pin pairs.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS
///
/// The stock <see cref="Orienter"/> derives a pin's yaw from the vector between that pin and its
/// neighbours (weighted 1/distance^2, so the CLOSEST neighbour dominates). That is a good strategy
/// when all pins are measured in a common, stable reference frame.
///
/// It is a bad strategy here. With anchorSubsystem = Null there is no drift correction, so a pin's
/// LockedPose is the raw head-tracking pose at the instant that marker was scanned. Our 18 pins are
/// scanned minutes apart while walking a 113 x 99 m site, so each one is recorded in a DIFFERENT
/// epoch of a drifting frame. Inferring yaw from the difference between two such positions converts
/// whatever the tracker drifted between those two moments directly into yaw error -- and because the
/// weighting favours the nearest pair, the shortest (noisiest) baseline dominates.
///
/// Every ArUco detection already contains a full 6-DoF pose. Using the marker's own measured
/// rotation makes each pin's orientation CONTEMPORANEOUS with its own position measurement, which
/// removes the cross-epoch coupling from yaw entirely. 18 independent yaw measurements were being
/// thrown away.
///
/// SAFETY
///
/// The rotation an IOrienter supplies is a model->locked CORRECTION; SpacePinOrientable.PushRotation
/// post-multiplies it by ModelingPoseGlobal.rotation. Because we only ever supply a rotation about
/// Vector3.up, the resulting world correction is provably yaw-only -- a mis-measured marker tilt can
/// never pitch or roll the world. That matters because these markers lie flat on the ground, so
/// their out-of-plane tilt is the least reliable part of the detected pose.
///
/// FALLBACK
///
/// If no marker yaw has been reported yet (nothing scanned), this defers to the stock pairwise
/// behaviour, so startup is never worse than before.
/// </remarks>
public class MarkerYawOrienter : Orienter
{
    /// <summary>
    /// How to combine the per-marker yaw measurements.
    /// </summary>
    public enum YawModeEnum
    {
        /// <summary>
        /// Each pin uses its own marker's measured yaw. Fully removes cross-epoch coupling, and
        /// allows genuine local yaw variation across the site to be corrected. Most faithful to the
        /// measurements, but a single noisy marker only affects its own pin.
        /// </summary>
        PerPin = 0,

        /// <summary>
        /// All pins share the mean of every measured yaw. Much more robust to per-marker noise
        /// (error falls as 1/sqrt(N)) and keeps the model perfectly rigid, but cannot correct local
        /// yaw variation and does re-mix epochs -- gently, as an average, rather than amplifying
        /// them across a short baseline. Use this if PerPin proves too noisy in the field.
        /// </summary>
        SharedAverage = 1
    }

    [Tooltip("PerPin: each pin uses its own marker's yaw. SharedAverage: all pins use the mean yaw.")]
    [SerializeField] private YawModeEnum yawMode = YawModeEnum.PerPin;

    /// <summary>
    /// Runtime access so ArucoMarkerManager can surface the mode without a second inspector field.
    /// </summary>
    public YawModeEnum YawMode { get { return yawMode; } set { yawMode = value; } }

    /// <summary>
    /// Latest measured yaw correction (degrees about world up) per pin. Written by ArucoPinDriver
    /// immediately before it pushes a position, read here during Reorient.
    /// </summary>
    private readonly Dictionary<IOrientable, float> measuredYawDegrees = new Dictionary<IOrientable, float>();

    /// <summary>
    /// Report the yaw implied by this pin's own marker detection.
    /// </summary>
    /// <param name="orientable">The pin the measurement belongs to.</param>
    /// <param name="yawDegrees">Model->locked yaw correction about world up, in degrees.</param>
    public void SetMeasuredYaw(IOrientable orientable, float yawDegrees)
    {
        if (orientable == null) return;
        measuredYawDegrees[orientable] = yawDegrees;
    }

    /// <summary>
    /// Forget a pin's measurement. Called when a driver is torn down so the dictionary can't
    /// retain a destroyed component.
    /// </summary>
    public void ClearMeasuredYaw(IOrientable orientable)
    {
        if (orientable == null) return;
        measuredYawDegrees.Remove(orientable);
    }

    /// <summary>
    /// True if at least one marker yaw has been reported.
    /// </summary>
    public bool HasAnyMeasurement { get { return measuredYawDegrees.Count > 0; } }

    /// <summary>
    /// Replace the stock pairwise computation with the markers' own measured yaw.
    /// </summary>
    /// <returns>True on success.</returns>
    /// <remarks>
    /// Mirrors OrienterThreeBody's pattern: override only this step, and defer to the base class
    /// when the inputs this override needs are unavailable.
    ///
    /// Note that "actives" only ever contains pins in the fragment currently being processed, and
    /// the base class has already defaulted each entry's rotation to that pin's existing correction.
    /// So a pin with no measurement yet simply keeps what it had.
    /// </remarks>
    protected override bool ComputeRotations()
    {
        if (measuredYawDegrees.Count == 0)
        {
            // Nothing scanned yet -- behave exactly as the stock Orienter.
            return base.ComputeRotations();
        }

        float sharedYaw = 0.0f;
        if (yawMode == YawModeEnum.SharedAverage)
        {
            // Average as unit vectors rather than raw degrees, so the mean is correct across the
            // +/-180 wrap instead of being dragged toward zero by it.
            float sumX = 0.0f, sumZ = 0.0f;
            int n = 0;
            for (int i = 0; i < actives.Count; ++i)
            {
                float yaw;
                if (!measuredYawDegrees.TryGetValue(actives[i].orientable, out yaw)) continue;
                float rad = yaw * Mathf.Deg2Rad;
                sumX += Mathf.Cos(rad);
                sumZ += Mathf.Sin(rad);
                ++n;
            }
            if (n == 0)
            {
                return base.ComputeRotations();
            }
            sharedYaw = Mathf.Atan2(sumZ, sumX) * Mathf.Rad2Deg;
        }

        bool appliedAny = false;
        for (int i = 0; i < actives.Count; ++i)
        {
            float yawDegrees;
            if (yawMode == YawModeEnum.SharedAverage)
            {
                // Only pins that contributed a measurement get the shared yaw; an unscanned pin
                // keeps its default so we never invent an orientation for it.
                if (!measuredYawDegrees.ContainsKey(actives[i].orientable)) continue;
                yawDegrees = sharedYaw;
            }
            else if (!measuredYawDegrees.TryGetValue(actives[i].orientable, out yawDegrees))
            {
                continue;
            }

            WeightedRotation wrot = actives[i];
            wrot.rotation = Quaternion.AngleAxis(yawDegrees, Vector3.up);
            wrot.weight = 1.0f;
            actives[i] = wrot;
            appliedAny = true;
        }

        if (!appliedAny)
        {
            // Every active pin in this fragment is unmeasured; the pairwise estimate is still
            // better than leaving them all at their defaults.
            return base.ComputeRotations();
        }

        return true;
    }
}
