using System.Collections.Generic;
using UnityEngine;

public class ResourceMarker : MonoBehaviour
{
    // ---- 外部から Resource ブロック一覧の親にアクセスするための getter ----
    public Transform BlocksRoot => blocksRoot;

    [Header("Save 用")]
    [Tooltip("この資源を表す BuildingDef（セーブ / ロードで使用）")]
    public BuildingDef def;

    // ---- Hex 揃え設定（数値を HexPerTileFineGrid と同じにする）----
    public enum HexOrientation { PointyTop, FlatTop }

    [Header("Hex Geometry (Resource)")]
    [Tooltip("HexPerTileFineGrid と同じ向きにしてください")]
    public HexOrientation orientation = HexOrientation.PointyTop;

    [Tooltip("六角の幅（ワールド単位）。HexPerTileFineGrid.hexWidth と同じ値にする")]
    public float hexWidth = 10f;

    [Tooltip("六角の高さ（ワールド単位）。HexPerTileFineGrid.hexHeight と同じ値にする")]
    public float hexHeight = 10f;

    [Tooltip("六角ポリゴンの回転微調整（度）。HexPerTileFineGrid.rotationOffsetDeg と合わせる")]
    public float rotationOffsetDeg = 0f;

    [Tooltip("六角の外周をX/Y方向に微調整 (+で外へ, -で内へ)")]
    public float edgeMarginX = 0f;
    public float edgeMarginY = 0f;

    [Tooltip("グリッドの間隔。HexPerTileFineGrid.spacing と同じ値にする")]
    public float cellSize = 0.25f;

    [Tooltip("true なら六角形の外側はクリップして配置しない")]
    public bool clipInsideHex = true;

    // ---- Resource ブロック ----
    [Header("Resource ブロック")]
    [Tooltip("六角の中に並べる小さい Resource ブロックのプレハブ")]
    public GameObject blockPrefab;

    [Range(0f, 1f)]
    [Tooltip("各セルにブロックを置く確率 (0=置かない, 1=全部置く)")]
    public float fillChance = 0.6f;

    [Tooltip("ブロックを少し前後させたい場合の Z オフセット")]
    public float zOffset = 0f;

    // ---- 生成タイミング ----
    [Header("生成タイミング")]
    [Tooltip("再生開始時に自動で GenerateBlocks() を呼ぶか")]
    public bool generateOnStart = true;

    [Tooltip("GenerateBlocks 実行前に既存ブロックを削除するか")]
    public bool clearBeforeGenerate = true;

    Transform blocksRoot;

    void Awake()
    {
        EnsureBlocksRoot();
    }

    void Start()
    {
        if (Application.isPlaying && generateOnStart)
        {
            GenerateBlocks();
        }
    }

    void EnsureBlocksRoot()
    {
        if (blocksRoot != null) return;

        var existing = transform.Find("Blocks");
        if (existing != null)
        {
            blocksRoot = existing;
            return;
        }

        var go = new GameObject("Blocks");
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        blocksRoot = go.transform;
    }

    [ContextMenu("Generate Blocks (Hex)")]
    public void GenerateBlocks()
    {
        if (blockPrefab == null)
        {
            Debug.LogWarning("[ResourceMarker] blockPrefab が設定されていません。", this);
            return;
        }

        EnsureBlocksRoot();

        if (clearBeforeGenerate)
        {
            ClearBlocks();
        }

        Vector3 center = transform.position;

        float width = hexWidth + edgeMarginX * 2f;
        float height = hexHeight + edgeMarginY * 2f;

        float minX = center.x - width * 0.5f;
        float maxX = center.x + width * 0.5f;
        float minY = center.y - height * 0.5f;
        float maxY = center.y + height * 0.5f;

        float step = (cellSize > 0f) ? cellSize : 0.25f;

        // 六角ポリゴンを作成（HexPerTileFineGrid と同じロジック）
        List<Vector2> hexPoly = BuildHexXY(center, hexWidth * 0.5f, hexHeight * 0.5f);

        for (float y = minY; y <= maxY + 0.0001f; y += step)
        {
            for (float x = minX; x <= maxX + 0.0001f; x += step)
            {
                // セル中心位置（ワールド）
                Vector2 p = new Vector2(x + step * 0.5f, y + step * 0.5f);

                // 六角形の内側だけに配置
                if (clipInsideHex && !InPoly(p, hexPoly)) continue;

                // 確率でスキップ
                if (fillChance < 1f && Random.value > fillChance) continue;

                Vector3 worldPos = new Vector3(p.x, p.y, center.z + zOffset);

                // ★ ResourceBlock タグ付きで生成
                var go = Instantiate(blockPrefab, worldPos, Quaternion.identity, blocksRoot);
                go.name = blockPrefab.name;
                go.tag = "ResourceBlock";
            }
        }
    }

