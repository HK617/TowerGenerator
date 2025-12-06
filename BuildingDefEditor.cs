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
    SerializedProperty buildByCraftedKit;
    SerializedProperty cellsWidth;
    SerializedProperty cellsHeight;
    SerializedProperty rebuildAfterPlace;
    SerializedProperty allowRotation;
    SerializedProperty buildCostsProp;

    void OnEnable()
    {
        displayName = serializedObject.FindProperty("displayName");
        icon = serializedObject.FindProperty("icon");
        prefab = serializedObject.FindProperty("prefab");
        isHexTile = serializedObject.FindProperty("isHexTile");
        hotkey = serializedObject.FindProperty("hotkey");
        buildByCraftedKit = serializedObject.FindProperty("buildByCraftedKit");
        cellsWidth = serializedObject.FindProperty("cellsWidth");
        cellsHeight = serializedObject.FindProperty("cellsHeight");
        rebuildAfterPlace = serializedObject.FindProperty("rebuildAfterPlace");
        allowRotation = serializedObject.FindProperty("allowRotation");
        buildCostsProp = serializedObject.FindProperty("buildCosts");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var def = (BuildingDef)target;
        if (def == null)
            return;

        // ==== UI 表示 ====
        EditorGUILayout.LabelField("UI 表示", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(displayName);
        EditorGUILayout.PropertyField(icon);

        // ==== プレハブ ====
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("プレハブ", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(prefab);

        // ==== フラグ / 基本 ====
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("フラグ / 基本設定", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(isHexTile, new GUIContent("Is Hex Tile"));
        EditorGUILayout.PropertyField(hotkey, new GUIContent("Hotkey"));
        EditorGUILayout.PropertyField(
            buildByCraftedKit,
            new GUIContent(
                "Build By Crafted Kit",
                "ON にすると、この建物はクラフトで作ったキットを消費して建てます。"
            )
        );
        EditorGUILayout.PropertyField(allowRotation, new GUIContent("Allow Rotation"));
        EditorGUILayout.PropertyField(rebuildAfterPlace, new GUIContent("Rebuild FlowField After Place"));

        // ==== 形状 ====
        EditorGUILayout.Space();
        DrawShapeEditor(def);

        // ==== 建築コスト ====
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("建築コスト", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(buildCostsProp, includeChildren: true);

        serializedObject.ApplyModifiedProperties();
    }

    void DrawShapeEditor(BuildingDef def)
    {
        EditorGUILayout.LabelField("FlowField Block Shape", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(cellsWidth, new GUIContent("Cells Width"));
        EditorGUILayout.PropertyField(cellsHeight, new GUIContent("Cells Height"));

        if (def.shape == null)
        {
            def.RestoreShape();
        }

        int gridSize = Mathf.Max(1, def.GetShapeSize());

        // グリッドサイズ変更
        int newSize = EditorGUILayout.IntSlider("Shape Size", gridSize, 1, 9);
        if (newSize != gridSize)
        {
            Undo.RecordObject(def, "Change Shape Size");
            def.RecreateShape(newSize, true);
            EditorUtility.SetDirty(def);
            gridSize = newSize;
        }

        var shape = def.shape;
        if (shape == null)
        {
            def.RestoreShape();
            shape = def.shape;
            if (shape == null)
                return;
        }

        GUILayout.BeginVertical("box");
        GUILayout.Label($"Shape ({gridSize}×{gridSize})", EditorStyles.boldLabel);

        float boxSize = Mathf.Clamp(150f / gridSize, 12f, 30f);
        Color oldColor = GUI.backgroundColor;
        Color onColor = new Color(0.3f, 0.8f, 1.0f);
        Color offColor = new Color(0.15f, 0.15f, 0.15f);

        // y を上から描画したいので逆順
        for (int y = gridSize - 1; y >= 0; y--)
        {
            GUILayout.BeginHorizontal();
            for (int x = 0; x < gridSize; x++)
            {
                bool val = shape[x, y];
                GUI.backgroundColor = val ? onColor : offColor;

                bool newVal = GUILayout.Toggle(
                    val,
                    GUIContent.none,
                    "Button",
                    GUILayout.Width(boxSize),
                    GUILayout.Height(boxSize)
                );

                if (newVal != val)
                {
                    Undo.RecordObject(def, "Toggle Shape Cell");
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
