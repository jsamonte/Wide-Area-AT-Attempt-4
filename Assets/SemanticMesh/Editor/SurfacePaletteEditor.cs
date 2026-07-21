using UnityEditor;
using UnityEngine;

namespace SemanticMesh.EditorTools
{
    // Friendlier inspector for the palette (roadmap 9.6). It presents the grid
    // size-vs-distance controls as plain "near cell / far cell / fade from / fade
    // to" per category, all in this ONE place, instead of the raw coarsen
    // multiplier. Edits apply to every surface in the open scene immediately:
    // SurfacePalette raises its changed event on validate and each SemanticSurface
    // ([ExecuteAlways]) re-pulls, so there is no Play mode and no re-selecting.
    //
    // Adding or removing a CATEGORY is still a code + asset edit (see guide
    // section 10); this inspector only tunes the existing rows.
    [CustomEditor(typeof(SurfacePalette))]
    public class SurfacePaletteEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "One source of truth for every category's grid look. Edits apply live to all surfaces in the open scene, no Play mode.",
                MessageType.Info);

            var entries = serializedObject.FindProperty("entries");
            for (int i = 0; i < entries.arraySize; i++)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                var type = entry.FindPropertyRelative("surface_type");
                var color = entry.FindPropertyRelative("grid_color");
                var style = entry.FindPropertyRelative("grid_style");
                var near = entry.FindPropertyRelative("cell_size");
                var far = entry.FindPropertyRelative("far_cell_size");
                var start = entry.FindPropertyRelative("lod_start");
                var end = entry.FindPropertyRelative("lod_end");

                EditorGUILayout.Space();
                string title = ((SurfaceType)type.enumValueIndex).ToString();
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(color, new GUIContent("Grid color"));
                EditorGUILayout.PropertyField(style, new GUIContent("Style", "Square (sharp), Rounded (soft), or Criss-cross (diagonal)."));
                EditorGUILayout.PropertyField(near, new GUIContent("Near cell (m)", "Cell size up close. Smaller = denser."));
                EditorGUILayout.PropertyField(far, new GUIContent("Far cell (m)", "Cell size far away (at Fade to and beyond). Must be >= near."));
                if (far.floatValue < near.floatValue)
                {
                    far.floatValue = near.floatValue;
                }
                EditorGUILayout.PropertyField(start, new GUIContent("Fade from (m)", "Distance where cells start coarsening from near to far."));
                EditorGUILayout.PropertyField(end, new GUIContent("Fade to (m)", "Distance where cells reach the far size."));
                if (end.floatValue < start.floatValue)
                {
                    end.floatValue = start.floatValue;
                }
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
