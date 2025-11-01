using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class HexTilemapFiller : MonoBehaviour
{
    [Header("Targets")]
    public Tilemap tilemap;      // Hexagonal Tilemap を割り当て
    public TileBase tile;        // あなたの HexagonalGrid タイル

    [Header("Area (tiles)")]
    [Min(1)] public int width = 20;
    [Min(1)] public int height = 20;

    [Header("Placement")]
    public bool centerOnOrigin = true;     // 原点を中心に敷く
    public Vector3Int start = Vector3Int.zero; // centerOff のときの開始座標

    [Header("Options")]
    public bool clearBeforeGenerate = true;
    public bool generateOnStart = false;

    void Reset()
    {
        if (!tilemap) tilemap = GetComponent<Tilemap>();
    }

    void Start()
    {
        // Editor でも実行したい場合は ExecuteAlways で動きます
        if (Application.isPlaying && generateOnStart)
            Generate();
    }

    [ContextMenu("Generate")]
    public void Generate()
    {
        if (!tilemap)
        {
            Debug.LogError("[HexTilemapFiller] Tilemap を割り当ててください。");
            return;
        }
        if (!tile)
        {
            Debug.LogError("[HexTilemapFiller] 敷き詰める Tile を割り当ててください。");
            return;
        }

        if (clearBeforeGenerate) Clear();

        // 敷き詰め開始セル
        Vector3Int origin = centerOnOrigin
            ? new Vector3Int(-width / 2, -height / 2, 0)
            : start;

        // まとめて配置（SetTilesBlock）で高速
        var bounds = new BoundsInt(origin.x, origin.y, 0, width, height, 1);
        var tiles = new TileBase[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = x + y * width;
                tiles[i] = tile;
            }
        }

        tilemap.SetTilesBlock(bounds, tiles);
        tilemap.RefreshAllTiles();
    }

    [ContextMenu("Clear")]
    public void Clear()
    {
        if (!tilemap) return;
        tilemap.ClearAllTiles();
    }
}
