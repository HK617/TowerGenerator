using UnityEngine;
using UnityEngine.Tilemaps;

[ExecuteAlways]
public class HexTilemapFiller : MonoBehaviour
{
    [Header("Targets")]
    public Tilemap tilemap;          // Hexagonal Tilemap
    [Tooltip("地面として敷き詰めるタイル")]
    public TileBase groundTile;      // 通常タイル

    [Header("Resource (BuildingDef)")]
    [Tooltip("ランダムで出したい資源の BuildingDef")]
    public BuildingDef resourceDef;
    [Range(0f, 1f)]
    [Tooltip("1マスごとにこの確率で resourceDef を配置する (0=出ない, 1=全部資源)")]
    public float resourceChance = 0.1f;

    [Header("Area (tiles)")]
    [Min(1)] public int width = 20;
    [Min(1)] public int height = 20;

    [Header("Placement")]
    [Tooltip("原点(0,0)を中心に配置するか？ false の場合 start から敷き詰める")]
    public bool centerOnOrigin = true;
    public Vector3Int start = Vector3Int.zero;

    [Header("Options")]
    [Tooltip("Generate 実行時に、既存のタイルと資源オブジェクトを消す")]
    public bool clearBeforeGenerate = true;
    [Tooltip("再生開始時に自動で Generate するか")]
    public bool generateOnStart = false;

    void Reset()
    {
        if (!tilemap) tilemap = GetComponent<Tilemap>();
    }

    void Start()
    {
        // 実際に自動生成したいのはプレイ時だけにしておく
        if (Application.isPlaying && generateOnStart)
        {
            Generate();
        }
    }

    [ContextMenu("Generate")]
    public void Generate()
    {
        if (!tilemap)
        {
            Debug.LogError("[HexTilemapFiller] Tilemap が未設定です。");
            return;
        }
        if (!groundTile)
        {
            Debug.LogError("[HexTilemapFiller] groundTile が未設定です。");
            return;
        }

        if (clearBeforeGenerate)
        {
            ClearTilesAndResources();
        }

        // 敷き詰め開始セル
        Vector3Int origin = centerOnOrigin
            ? new Vector3Int(-width / 2, -height / 2, 0)
            : start;

        BoundsInt bounds = new BoundsInt(origin.x, origin.y, 0, width, height, 1);
        TileBase[] tiles = new TileBase[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = x + y * width;
                tiles[idx] = groundTile;

                // 資源配置判定
                if (resourceDef != null && resourceDef.prefab != null && resourceChance > 0f)
                {
                    if (Random.value < resourceChance)
                    {
                        Vector3Int cell = new Vector3Int(origin.x + x, origin.y + y, 0);
                        Vector3 world = tilemap.CellToWorld(cell) + tilemap.tileAnchor;

                        GameObject obj = Instantiate(resourceDef.prefab, world, Quaternion.identity, transform);
                        obj.name = resourceDef.prefab.name; // "(Clone)" を消しておくと見やすい

                        // セーブ用のマーカーを付与
                        var marker = obj.GetComponent<ResourceMarker>();
                        if (!marker) marker = obj.AddComponent<ResourceMarker>();
                        marker.def = resourceDef;
                    }
                }
            }
        }

        tilemap.SetTilesBlock(bounds, tiles);
        tilemap.RefreshAllTiles();
    }

    [ContextMenu("Clear Tiles + Resources")]
    public void ClearTilesAndResources()
    {
        // タイル削除
        if (tilemap)
        {
            tilemap.ClearAllTiles();
        }

        // この Filler が生成した資源（子オブジェクトの ResourceMarker）を削除
        var markers = GetComponentsInChildren<ResourceMarker>(true);
        foreach (var m in markers)
        {
            if (!m) continue;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(m.gameObject);
            else
                Destroy(m.gameObject);
#else
            Destroy(m.gameObject);
#endif
        }
    }

    [ContextMenu("Clear Tiles Only")]
    public void ClearTiles()
    {
        if (!tilemap) return;
        tilemap.ClearAllTiles();
    }

    [ContextMenu("Clear Resources Only")]
    public void ClearResourcesOnly()
    {
        var markers = GetComponentsInChildren<ResourceMarker>(true);
        foreach (var m in markers)
        {
            if (!m) continue;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(m.gameObject);
            else
                Destroy(m.gameObject);
#else
            Destroy(m.gameObject);
#endif
        }
    }
}
