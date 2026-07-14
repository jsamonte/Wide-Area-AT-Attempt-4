using UnityEngine;

namespace ARCockpit.Study
{
    /// <summary>
    /// Marks a thing the participant can look at, so gaze can be attributed to it. Drop one on an AR
    /// element root (the NavBall, the map box) or on an invisible proxy placed over a PHYSICAL cockpit
    /// instrument (the PFD, the airspeed indicator), and <see cref="GazeAoi"/> will report dwell on it.
    ///
    /// SIZE COMES FROM THE ELEMENT'S PROFILE, not from this component. Assign the same MapProfile /
    /// NavBallProfile the element itself is drawn from (any <see cref="IGazeBoundsSource"/>) and the
    /// hitbox tracks the element through every dev-window size change, live, with no rebuild. This
    /// matters: the element sizes are tunable on the headset, and a hitbox that does not follow them
    /// would silently attribute gaze to a box floating where the element used to be. Leave the profile
    /// empty ONLY for a physical-instrument proxy, which has no profile and so carries its own size.
    ///
    /// The collider is a trigger sized in meters, so it can never interfere with anything else in the
    /// scene: it exists purely to be hit by the gaze ray.
    ///
    /// The key is what lands in the log, so keep keys short, stable, and comparable across conditions
    /// ("navball", "map", "pfd", "airspeed"). Changing a key mid-study splits your data.
    /// </summary>
    public class GazeTarget : MonoBehaviour
    {
        public enum Shape { Sphere, Box }

        [Tooltip("Short stable name for this area of interest. It is what gets written to the CSV.")]
        public string aoiKey = "unnamed";

        [Tooltip("The element's profile (MapProfile, NavBallProfile). The hitbox takes its shape and size " +
                 "from this and follows it as it is tuned. Leave empty ONLY for a physical-instrument " +
                 "proxy, which has no profile: then the manual size below is used.")]
        public ScriptableObject sizeProfile;

        [Header("Manual size (used only when there is no profile)")]
        [Tooltip("Sphere: radius in meters. Box: full extents in meters (x right, y up, z forward).")]
        public Shape shape = Shape.Box;
        public float radius = 0.15f;
        public Vector3 boxSize = new Vector3(0.3f, 0.2f, 0.02f);

        [Tooltip("Offset of the collider from this transform, in local meters. Use it when the element's " +
                 "visual center is not its pivot, or to sit a proxy over a physical instrument. It ADDS to " +
                 "any offset the profile reports (the NavBall's readout stack sits above its globe), so " +
                 "leave it at zero unless you are nudging a proxy.")]
        public Vector3 localOffset = Vector3.zero;

        [Tooltip("Draw the hitbox in the Editor so you can see what gaze is tested against.")]
        public bool drawGizmo = true;

        // Hitbox padding is a STUDY parameter, not a per-target one, so it lives on StudyProfile (one value,
        // tunable from the 231 window, stamped into every trial's CSV header). It used to be a field here,
        // which meant the map and the NavBall could silently disagree about how generously a look counts as a
        // look, and the AR-versus-physical comparison rests on exactly that judgement being uniform.
        static float Padding
        {
            get
            {
                StudyProfile p = StudyProfile.Active;
                return p != null ? Mathf.Max(0.01f, p.gazeHitboxPadding) : 1.15f;
            }
        }

        IGazeBoundsSource _source;
        SphereCollider _sphere;
        BoxCollider _box;
        bool _builtAsSphere;
        Vector3 _lastSize;

        void OnEnable()
        {
            _source = sizeProfile as IGazeBoundsSource;
            if (sizeProfile != null && _source == null)
                Debug.LogWarning($"[AOI] '{aoiKey}': {sizeProfile.name} does not expose gaze bounds. " +
                                 "Falling back to the manual size, which will NOT follow the element.");
            Sync();
        }

        // Cheap, and the element sizes are live-tunable, so we re-check every frame and rebuild only when
        // the numbers actually move.
        void Update() => Sync();

        void Sync()
        {
            bool isSphere = _source != null ? _source.GazeHitIsSphere : shape == Shape.Sphere;
            Vector3 size = _source != null
                ? _source.GazeHitHalfExtents * 2f            // half-extents to full extents
                : (shape == Shape.Sphere ? Vector3.one * radius * 2f : boxSize);

            size *= Padding;

            // The profile's own center (the NavBall's readout stack sits above the globe) plus any manual
            // nudge. Padding grows the box about that center, so the whole element stays covered.
            Vector3 center = (_source != null ? _source.GazeHitCenter : Vector3.zero) + localOffset;

            if ((_sphere != null || _box != null) && isSphere == _builtAsSphere && size == _lastSize)
            {
                Apply(isSphere, size, center);   // the center can still move; it is cheap to reapply
                return;
            }

            // Shape changed (or first build): drop the old collider so we never leave a stale one behind.
            if (_sphere != null) Destroy(_sphere);
            if (_box != null) Destroy(_box);
            _sphere = null;
            _box = null;

            if (isSphere) _sphere = gameObject.AddComponent<SphereCollider>();
            else _box = gameObject.AddComponent<BoxCollider>();

            _builtAsSphere = isSphere;
            _lastSize = size;
            Apply(isSphere, size, center);
        }

        void Apply(bool isSphere, Vector3 size, Vector3 center)
        {
            if (isSphere && _sphere != null)
            {
                _sphere.radius = size.x * 0.5f;
                _sphere.center = center;
                _sphere.isTrigger = true;
            }
            else if (_box != null)
            {
                _box.size = size;
                _box.center = center;
                _box.isTrigger = true;
            }
        }

        void OnDrawGizmosSelected()
        {
            if (!drawGizmo) return;
            var source = sizeProfile as IGazeBoundsSource;
            bool isSphere = source != null ? source.GazeHitIsSphere : shape == Shape.Sphere;
            Vector3 size = source != null
                ? source.GazeHitHalfExtents * 2f
                : (isSphere ? Vector3.one * radius * 2f : boxSize);
            size *= Padding;
            Vector3 center = (source != null ? source.GazeHitCenter : Vector3.zero) + localOffset;

            Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.35f);
            Gizmos.matrix = transform.localToWorldMatrix;
            if (isSphere) Gizmos.DrawWireSphere(center, size.x * 0.5f);
            else Gizmos.DrawWireCube(center, size);
        }
    }
}
