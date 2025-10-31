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

    // 🟨 ここから追加部分 ---------------------

    [Header("FlowField Block Shape")]
    [Tooltip("この建物が占有するセル範囲 (cellSize単位)。たとえば 0.25×0.25 セルなら 1×1、0.5×0.5 なら 2×2。")]
    public int cellsWidth = 1;
    public int cellsHeight = 1;

    [Tooltip("形状パターン（Center基準）。例: 3x3で中央だけ空けたいなどに使う。")]
    public bool[,] shape;

    [Tooltip("設置したらFlowFieldを即Rebuildするか。falseにするとまとめて軽くできる。")]
    public bool rebuildAfterPlace = true;

    // shape を簡単に設定できる補助
    public bool IsCellBlocked(int x, int y)
    {
        if (shape == null) return true; // shape未設定なら全ブロック扱い
        if (x < 0 || y < 0 || x >= shape.GetLength(0) || y >= shape.GetLength(1))
            return true;
        return shape[x, y];
    }
}
