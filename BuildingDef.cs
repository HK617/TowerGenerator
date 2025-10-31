using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "BuildingDef", menuName = "Game/Building Def")]
public class BuildingDef : ScriptableObject
{
    [Header("UI表示")]
    public string displayName;
    public Sprite icon;

    [Header("配置プレハブ")]
    public GameObject prefab;

    [Header("Flags")]
    public bool isHexTile = false;
    [Range(1, 9)] public int hotkey = 1;

    [Header("FlowField Block Shape")]
    [Tooltip("この建物が論理上何セル×何セルふさぐか")]
    public int cellsWidth = 1;
    public int cellsHeight = 1;

    [Tooltip("shape の実際のグリッドサイズ。3にすると3×3、5にすると5×5")]
    public int shapeSize = 3;

    [Tooltip("shapeSize * shapeSize 個ぶんをフラットに持つ。true=ブロック")]
    public List<bool> shapeData = new List<bool>();

    [Tooltip("配置/削除のたびにFlowFieldを作り直すか")]
    public bool rebuildAfterPlace = true;

    // ========== ヘルパー ==========

    public void EnsureShape(int newSize)
    {
        if (newSize < 1) newSize = 1;
        shapeSize = newSize;
        int need = newSize * newSize;
        if (shapeData == null) shapeData = new List<bool>(need);
        if (shapeData.Count < need)
        {
            while (shapeData.Count < need) shapeData.Add(false);
        }
        else if (shapeData.Count > need)
        {
            shapeData.RemoveRange(need, shapeData.Count - need);
        }
    }

    // x:0..shapeSize-1, y:0..shapeSize-1 (下が0, 上がshapeSize-1想定)
    public bool GetShape(int x, int y)
    {
        if (shapeData == null || shapeData.Count == 0) return false;
        if (x < 0 || y < 0 || x >= shapeSize || y >= shapeSize) return false;
        int idx = y * shapeSize + x;
        if (idx < 0 || idx >= shapeData.Count) return false;
        return shapeData[idx];
    }

    public void SetShape(int x, int y, bool val)
    {
        if (x < 0 || y < 0 || x >= shapeSize || y >= shapeSize) return;
        int idx = y * shapeSize + x;
        EnsureShape(shapeSize);
        shapeData[idx] = val;
    }
}
