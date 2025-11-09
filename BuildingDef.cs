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
    [Tooltip("この建物が占有するセル範囲 (cellSize単位)。たとえば 0.25×0.25 セルなら 1×1、0.5×0.5 なら 2×2。")]
    public int cellsWidth = 1;
    public int cellsHeight = 1;

    [Tooltip("設置したらFlowFieldを即Rebuildするか。falseにするとまとめて軽くできる。")]
    public bool rebuildAfterPlace = true;

    [Tooltip("回転可能なオブジェクトかどうか。TrueにするとRキーで回転させられる")]
    public bool allowRotation = false;

    // ========= ここから保存用バックアップ =========
    // Unity は bool[,] を保存できないので、フラットなリストにしておく
    [SerializeField, HideInInspector] int shapeSize = 3;
    [SerializeField, HideInInspector] List<bool> shapeFlat = new();

    // ゲーム中に実際に使う2次元配列（これはシリアライズしない）
    [System.NonSerialized]
    public bool[,] shape;

    void OnEnable()
    {
        RestoreShape();
    }

    void OnValidate()
    {
        // インスペクタで値を変えた時にも形を再構築しておく
        RestoreShape();
    }

    // フラットなリストから bool[,] を組み立てる
    public void RestoreShape()
    {
        if (shapeSize <= 0)
            shapeSize = 3;

        if (shapeFlat == null || shapeFlat.Count != shapeSize * shapeSize)
        {
            // データが壊れていたら 3x3 を全部 true にする
            shapeFlat = new List<bool>(shapeSize * shapeSize);
            for (int i = 0; i < shapeSize * shapeSize; i++)
                shapeFlat.Add(true);
        }

        shape = new bool[shapeSize, shapeSize];
        for (int y = 0; y < shapeSize; y++)
        {
            for (int x = 0; x < shapeSize; x++)
            {
                int idx = y * shapeSize + x;
                shape[x, y] = shapeFlat[idx];
            }
        }
    }

    // Editor から呼んで「新しいサイズで作り直す」用
    public void RecreateShape(int newSize, bool defaultValue = true)
    {
        shapeSize = newSize;
        shapeFlat = new List<bool>(newSize * newSize);
        for (int i = 0; i < newSize * newSize; i++)
            shapeFlat.Add(defaultValue);

        RestoreShape();
    }

    // Editor がセルを1つ書き換えたときに呼ぶ
    public void SetShapeCell(int x, int y, bool value)
    {
        if (shape == null) RestoreShape();
        if (x < 0 || y < 0 || x >= shapeSize || y >= shapeSize) return;

        shape[x, y] = value;

        int idx = y * shapeSize + x;
        if (shapeFlat == null || shapeFlat.Count != shapeSize * shapeSize)
        {
            // 念のため整列
            shapeFlat = new List<bool>(shapeSize * shapeSize);
            for (int i = 0; i < shapeSize * shapeSize; i++)
                shapeFlat.Add(true);
        }
        shapeFlat[idx] = value;
    }

    public int GetShapeSize() => shapeSize;

    // ここから下はもともとのAPI
    public bool IsCellBlocked(int x, int y)
    {
        if (shape == null)
        {
            RestoreShape();
            if (shape == null) return true;
        }

        if (x < 0 || y < 0 || x >= shape.GetLength(0) || y >= shape.GetLength(1))
            return true;
        return shape[x, y];
    }
}
