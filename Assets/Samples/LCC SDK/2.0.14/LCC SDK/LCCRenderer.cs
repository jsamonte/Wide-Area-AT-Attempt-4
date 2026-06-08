using UnityEngine;
using LCCCore;
using System.Collections;

[ExecuteInEditMode]
public class LCCRenderer : MonoBehaviour
{
    public LCCManager m_manager;
    public string m_FilePath;
    //public GameObject m_clipBox;
    private LCCCore.Renderer m_renderer;

    private Camera m_mainCamera;
    void Start()
    {
        m_mainCamera = Camera.main;
        m_renderer = m_manager.GetRender(this.transform);        
        m_renderer.Load(m_FilePath, PlatformType.PC, onLoadCallback);
        //m_manager.SetLockFPS(false);
        //m_renderer.SetDebugMode(true);
        //m_renderer.SetZDepth(true);

        //transform.rotation = Quaternion.identity;
        //transform.Rotate(Vector3.right, -90);
        //transform.localScale = new Vector3(-1, 1, 1);
        //Camera.main.gameObject.transform.rotation = Quaternion.identity;
        //Camera.main.gameObject.transform.position = new Vector3(0.0f, 1.6f, 0.0f);
        //Camera.main.gameObject.transform.Rotate(Vector3.up, 180);

    }

    //private void Update()
    //{
    //    if (Input.GetMouseButtonDown(0))
    //    {
    //        Vector3 _mousePosition = Input.mousePosition;
    //        if (m_manager.Raycast(_mousePosition, out HitResult _result))
    //        {
    //            GameObject _sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
    //            _sphere.transform.position = _result.hitPos;
    //            _sphere.transform.rotation = Quaternion.identity;
    //            _sphere.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
    //        }
    //    }
    //}

    private void onLoadCallback()
    {
        Debug.Log("data loaded !!!");
    }

    [ContextMenu("Render (need a little time to load data)")]
    public void Render()
    {    
        if(m_renderer == null)
            m_renderer = m_manager.GetRender(this.transform);
        m_renderer.Load(m_FilePath, PlatformType.PC, onLoadCallback);
    }

    [ContextMenu("unRender")]
    public void unRender()
    {
        m_renderer?.Dispose();
    }

    public void onRenderPointClick()
    {
        Texture2D rainbow = Resources.Load<Texture2D>("rainbow");
        m_renderer.SwitchRenderMode(LCCCore.RenderMode.PointCloud, rainbow);

    }
    public void onRender3DGSClick()
    {
        m_renderer.SwitchRenderMode(LCCCore.RenderMode.LCCGS);
    }

    public void onAlphaClick()
    {
        m_renderer.SetAlpha(0.3f);
    }

    public void onClipBox()
    {        
        //var _mat = m_clipBox.transform.worldToLocalMatrix;
        //m_renderer.SetClip(ClipType.PreBox, _mat);
    }

    public void onFov()
    {
        int _w = Camera.main.pixelWidth;
        int _h = Camera.main.pixelHeight;
        float _fov = Camera.main.fieldOfView;
        float _aspect = Camera.main.aspect;
        m_manager.SetFOV(_w, _h, _fov, _aspect);
    }


        private int aaartWidth = 3840;
    private int aaartHeight = 2160;
    private string fileName = "G:\\Test\\capture0407.jpg";

    public void CaptureAndsave()
    {
        m_renderer.SetRecordMode(true, new Vector2(aaartWidth, aaartHeight), m_mainCamera.fieldOfView);
        StartCoroutine(SaveFile());
    }

    private IEnumerator SaveFile()
    {
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();
        RenderTexture rt = new RenderTexture(aaartWidth, aaartHeight, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        //设置相机渲染目标
        m_mainCamera.targetTexture = rt;
        //强制相机渲染一次到RT
        m_mainCamera.Render();
        // 激活 RT
        RenderTexture.active = rt;
        //从RT读像素,注意这里用 rt.width/rt.height
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        // 重置
        m_mainCamera.targetTexture = null;
        RenderTexture.active = null;
        rt.Release();
        Destroy(rt);
        //保存
        byte[] bytes = tex.EncodeToJPG();
        System.IO.File.WriteAllBytes(fileName, bytes);
        Debug.Log("保存完成:" + fileName);

        m_renderer.SetRecordMode(false, new Vector2(1920, 1080), m_mainCamera.fieldOfView);

    }
}
