/*************************************
 * 版    权：XGrids Corporation
 * 时    间：2022.07.26
 * 编写人员：汪祥春
 * 功    能：
 *           PC端鼠标控制相机类
 *                      
 * 使用方法:   将该脚本挂载到摄像机上即可
 * 操作方法:     
 *            1、鼠标左键按下+鼠标移动 ＝ 镜头平移
 *            2、鼠标右键按下+鼠标移动 ＝ 镜头旋转
 *            3、支持键盘操作键盘WASD (+leftShift) 镜头前进，后退，向左，向右（3倍速）
 *            4、鼠标滚轮滚动 ＝ 镜头前进，后退                    
 * ***********************************/

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
namespace XGrids.LCC
{
    [DefaultExecutionOrder(999999)]
    public class CameraOperate : MonoBehaviour
    {
        #region 速度控制参数
        [SerializeField]
        [Range(1f, 100f)] private float scrollSpeed = 50f;       //缩放速度
        [SerializeField]
        [Range(0.5f, 5f)] private float scrollDampen = 1.5f;    //缩放阻尼
        [SerializeField]
        [Range(0.1f, 1f)] private float rotateXSpeed = 0.3f;    //X轴旋转速度
        [SerializeField]
        [Range(0.1f, 1f)] private float rotateYSpeed = 0.3f;    //Y轴旋转速度
        [SerializeField]
        [Range(0.05f, 1f)] private float moveSpeed = 0.12f;     //平移速度
        [SerializeField]
        [Range(1f, 10f)] private float keyMoveSpeed = 5f;     //键盘操作平移速度
        #endregion

        #region 操作控制参数
        private bool isEnable = true;                           //是否启用操作                        
        private readonly bool isKeyOperateSupport = true;       //是否支持键盘操作
        private bool isRotate = false;                          //是否旋转
        private bool isMove = false;                            //是否平移
        private Transform mTransform;                           //摄像机Transform组件
        private Vector3 traStart;                               //摄像机在开始进行操作时摄像机初始的位置 
        private Vector3 mouseStart;                             //摄像机在开始进行操作时鼠标初始的位置
        private bool isFaceDown = false;                        //摄像机是否镜头朝下
        private float scrollValue = 0f;                         //滚轮缩放量
        private bool isZooming = false;                         //是否在缩放中
        private Queue<float> deltaTimeQueue;                    //平滑时间差值
        private float smoothedDeltaTime;                        //平滑后的时间
        private const int smoothingFrames = 5;                  //平滑帧数
        #endregion
        void Start()
        {
            mTransform = transform;
            deltaTimeQueue = new Queue<float>();
        }

        void Update()
        {
            if (!isEnable)
                return;


            if (isRotate && Input.GetMouseButtonUp(0))                 //当处于旋转状态，且鼠标右键放开，则退出旋转状态
            {
                isRotate = false;
            }            
            if (isMove && Input.GetMouseButtonUp(1))                   //当处于平移状态，且鼠标滚轮放开，则退出平移状态
            {
                isMove = false;
            }
            if (Input.GetMouseButtonDown(1))                           //鼠标左键控制平移
            {                
                isMove = true;                
                mouseStart = Input.mousePosition;                       //记录鼠标初始位置                
                traStart = mTransform.position;                         //记录摄像机初始位置
            }
            else if (Input.GetMouseButtonDown(0))                       //鼠标右键控制旋转
            {                
                isRotate = true;                
                mouseStart = Input.mousePosition;                       //记录鼠标初始位置，为了计算偏移量                
                traStart = mTransform.rotation.eulerAngles;             //记录鼠标初始角度
                                                                        //判断镜头是否朝下(根据物体朝上的位置的y轴是<0),-0.0001f是x旋转90的时候的特例
                isFaceDown = mTransform.up.y < -0.0001f ? true : false;
            }           

            if (isRotate)
            {
                Rotate();
            }          
            if (isMove)
            {
                Move();
            }
            Zoom();
        }

