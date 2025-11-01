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
    public Tilemap tilemap;      // Hexagonal Tilemap �����蓖��
    public TileBase tile;        // ���Ȃ��� HexagonalGrid �^�C��

    [Header("Area (tiles)")]
    [Min(1)] public int width = 20;
    [Min(1)] public int height = 20;

    [Header("Placement")]
    public bool centerOnOrigin = true;     // ���_�𒆐S�ɕ~��
    public Vector3Int start = Vector3Int.zero; // centerOff �̂Ƃ��̊J�n���W

    [Header("Options")]
    public bool clearBeforeGenerate = true;
    public bool generateOnStart = false;

    void Reset()
    {
        if (!tilemap) tilemap = GetComponent<Tilemap>();
    }

    void Start()
    {
        // Editor �ł����s�������ꍇ�� ExecuteAlways �œ����܂�
        if (Application.isPlaying && generateOnStart)
            Generate();
    }

    [ContextMenu("Generate")]
    public void Generate()
    {
        if (!tilemap)
        {
            Debug.LogError("[HexTilemapFiller] Tilemap �����蓖�ĂĂ��������B");
            return;
        }
        if (!tile)
        {
            Debug.LogError("[HexTilemapFiller] �~���l�߂� Tile �����蓖�ĂĂ��������B");
            return;
        }

        if (clearBeforeGenerate) Clear();

        // �~���l�ߊJ�n�Z��
        Vector3Int origin = centerOnOrigin
            ? new Vector3Int(-width / 2, -height / 2, 0)
            : start;

        // �܂Ƃ߂Ĕz�u�iSetTilesBlock�j�ō���
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