    [ContextMenu("Clear Blocks")]
    public void ClearBlocks()
    {
        EnsureBlocksRoot();

        for (int i = blocksRoot.childCount - 1; i >= 0; i--)
        {
            var c = blocksRoot.GetChild(i);
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(c.gameObject);
            else
                Destroy(c.gameObject);
#else
            Destroy(c.gameObject);
#endif
        }
    }

    // --- 六角ポリゴン生成 ---
    List<Vector2> BuildHexXY(Vector3 c, float rx, float ry)
    {
        var verts = new List<Vector2>(6);

        // 基本角：PointyTop=90°, FlatTop=0°
        float baseDeg = (orientation == HexOrientation.PointyTop) ? 90f : 0f;
        float degOffset = rotationOffsetDeg;

        for (int i = 0; i < 6; i++)
        {
            float ang = Mathf.Deg2Rad * (baseDeg + degOffset + i * 60f);
            float vx = c.x + rx * Mathf.Cos(ang);
            float vy = c.y + ry * Mathf.Sin(ang);
            verts.Add(new Vector2(vx, vy));
        }
        return verts;
    }

    // --- 多角形内判定 ---
    bool InPoly(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            bool inter = ((a.y > p.y) != (b.y > p.y)) &&
                         (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y + 1e-9f) + a.x);
            if (inter) inside = !inside;
        }
        return inside;
    }
    // --- Resouceブロックの配置復元 ---
    public void RebuildBlocksFromPositions(List<Vector3> positions)
    {
        if (positions == null) return;

        EnsureBlocksRoot();
        ClearBlocks();

        if (blockPrefab == null)
        {
            Debug.LogWarning("[ResourceMarker] blockPrefab が設定されていません。", this);
            return;
        }

        foreach (var wp in positions)
        {
            var go = Instantiate(blockPrefab, wp, Quaternion.identity, blocksRoot);
            go.name = blockPrefab.name;
            go.tag = "ResourceBlock";   // Drill判定用タグ
        }
    }

    //----------------------------------
    // --- ドローン採掘 ---
    //----------------------------------
    // ★ ドローンがこの資源に対して採掘を開始したときに呼ばれる
    public void StartDroneMining(Vector3 droneWorldPos)
    {
        if (blocksRoot == null)
        {
            Debug.LogWarning("[ResourceMarker] blocksRoot が設定されていません。");
            return;
        }

        // 1. ドローンの位置に一番近い Resource ブロック(小ブロック)を探す
        Transform nearestBlock = null;
        float bestDistSq = float.MaxValue;

        foreach (Transform child in blocksRoot)
        {
            if (child == null) continue;

            float d = (child.position - droneWorldPos).sqrMagnitude;
            if (d < bestDistSq)
            {
                bestDistSq = d;
                nearestBlock = child;
            }
        }

        if (nearestBlock == null)
        {
            Debug.LogWarning("[ResourceMarker] StartDroneMining: 対象ブロックが見つかりませんでした。");
            return;
        }

        // 2. そのブロックの子から MiningIconBlinker を探して点滅ON
        var blinker = nearestBlock.GetComponentInChildren<MiningIconBlinker>(true);
        if (blinker != null)
        {
            blinker.SetBlinking(true);
        }
        else
        {
            Debug.LogWarning("[ResourceMarker] StartDroneMining: MiningIconBlinker が見つかりませんでした。");
        }

        Debug.Log($"[ResourceMarker] Drone mining started at {nearestBlock.position}");
    }
}
