// %BANNER_BEGIN%
// ---------------------------------------------------------------------
// %COPYRIGHT_BEGIN%
// Copyright (c) (2024) Magic Leap, Inc. All Rights Reserved.
// Use of this file is governed by the Software License Agreement, located here: https://www.magicleap.com/software-license-agreement-ml2
// Terms and conditions applicable to third-party materials accompanying this distribution may also be found in the top-level NOTICE file appearing herein.
// %COPYRIGHT_END%
// ---------------------------------------------------------------------
// %BANNER_END%
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MagicLeap.OpenXR.Features;
using MagicLeap.OpenXR.Features.LightEstimation;
using UnityEngine.XR.OpenXR;
using MagicLeap.Android;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace MagicLeap.Examples
{
    public class LightEstimationExample : MonoBehaviour
    {
        [SerializeField] private Material skyboxMaterial;
        [SerializeField] private Light directionalLight;
        [SerializeField] private GameObject directionalLightArrow;
        [SerializeField] private Text statusText;
        private Material oldSkyboxMaterial;
        private bool permissionGranted;
        private MagicLeapLightEstimationFeature lightEstimationFeature;
        private EstimateData estimateData;
        private Cubemap cubemap;
        private int cubemapResolution;
        private bool useSphericalHarmonics;
        private bool runEnvironmentMap;
        private bool environmentUpdateQueued;
        private int skyboxTextureKey;

        void Start()
        {
            lightEstimationFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLightEstimationFeature>();
            if(lightEstimationFeature == null)
            {
                Debug.LogError("Light estimation feature not enabled in OpenXR Feature Groups"); 
                enabled = false;
                return;
            }

            if (skyboxMaterial)
                skyboxTextureKey = Shader.PropertyToID("_Tex");
            MagicLeapController.Instance.BumperPressed += OnBumperPressed;
            MagicLeapController.Instance.MenuPressed += OnMenuPressed;
            Permissions.RequestPermission(UnityEngine.Android.Permission.Camera, OnPermissionGranted, OnPermissionDenied);
        }
        private void OnPermissionGranted(string permission)
        {
            permissionGranted = true;
            runEnvironmentMap = true;
            statusText.text = "Press either the menu or bumper button to begin light estimation";
        }

        private void OnPermissionDenied(string permission)
        {
            Debug.LogError($"{permission} denied, example won't function");
            enabled = false;
        }

        private void OnDisable()
        {
            MagicLeapController.Instance.BumperPressed -= OnBumperPressed;
            MagicLeapController.Instance.MenuPressed -= OnMenuPressed;
        }

        private void OnDestroy()
        {
            if(lightEstimationFeature.LightEstimationCreated)
                lightEstimationFeature.DestroyLightEstimation();
        }

        private void OnMenuPressed(UnityEngine.InputSystem.InputAction.CallbackContext obj)
        {
            //Change resolution; don't increment the resolution if there is no created light estimation
            if (!lightEstimationFeature.LightEstimationCreated)
            {
                ChooseEnvironmentMapResolution((HDRCubemapFaceResolution)cubemapResolution);
            }
            else
            {
                cubemapResolution = (cubemapResolution + 1) % 3;
                ChooseEnvironmentMapResolution((HDRCubemapFaceResolution)cubemapResolution);
            }
            statusText.text = string.Format("Cubemap Face resolution:{0}\nUsing HDR Cubemap:{1}\nUsing Spherical Harmonics:{2}", (HDRCubemapFaceResolution)cubemapResolution, runEnvironmentMap, useSphericalHarmonics);
        }

        private void OnBumperPressed(UnityEngine.InputSystem.InputAction.CallbackContext obj)
        {
            //Toggle sh/envmap
            if (!lightEstimationFeature.LightEstimationCreated)
                ChooseEnvironmentMapResolution((HDRCubemapFaceResolution)cubemapResolution);

            useSphericalHarmonics = !useSphericalHarmonics;
            runEnvironmentMap = !runEnvironmentMap;
            SwapSkybox();
            statusText.text = string.Format("Cubemap Face resolution:{0}\nUsing HDR Cubemap:{1}\nUsing Spherical Harmonics:{2}", (HDRCubemapFaceResolution)cubemapResolution, runEnvironmentMap, useSphericalHarmonics);
        }

        void Update()
        {
            if (!permissionGranted)
                return;
            if (!lightEstimationFeature.LightEstimationCreated || !lightEstimationFeature.CheckEstimationEstimateReadiness())
                return;

            estimateData = lightEstimationFeature.GetLightEstimationEstimateData();

            if (estimateData.TimeStampNanoSeconds == 0)
                return;
            
            if(estimateData.DirectionalLight.Direction != Vector3.zero)
            {
                directionalLight.color = estimateData.DirectionalLight.Color;
                directionalLight.intensity = 1f;
                float dist = -2f;
                directionalLight.transform.position = dist * estimateData.DirectionalLight.Direction;
                directionalLight.transform.LookAt(Vector3.zero);
                directionalLightArrow.transform.rotation = Quaternion.LookRotation(estimateData.DirectionalLight.Direction);
            }
            
            if (useSphericalHarmonics)
            {
                SphericalHarmonicsL2 sphericalHarmonics = new();

                for(int i = 0; i<9; i++)
                {
                    sphericalHarmonics[0, i] = estimateData.HarmonicsCoefficients[i * 3];
                    sphericalHarmonics[1, i] = estimateData.HarmonicsCoefficients[(i * 3) + 1];
                    sphericalHarmonics[2, i] = estimateData.HarmonicsCoefficients[(i * 3) + 2];
                }
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientProbe = sphericalHarmonics;
            }

            if (runEnvironmentMap)
            {
                cubemap = lightEstimationFeature.GetEstimateCubemap(estimateData.CubeMap.Pixels, (int)estimateData.CubeMap.FaceDimension);
                if (!skyboxMaterial)
                    return;
                UpdateSkybox(cubemap);
            }
        }
        
        private void ChooseEnvironmentMapResolution(HDRCubemapFaceResolution res)
        {
            if (lightEstimationFeature.LightEstimationCreated)
                lightEstimationFeature.DestroyLightEstimation();

            lightEstimationFeature.CreateLightEstimation(res);
        }

        private void SwapSkybox()
        {
            if (!skyboxMaterial)
                return;

            if (runEnvironmentMap)
            {
                oldSkyboxMaterial = RenderSettings.skybox;
                RenderSettings.skybox = skyboxMaterial;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                DynamicGI.UpdateEnvironment();
            }
            else
            {
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
                RenderSettings.skybox = oldSkyboxMaterial;
                DynamicGI.UpdateEnvironment();
            }
        }

        private void UpdateSkybox(Cubemap cubemapTexture)
        {
            if (environmentUpdateQueued)
                return;
            environmentUpdateQueued = true;
            StartCoroutine(DeferEnvironmentUpdate(cubemapTexture));
        }

        private IEnumerator DeferEnvironmentUpdate(Cubemap cubemapTexture)
        {
            while (runEnvironmentMap)
            {
                if (environmentUpdateQueued)
                {
                    skyboxMaterial.SetTexture(skyboxTextureKey, cubemapTexture);
                    RenderSettings.customReflectionTexture = cubemapTexture;
                    DynamicGI.UpdateEnvironment();
                }

                //Add a short yield here to prevent potential issues from rapid DGI updates 
                environmentUpdateQueued = false;
                yield return new WaitForSeconds(1.0f);
                yield break;
            }
        }
    }
}
