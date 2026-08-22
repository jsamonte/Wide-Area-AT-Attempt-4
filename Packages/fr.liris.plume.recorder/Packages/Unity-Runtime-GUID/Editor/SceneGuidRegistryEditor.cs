using UnityEditor;
using UnityEngine;

namespace UnityRuntimeGuid.Editor
{
    [CustomEditor(typeof(SceneGuidRegistry))]
    public class SceneGuidRegistryEditor : UnityEditor.Editor
    {
        private SerializedProperty _registryEntries;

        private string _searchName = "";
        private string _searchType = "";
        private string _searchGuid = "";

        private void OnEnable()
        {
            _registryEntries = serializedObject.FindProperty("registry").FindPropertyRelative("entries");
        }

        public override void OnInspectorGUI()
        {
            var toolbarSearchCancelStyle = GUI.skin.FindStyle("ToolbarSeachCancelButton") ?? GUI.skin.FindStyle("ToolbarSearchCancelButton");

            serializedObject.Update();

            var sceneGuidRegistry = (SceneGuidRegistry)target;
            
            GUI.enabled = false;
            EditorGUILayout.TextField("Scene GUID", sceneGuidRegistry.SceneGuid);
            GUI.enabled = true;

            if (GUILayout.Button("Update"))
                GuidRegistryUpdater.UpdateScenesGuidRegistry(new[] { sceneGuidRegistry.gameObject.scene.path });
            
            if (GUILayout.Button("Clear"))
                GuidRegistryUpdater.ClearScenesGuidRegistry(new[] { sceneGuidRegistry.gameObject.scene.path });
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search by name:", GUILayout.ExpandWidth(false));
            _searchName = GUILayout.TextField(_searchName, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("", toolbarSearchCancelStyle))
            {
                _searchName = "";
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search by type:", GUILayout.ExpandWidth(false));
            _searchType = GUILayout.TextField(_searchType, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("", toolbarSearchCancelStyle))
            {
                _searchType = "";
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search by GUID:", GUILayout.ExpandWidth(false));
            _searchGuid = GUILayout.TextField(_searchGuid, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("", toolbarSearchCancelStyle))
            {
                _searchGuid = "";
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();

            EditorStyles.label.wordWrap = true;

            _registryEntries.isExpanded =
                EditorGUILayout.BeginFoldoutHeaderGroup(_registryEntries.isExpanded, "Scene objects");

            var visibleEntriesCount = 0;

            if (_registryEntries.isExpanded)
            {
                EditorGUI.indentLevel = 1;

                for (var i = 0; i < _registryEntries.arraySize; ++i)
                {
                    var entry = _registryEntries.GetArrayElementAtIndex(i);

                    var guid = entry.FindPropertyRelative("guid");
                    var @object = entry.FindPropertyRelative("object");
                    
                    if(@object == null || @object.objectReferenceValue == null)
                        continue;
                    
                    var objectType = @object.objectReferenceValue.GetType().Name;
                    var objectName = @object.objectReferenceValue.name;
                    var objectGuid = guid.stringValue;

                    if (!string.IsNullOrEmpty(objectName) && !objectName.ToLower().StartsWith(_searchName.ToLower()))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(objectType) && !objectType.ToLower().StartsWith(_searchType.ToLower()))
                    {
                        continue;
                    }
                    
                    if (!string.IsNullOrEmpty(objectGuid) && !objectGuid.ToLower().StartsWith(_searchGuid.ToLower()))
                    {
                        continue;
                    }

                    visibleEntriesCount++;

                    entry.isExpanded =
                        EditorGUILayout.Foldout(entry.isExpanded, $"{objectName} ({objectType})", true);

                    if (!entry.isExpanded) continue;

                    EditorGUI.indentLevel = 2;
                    GUI.enabled = false;
                    EditorGUILayout.ObjectField("Scene Object", @object.objectReferenceValue, typeof(Object), false);
                    EditorGUILayout.TextField("GUID", guid.stringValue);
                    GUI.enabled = true;
                    
                    if (GUILayout.Button("Remove"))
                    {
                        sceneGuidRegistry.Remove(@object.objectReferenceValue);
                    }
                    
                    EditorGUI.indentLevel = 1;
                }

                if (visibleEntriesCount == 0)
                {
                    GUILayout.Label("No object corresponding to search.");
                }

                EditorGUI.indentLevel = 0;
            }

            EditorGUI.EndFoldoutHeaderGroup();
            serializedObject.ApplyModifiedProperties();
        }
    }
}