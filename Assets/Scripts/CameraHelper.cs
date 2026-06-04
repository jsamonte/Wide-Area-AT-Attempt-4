using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraHelper : MonoBehaviour
{
    void LateUpdate()
    {
        // Forces the camera background to absolute transparent black every frame
        GetComponent<Camera>().backgroundColor = new Color(0, 0, 0, 0);
    }
}
