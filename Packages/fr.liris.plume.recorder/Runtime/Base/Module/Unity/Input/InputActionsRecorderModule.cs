#if INPUT_SYSTEM_ENABLED
using System;
using System.Collections.Generic;
using System.Linq;
using PLUME.Core.Recorder;
using PLUME.Core.Recorder.Module;
using PLUME.Core.Utils;
using PLUME.Sample.Unity;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Scripting;
using InputAction = UnityEngine.InputSystem.InputAction;
using InputActionType = UnityEngine.InputSystem.InputActionType;

namespace PLUME.Base.Module.Unity.Input
{
    [Preserve]
    public class InputActionsRecorderModule : RecorderModule
    {
        private RecorderContext _ctx;
        private List<InputAction> _enabledActions;

        protected override void OnStartRecording(RecorderContext ctx)
        {
            base.OnStartRecording(ctx);
            _ctx = ctx;

            _enabledActions = InputSystem.ListEnabledActions();

            foreach (var action in _enabledActions)
            {
                action.performed += OnActionPerformed;
            }
        }

        protected override void OnStopRecording(RecorderContext ctx)
        {
            base.OnStopRecording(ctx);
            
            foreach (var action in _enabledActions)
            {
                action.performed -= OnActionPerformed;
            }
        }
        
        private void OnActionPerformed(InputAction.CallbackContext context)
        {
            if (!_ctx.IsRecording)
                return;

            // PATCHED (local fork): actionMap is NULL for singleton actions -- actions created inline rather
            // than inside an InputActionAsset. Unity's own TrackedPoseDriver makes exactly these when its
            // Position/Rotation inputs are not backed by an InputActionReference, so a standard XR rig throws
            // here on EVERY head-pose event. This line sits outside the try block below, so nothing caught it:
            // on 2026-08-22 it produced ~480 NullReferenceExceptions/sec for a whole session, saturating the
            // logcat ring buffer down to 0.6s of retained history. Fall back to the action's own name.
            var actionMap = context.action.actionMap;

            var inputActionSample = new Sample.Unity.InputAction
            {
                Name = actionMap == null ? context.action.name : actionMap.name + '/' + context.action.name
            };
            inputActionSample.BindingPaths.AddRange(context.action.bindings.Select(b => b.path));

            // TODO: add support for composite actions
            try
            {
                switch (context.action.type)
                {
                    case InputActionType.Value:
                        inputActionSample.Type = Sample.Unity.InputActionType.Value;
                        break;
                    case InputActionType.Button:
                        inputActionSample.Type = Sample.Unity.InputActionType.Button;
                        break;
                    case InputActionType.PassThrough:
                        inputActionSample.Type = Sample.Unity.InputActionType.Passthrough;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (context.action.type == InputActionType.Button)
                {
                    var b = context.ReadValueAsButton();
                    var f = context.ReadValue<float>();

                    var buttonValue = new ButtonValue
                    {
                        Boolean = b,
                        Float = f
                    };

                    if (context.control is ButtonControl btnControl)
                        buttonValue.Threshold = btnControl.pressPointOrDefault;
                    else
                        buttonValue.Threshold = InputSystem.settings.defaultButtonPressPoint;
                }
                else
                {
                    var value = context.ReadValueAsObject();

                    switch (value)
                    {
                        case bool b:
                            inputActionSample.Boolean = b;
                            break;
                        case int i:
                            inputActionSample.Integer = i;
                            break;
                        case float f:
                            inputActionSample.Float = f;
                            break;
                        case double d:
                            inputActionSample.Double = d;
                            break;
                        case Vector2 vec2:
                            inputActionSample.Vector2 = vec2.ToPayload();
                            break;
                        case Vector3 vec3:
                            inputActionSample.Vector3 = vec3.ToPayload();
                            break;
                        case Quaternion q:
                            inputActionSample.Quaternion = q.ToPayload();
                            break;
                    }
                }

                _ctx.CurrentRecord.RecordTimestampedManagedSample(inputActionSample);
            } catch (Exception e)
            {
                Debug.LogWarning($"Failed to record input action: {e}");
            }
        }
    }
}
#endif