        private void FixedUpdate()
        {

            deltaTimeQueue.Enqueue(Time.deltaTime);
            if (deltaTimeQueue.Count > smoothingFrames)
            {
                deltaTimeQueue.Dequeue();
            }
            smoothedDeltaTime = 0f;
            foreach (float deltaTime in deltaTimeQueue)
            {
                smoothedDeltaTime += deltaTime;
            }
            smoothedDeltaTime /= deltaTimeQueue.Count;

            if (!isEnable)
                return;
            if (isKeyOperateSupport)
            {
                KeyOperation();
            }
        }

        #region Helper
        private void Move()
        {
            //鼠标在屏幕上的偏移量
            Vector3 offset = Input.mousePosition - mouseStart;
            //最终位置 = 初始位置 + 偏移量
            mTransform.position = traStart + mTransform.up * -offset.y * 0.1f * moveSpeed + mTransform.right * -offset.x * 0.1f * moveSpeed;
        }
        private void Rotate()
        {
            //获取鼠标在屏幕上的偏移量
            Vector3 offset = Input.mousePosition - mouseStart;
            //是否镜头朝下
            if (isFaceDown)
            {
                //最后的旋转角度 = 初始角度 + 偏移量，0.3f系数使得当rotateYSpeed，rotateXSpeed为1的时候，旋转速度正常
                mTransform.rotation = Quaternion.Euler(traStart + new Vector3(offset.y *  rotateYSpeed, -offset.x * rotateXSpeed, 0));
            }
            else
            {
                //最后的旋转角度 = 初始角度 + 偏移量
                mTransform.rotation = Quaternion.Euler(traStart + new Vector3(-offset.y *  rotateYSpeed, offset.x * rotateXSpeed, 0));
            }
        }
        private void Zoom()
        {
            scrollValue = Input.GetAxis("Mouse ScrollWheel");
            //滚轮是否滚动
            if (scrollValue != 0)
            {
                if (isZooming)
                {
                    StopCoroutine(ElasticZoom(0f));
                }
                isZooming = true;
                StartCoroutine(ElasticZoom(scrollValue));                
            }
        }

        private IEnumerator ElasticZoom(float scroll)
        {
            if (scroll > 0)
            {
                while (scroll > 0.01)
                {
                    scroll = Mathf.Lerp(scroll, 0, Time.deltaTime* scrollDampen);
                    mTransform.position += mTransform.forward * scroll * Time.deltaTime * scrollSpeed;
                    yield return new WaitForEndOfFrame();
                }

            }
            else
            {
                while (scroll < -0.01)
                {
                    scroll = Mathf.Lerp(scroll, 0, Time.deltaTime* scrollDampen);
                    mTransform.position += mTransform.forward * scroll * Time.deltaTime * scrollSpeed;
                    yield return new WaitForEndOfFrame();
                }
            }
            isZooming = false;

        }

        private void KeyOperation()
        {
            float speed = keyMoveSpeed;
            //按下LeftShift使得速度*2
            if (Input.GetKey(KeyCode.LeftShift))
            {
                speed = 3f * speed;
            }
            //键盘W键按下，镜头前进
            if (Input.GetKey(KeyCode.W))
            {
                mTransform.position += mTransform.forward  *speed * smoothedDeltaTime;      //* Time.deltaTime
            }
            //键盘S键按下，镜头后退
            if (Input.GetKey(KeyCode.S))
            {
                mTransform.position -= mTransform.forward  * speed * smoothedDeltaTime;
            }
            //键盘A键按下，镜头向左
            if (Input.GetKey(KeyCode.A))
            {
                mTransform.position -= mTransform.right  * speed * smoothedDeltaTime;
            }
            //键盘D键按下，镜头向右
            if (Input.GetKey(KeyCode.D))
            {
                mTransform.position += mTransform.right  * speed * smoothedDeltaTime;
            }
        }
        #endregion
    }
}


