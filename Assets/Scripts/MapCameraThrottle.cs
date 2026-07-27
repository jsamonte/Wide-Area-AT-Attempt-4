using UnityEngine;

/// <summary>
/// Renders a secondary camera every Nth frame instead of every frame.
///
/// Written for the map camera, which draws the whole scene a second time into a small
/// RenderTexture. The pixel cost is trivial at 256x256, but the CPU cost -- culling and
/// submitting every renderer in the scene -- is a full duplicate of the main camera pass,
/// every frame. A navigation map does not need to update 30 times a second.
///
/// Works by toggling Camera.enabled rather than calling Camera.Render(). Under a Scriptable
/// Render Pipeline (this project uses URP) Camera.Render() is not the supported path;
/// letting the pipeline pick the camera up on the frames it is enabled is.
///
/// Attach to the map camera's GameObject.
/// </summary>
[RequireComponent(typeof(Camera))]
public class MapCameraThrottle : MonoBehaviour
{
    [Tooltip("Render one frame out of every N. 1 = every frame (no throttling). At a 30 fps " +
             "cap, 6 gives 5 map updates per second, which is smooth enough for a map.")]
    [SerializeField] [Range(1, 30)] private int framesBetweenRenders = 6;

    [Tooltip("Log the effective map refresh rate once on startup.")]
    [SerializeField] private bool logOnStart = true;

    private Camera _camera;
    private int _framesUntilNextRender;

    private void Awake()
    {
        _camera = GetComponent<Camera>();

        // Start disabled, then render on the very first LateUpdate so the RenderTexture is
        // populated immediately rather than staying blank until the first interval elapses.
        _camera.enabled = false;
        _framesUntilNextRender = 1;
    }

    private void Start()
    {
        if (!logOnStart) return;

        int cap = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60;
        float hz = cap / (float)Mathf.Max(1, framesBetweenRenders);
        Debug.Log($"MapCameraThrottle: '{name}' rendering 1 frame in {framesBetweenRenders} " +
                  $"(~{hz:F1} map updates/sec at a {cap} fps cap).");
    }

    private void LateUpdate()
    {
        // Cameras render after LateUpdate, so a camera enabled here draws during this frame
        // and must be switched off again on the next one.
        if (_camera.enabled)
            _camera.enabled = false;

        if (--_framesUntilNextRender <= 0)
        {
            _camera.enabled = true;
            _framesUntilNextRender = Mathf.Max(1, framesBetweenRenders);
        }
    }

    private void OnDisable()
    {
        // Leave the camera in a working state if this component is disabled or removed,
        // rather than stranding it switched off with a frozen map.
        if (_camera != null)
            _camera.enabled = true;
    }
}
