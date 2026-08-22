using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityRuntimeGuid.Editor
{
    [CustomEditor(typeof(GameObject))]
    [CanEditMultipleObjects]
    public class GameObjectEditorExtension : UnityEditor.Editor
    {
        private static readonly Type GameObjectInspectorType;
        private static readonly Action<UnityEditor.Editor> OnHeaderGUIFunc;

        private UnityEditor.Editor _baseEditor;
        
        private GameObject _gameObject;
        
        private Texture _defaultIcon;
        private Texture _clipboardIcon;

        private bool _showGuids;

        static GameObjectEditorExtension()
        {
            GameObjectInspectorType = Type.GetType("UnityEditor.GameObjectInspector, UnityEditor");

            if (GameObjectInspectorType == null)
                throw new Exception("UnityEditor.GameObjectInspector not found");

            var methodInfo =
                GameObjectInspectorType.GetMethod("OnHeaderGUI", BindingFlags.NonPublic | BindingFlags.Instance);

            if (methodInfo == null)
                throw new Exception("OnHeaderGUI was not found in UnityEditor.GameObjectInspector");

            OnHeaderGUIFunc = editor => { methodInfo.Invoke(editor, null); };
        }

        private void OnEnable()
        {
            _defaultIcon = EditorGUIUtility.IconContent("cs Script Icon").image;
            _clipboardIcon = EditorGUIUtility.IconContent("Clipboard").image;
            _baseEditor = CreateEditor(targets, GameObjectInspectorType);
            _gameObject = target as GameObject;
        }

        private void OnDisable()
        {
            // When OnDisable is called, the default editor we created should be destroyed to avoid memory leakage.
            // Also, make sure to call any required methods like OnDisable
            var disableMethod = _baseEditor.GetType().GetMethod("OnDisable",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            var propertyInfo = typeof(SerializedObject).GetProperty("isValid", 
                BindingFlags.NonPublic | // Internal is non-public
                BindingFlags.Instance);
            var getter = propertyInfo.GetGetMethod(true);
            
            var isValid = (bool)getter!.Invoke(_baseEditor.serializedObject, null);

            if (disableMethod != null && isValid)
                disableMethod.Invoke(_baseEditor, null);

            DestroyImmediate(_baseEditor);
        }

        protected override void OnHeaderGUI()
        {
            OnHeaderGUIFunc(_baseEditor);
        }

        private void DrawGuidEntry(string objName, Texture icon, string guid)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));
            EditorGUILayout.TextField(objName, guid);

            GUI.enabled = true;
            if (GUILayout.Button(_clipboardIcon, GUILayout.Width(24)))
                EditorGUIUtility.systemCopyBuffer = guid;
            GUI.enabled = false;

            EditorGUILayout.EndHorizontal();
        }

        private Texture GetIconOrDefault(UnityEngine.Object obj)
        {
            var icon = EditorGUIUtility.ObjectContent(null, obj.GetType()).image;
            return icon != null ? icon : _defaultIcon;
        }

        public override void OnInspectorGUI()
        {
            _baseEditor.OnInspectorGUI();

            if (targets.Length > 1)
                return;

            _showGuids = EditorGUILayout.Foldout(_showGuids, "GUIDs", true, EditorStyles.foldoutHeader);

            if (_showGuids)
            {
                var sceneGuidRegistry = SceneGuidRegistry.GetOrCreate(_gameObject.scene);
                var gameObjectEntry = sceneGuidRegistry.GetOrCreateEntry(_gameObject);

                GUI.enabled = false;
                DrawGuidEntry("GameObject (Self)", GetIconOrDefault(_gameObject), gameObjectEntry.guid);

                foreach (var component in _gameObject.GetComponents<Component>())
                {
                    var componentEntry = sceneGuidRegistry.GetOrCreateEntry(component);
                    DrawGuidEntry(component.GetType().Name, GetIconOrDefault(component), componentEntry.guid);
                }

                GUI.enabled = true;
            }

            GUILayout.Space(5);
        }
    }
}