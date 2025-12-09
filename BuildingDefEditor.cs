using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(BuildingDef))]
public class BuildingDefEditor : Editor
{
    // ==== SerializedProperty ====
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

    // ★ 追加：敵用パス関連
    SerializedProperty destructibleForEnemy;
    SerializedProperty breakCostForEnemy;

    SerializedProperty buildCosts;

    bool shapeFoldout = true;

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

        // ★ ここで新フィールドを捕まえる
        destructibleForEnemy = serializedObject.FindProperty("destructibleForEnemy");
        breakCostForEnemy = serializedObject.FindProperty("breakCostForEnemy");

        buildCosts = serializedObject.FindProperty("buildCosts");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var def = (BuildingDef)target;

        // ========= 基本情報 =========
        EditorGUILayout.LabelField("UI表示", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(displayName);
        EditorGUILayout.PropertyField(icon);

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("配置プレハブ", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(prefab);

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Flags", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(isHexTile);
        EditorGUILayout.PropertyField(hotkey);
        EditorGUILayout.PropertyField(buildByCraftedKit);

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("FlowField Block Shape", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(cellsWidth);
        EditorGUILayout.PropertyField(cellsHeight);
        EditorGUILayout.PropertyField(rebuildAfterPlace);
        EditorGUILayout.PropertyField(allowRotation);

        EditorGUILayout.Space();

        // ========= ★ Enemy Path / Destruction =========
        EditorGUILayout.LabelField("Enemy Path / Destruction", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(destructibleForEnemy,
            new GUIContent("Destructible For Enemy", "敵が壊して通れるブロックかどうか"));
        using (new EditorGUI.DisabledScope(!destructibleForEnemy.boolValue))
        {
            EditorGUILayout.PropertyField(breakCostForEnemy,
                new GUIContent("Break Cost For Enemy",
                "敵がこのブロックを壊して通るときの追加コスト（大きいほど壊しにくい）"));
        }

        EditorGUILayout.Space();

        // ========= 建築コスト =========
        EditorGUILayout.LabelField("建築コスト", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(buildCosts, true);

        EditorGUILayout.Space();

        // ========= Shape 編集 =========
        DrawShapeEditor(def);

        serializedObject.ApplyModifiedProperties();
    }

    void DrawShapeEditor(BuildingDef def)
    {
        shapeFoldout = EditorGUILayout.Foldout(shapeFoldout, "Block Shape (FlowField用)", true);
        if (!shapeFoldout) return;

        // shape の実体を確保
        def.RestoreShape();
        var shape = def.shape;
        int gridSize = def.GetShapeSize();
        if (shape == null || gridSize <= 0) return;

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField($"Shape ({gridSize} x {gridSize})", EditorStyles.boldLabel);

        float buttonSize = 22f;
        float spacing = 2f;

        // 上から下へ表示したいので y を逆順に回す
        for (int y = gridSize - 1; y >= 0; y--)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            for (int x = 0; x < gridSize; x++)
            {
                bool val = shape[x, y];

                Color old = GUI.backgroundColor;
                GUI.backgroundColor = val ? Color.red : Color.gray;

                if (GUILayout.Button("", GUILayout.Width(buttonSize), GUILayout.Height(buttonSize)))
                {
                    Undo.RecordObject(def, "Toggle Shape Cell");
                    def.SetShapeCell(x, y, !val);
                    EditorUtility.SetDirty(def);
                    def.RestoreShape(); // 内部配列を更新
                    shape = def.shape;
                }

                GUI.backgroundColor = old;
                GUILayout.Space(spacing);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(spacing);
        }

        EditorGUILayout.EndVertical();
    }
}
