using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(BuildingDef))]
public class BuildingDefEditor : Editor
{
    SerializedProperty displayName;
    SerializedProperty icon;
    SerializedProperty prefab;
    SerializedProperty isHexTile;
    SerializedProperty hotkey;
    SerializedProperty cellsWidth;
    SerializedProperty cellsHeight;
    SerializedProperty rebuildAfterPlace;

    BuildingDef def;

    void OnEnable()
    {
        def = (BuildingDef)target;
        displayName = serializedObject.FindProperty("displayName");
        icon = serializedObject.FindProperty("icon");
        prefab = serializedObject.FindProperty("prefab");
        isHexTile = serializedObject.FindProperty("isHexTile");
        hotkey = serializedObject.FindProperty("hotkey");
        cellsWidth = serializedObject.FindProperty("cellsWidth");
        cellsHeight = serializedObject.FindProperty("cellsHeight");
        rebuildAfterPlace = serializedObject.FindProperty("rebuildAfterPlace");

        if (def.shapeData == null || def.shapeData.Count == 0)
        {
            def.EnsureShape(3);
            EditorUtility.SetDirty(def);
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(displayName);
        EditorGUILayout.PropertyField(icon);
        EditorGUILayout.PropertyField(prefab);
        EditorGUILayout.PropertyField(isHexTile);
        EditorGUILayout.PropertyField(hotkey);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("FlowField Block Shape", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("赤=ブロック(通行不可)、緑=通行可。グリッドサイズは奇数・偶数どちらも選択できます。", MessageType.Info);

        // グリッドサイズ選択（2〜9）
        int curSize = def.shapeSize;
        int[] sizes = { 2, 3, 4, 5, 6, 7, 8, 9 };
        string[] labels = { "2×2", "3×3", "4×4", "5×5", "6×6", "7×7", "8×8", "9×9" };
        int newSize = EditorGUILayout.IntPopup("Grid Size", curSize, labels, sizes);
        if (newSize != curSize)
        {
            if (EditorUtility.DisplayDialog(
                "Grid Size Change",
                $"グリッドを {curSize}×{curSize} から {newSize}×{newSize} に変更します。\n既存の形状データはトリミング/拡張されます。",
                "OK", "キャンセル"))
            {
                def.EnsureShape(newSize);
                EditorUtility.SetDirty(def);
            }
        }

        DrawShapeGrid(def);

        EditorGUILayout.Space(10);
        EditorGUILayout.PropertyField(cellsWidth);
        EditorGUILayout.PropertyField(cellsHeight);
        EditorGUILayout.PropertyField(rebuildAfterPlace);

        serializedObject.ApplyModifiedProperties();

        if (GUI.changed)
            EditorUtility.SetDirty(def);
    }

    void DrawShapeGrid(BuildingDef def)
    {
        int s = def.shapeSize;
        float boxSize = Mathf.Clamp(150f / s, 12f, 35f);
        Color old = GUI.backgroundColor;

        GUILayout.BeginVertical("box");
        GUILayout.Label($"Shape ({s}×{s})", EditorStyles.boldLabel);

        // 上から下へ並べたいので y を後ろから
        for (int y = s - 1; y >= 0; y--)
        {
            GUILayout.BeginHorizontal();
            for (int x = 0; x < s; x++)
            {
                bool val = def.GetShape(x, y);
                GUI.backgroundColor = val ? new Color(1f, 0.4f, 0.4f) : new Color(0.4f, 1f, 0.4f);

                bool newVal = GUILayout.Toggle(val, "", "Button", GUILayout.Width(boxSize), GUILayout.Height(boxSize));
                if (newVal != val)
                {
                    Undo.RecordObject(def, "Toggle Shape Cell");
                    def.SetShape(x, y, newVal);
                    EditorUtility.SetDirty(def);
                }

                GUI.backgroundColor = old;
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
    }
}
