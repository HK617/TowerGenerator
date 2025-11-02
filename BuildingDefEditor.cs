using UnityEngine;
using UnityEditor;

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

        // シリアライズされたデータ → 実体配列 に起こす
        def.RestoreShape();
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
        EditorGUILayout.HelpBox("赤=ブロック(通行不可)、緑=通行可。\nグリッドサイズは奇数・偶数どちらも選択可。", MessageType.Info);

        // 今のサイズ
        int currentSize = def.GetShapeSize();

        // サイズ選択
        int[] sizeOptions = {1, 2, 3, 4, 5, 6, 7, 8, 9, 20 };
        string[] sizeLabels = { "1×1", "2×2", "3×3", "4×4", "5×5", "6×6", "7×7", "8×8", "9×9", "20×20" };
        int newSize = EditorGUILayout.IntPopup("Grid Size", currentSize, sizeLabels, sizeOptions);

        if (newSize != currentSize)
        {
            if (EditorUtility.DisplayDialog(
                "Grid Size Change",
                $"グリッドを {currentSize}×{currentSize} から {newSize}×{newSize} に変更します。\n既存の形状データは消去されます。",
                "OK", "キャンセル"))
            {
                Undo.RecordObject(def, "Resize Shape");
                def.RecreateShape(newSize, defaultValue: true);
                EditorUtility.SetDirty(def);
            }
        }

        DrawShapeGrid();

        EditorGUILayout.Space(10);
        EditorGUILayout.PropertyField(cellsWidth);
        EditorGUILayout.PropertyField(cellsHeight);
        EditorGUILayout.PropertyField(rebuildAfterPlace);

        serializedObject.ApplyModifiedProperties();

        if (GUI.changed)
            EditorUtility.SetDirty(def);
    }

    void DrawShapeGrid()
    {
        def.RestoreShape();
        var shape = def.shape;
        if (shape == null) return;
        int gridSize = shape.GetLength(0);

        GUILayout.BeginVertical("box");
        GUILayout.Label($"Shape ({gridSize}×{gridSize}):", EditorStyles.boldLabel);

        float boxSize = Mathf.Clamp(150f / gridSize, 12f, 30f);
        Color oldColor = GUI.backgroundColor;

        // yを上から描画したいので逆順
        for (int y = gridSize - 1; y >= 0; y--)
        {
            GUILayout.BeginHorizontal();
            for (int x = 0; x < gridSize; x++)
            {
                bool val = shape[x, y];
                GUI.backgroundColor = val ? new Color(1f, 0.4f, 0.4f) : new Color(0.4f, 1f, 0.4f);

                bool newVal = GUILayout.Toggle(val, "", "Button",
                    GUILayout.Width(boxSize), GUILayout.Height(boxSize));

                if (newVal != val)
                {
                    Undo.RecordObject(def, "Change Shape");
                    def.SetShapeCell(x, y, newVal);
                    EditorUtility.SetDirty(def);
                }

                GUI.backgroundColor = oldColor;
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
    }
}
