using UnityEngine;
using UnityEngine.Android;
using System.Collections;
#if UNITY_ANDROID
using UnityEngine.XR.MagicLeap;
#endif

public class BackgroundRunPermission : MonoBehaviour
{
    private readonly string[] requiredPermissions = new string[]
    {
        "android.permission.CAMERA",
        "android.permission.RECORD_AUDIO",
        "com.magicleap.permission.EYE_TRACKING",
        "com.magicleap.permission.PUPIL_SIZE",
        "com.magicleap.permission.VOICE_INPUT",
        "com.magicleap.permission.SPATIAL_MAPPING",
        "com.magicleap.permission.SPATIAL_ANCHOR",
        "com.magicleap.permission.HAND_TRACKING",
        "com.magicleap.permission.WEBVIEW",
        "com.magicleap.permission.MARKER_TRACKING",
        "com.magicleap.permission.SPACE_MANAGER",
        "com.magicleap.permission.FACIAL_EXPRESSION",
        "com.magicleap.permission.SPACE_IMPORT_EXPORT",
        "com.magicleap.permission.EYE_CAMERA",
        "com.magicleap.permission.DEPTH_CAMERA",
        "com.magicleap.permission.KEEP_RUNNING_IN_BACKGROUND"
    };

    private void Awake()
    {
        StartCoroutine(RequestAllPermissions());
    }

    private IEnumerator RequestAllPermissions()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        foreach (var permission in requiredPermissions)
        {
            if (!Permission.HasUserAuthorizedPermission(permission))
            {
                Permission.RequestUserPermission(permission);
                yield return new WaitForSeconds(0.2f);
            }
        }
#endif
        Debug.Log("[BGRun] All permissions requested");
        yield break;
    }
}
