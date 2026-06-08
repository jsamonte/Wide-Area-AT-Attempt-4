/*********************************************
 * 版    权：XGrids Corporation
 * 时    间：2025.04.24
 * 编写人员：汪祥春
 * 功    能：
 *          场景相机同步器
 *          使用时需将该脚本挂载到相机游戏对象上
 * ******************************************/
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteInEditMode]
public class SceneGameCameraSync : MonoBehaviour
{
    [Header("Settings")]
    public bool syncPosition = true;
    public bool syncRotation = true;
    public bool syncFOV = true;
    [Tooltip("SyncTime")]
    public float syncInterval = 0.033f;

    private Camera _gameCamera;
    private float _lastSyncTime;

    void Start()
    {
        _gameCamera = GetComponent<Camera>();
    }

    void Update()
    {
#if UNITY_EDITOR
        if (Time.realtimeSinceStartup - _lastSyncTime > syncInterval)
        {
            SyncCamera();
            _lastSyncTime = Time.realtimeSinceStartup;
        }
#endif
    }

    void SyncCamera()
    {
#if UNITY_EDITOR
        if (SceneView.lastActiveSceneView == null || _gameCamera == null) return;
        var sceneCam = SceneView.lastActiveSceneView.camera;
        if (syncPosition) _gameCamera.transform.position = sceneCam.transform.position;
        if (syncRotation) _gameCamera.transform.rotation = sceneCam.transform.rotation;
        if (syncFOV) _gameCamera.fieldOfView = sceneCam.fieldOfView;
#endif
    }
}