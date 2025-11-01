﻿using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using Unity.Cinemachine;

/// <summary>
/// 六角タイル内に ZoomGrid(0.25×0.25) を敷き詰めるオーバーレイ。
/// ・六角の実寸を「幅×高さ」で指定（縦横比の補正が可能）
/// ・ズームイン時だけ表示
/// ・プレイヤーがいるタイルとその周囲6枚（計7枚）だけ生成/維持
/// </summary>
public class HexPerTileFineGrid : MonoBehaviour
{
    public enum HexOrientation { PointyTop, FlatTop }

    // -------- References --------
    [Header("References")]
    public Tilemap hexTilemap;
    public GameObject zoomGridPrefab;
    [Tooltip("現在位置の基準（通常は Player）。未設定なら MainCamera の位置")]
    public Transform focusTarget;

    // -------- Hex Geometry (W×H) --------
    [Header("Hex Geometry (Aspect)")]
    public HexOrientation orientation = HexOrientation.PointyTop;

    [Tooltip("六角の幅（ワールド単位）。例：10")]
    public float hexWidth = 10f;

    [Tooltip("六角の高さ（ワールド単位）。例：10  ※縦長/横長の補正に使えます")]
    public float hexHeight = 10f;

    [Tooltip("六角ポリゴンの回転微調整（度）。画像と合わない時に ±30°単位で調整")]
    public float rotationOffsetDeg = 0f;

    [Tooltip("六角の外周をX/Y方向に微調整します (+で外へ, -で内へ。縁の隙間を詰める用途)")]
    public float edgeMarginX = 0f;
    public float edgeMarginY = 0f;

    [Header("FlowField options")]
    public FlowField025 flowField;
    public bool rebuildAfterEachCell = false;

    // -------- Fill Settings --------
    [Header("Fill Settings")]
    public float spacing = 0.25f;     // 並べ間隔
    public float squareSize = 0.25f;  // ZoomGrid一辺（判定用）
    public bool clipInsideHex = true;

    [Range(0f, 1f)]
    [Tooltip("中心+四隅5点の内側率（0.25〜0.35推奨）")]
    public float minCoverage = 0.25f;

    // -------- Render Placement --------
    [Header("Render Placement")]
    public float zOffset = -0.05f;
    public string sortingLayerName = "";
    public int sortingOrder = 50;

    // -------- Zoom Toggle --------
    [Header("Zoom Toggle")]
    public CinemachineCamera vcam;      // 使っていれば割当
    public Camera fallbackOrthoCam;     // 非Cinemachineなら MainCamera を割当
    [Tooltip("この値未満の OrthoSize で表示（例：6）")]
    public float showThreshold = 6f;
    [Tooltip("非表示時は破棄せず SetActive 切替（高速）。OFF=都度破棄")]
    public bool cacheAndToggleActive = true;

    [Header("Buildable Filtering")]
    [Tooltip("Buildable タグのオブジェクトのみに ZoomGrid を表示する")]
    public bool onlyBuildable = true;

    [Tooltip("Buildable 判定に使う半径（タイル中心から）")]
    public float buildableCheckRadius = 0.1f;

    // すでにこのworld位置の六角に細かいグリッドを生成しているかどうか
    public bool HasFineAtWorld(Vector3 worldPos)
    {
        if (!hexTilemap) return false;
        var cell = hexTilemap.WorldToCell(worldPos);
        return _loadedParents.ContainsKey(cell);
    }

    // -------- Internal --------
    readonly Dictionary<Vector3Int, Transform> _loadedParents = new();
    readonly HashSet<Vector3Int> _wanted = new();
    bool _visible = false;
    float Half => squareSize * 0.5f;

    void Start()
    {
        if (!fallbackOrthoCam) fallbackOrthoCam = Camera.main;
        if (!vcam) vcam = FindAnyObjectByType<CinemachineCamera>();
        if (hexWidth <= 0f) hexWidth = 10f;
        if (hexHeight <= 0f) hexHeight = 10f;
    }

    void Update()
    {
        if (!hexTilemap || !zoomGridPrefab) return;

        float ortho = CurrentOrtho();
        bool shouldShow = (ortho > 0f && ortho < showThreshold);

        Vector3 focus = FocusWorld();
        Vector3Int centerCell = hexTilemap.WorldToCell(focus);

        BuildWantedSet(centerCell); // 中心+6近傍

        if (shouldShow)
        {
            foreach (var c in _wanted)
                if (!_loadedParents.ContainsKey(c))
                    CreateForCell(c);

            foreach (var kv in _loadedParents)
                if (!_wanted.Contains(kv.Key))
                    SetActiveOrDestroy(kv.Key, false);

            if (!_visible)
            {
                foreach (var kv in _loadedParents) kv.Value.gameObject.SetActive(true);
                _visible = true;
            }
        }
        else
        {
            if (_visible)
            {
                if (cacheAndToggleActive)
                {
                    foreach (var kv in _loadedParents) kv.Value.gameObject.SetActive(false);
                }
                else
                {
                    ClearAll();
                }
                _visible = false;
            }
        }
    }

    // ===== Create / Destroy =====
    void CreateForCell(Vector3Int cell, bool force = false)
    {
        // すでに作ってあるなら何もしない
        if (_loadedParents.ContainsKey(cell)) return;
        if (!hexTilemap) return;

        // 六角タイルの中心ワールド座標をとる
        Vector3 center = hexTilemap.GetCellCenterWorld(cell);

        // Buildable フィルタがONなら、ここでスキップ
        if (onlyBuildable && !force)
        {
            bool hasBuildable = false;
            var hits = Physics2D.OverlapCircleAll((Vector2)center, buildableCheckRadius);
            foreach (var h in hits)
            {
                if (h.CompareTag("Buildable")) { hasBuildable = true; break; }
            }
            if (!hasBuildable)
            {
                return;
            }
        }

        // ここから下は今まで通り
        GameObject parentGO = new GameObject($"FineGrid_{cell.x}_{cell.y}");
        parentGO.transform.SetParent(transform, worldPositionStays: false);
        parentGO.transform.position = Vector3.zero;
        _loadedParents[cell] = parentGO.transform;

        float width = hexWidth + edgeMarginX * 2f;
        float height = hexHeight + edgeMarginY * 2f;

        float minX = center.x - width * 0.5f;
        float maxX = center.x + width * 0.5f;
        float minY = center.y - height * 0.5f;
        float maxY = center.y + height * 0.5f;

        float step = (spacing > 0f) ? spacing : 0.25f;

        List<Vector2> hexPoly = BuildHexXY(
            center,
            (hexWidth * 0.5f),
            (hexHeight * 0.5f)
        );

        for (float y = minY; y <= maxY + 0.0001f; y += step)
        {
            for (float x = minX; x <= maxX + 0.0001f; x += step)
            {
                Vector2 p = new Vector2(x + step * 0.5f, y + step * 0.5f);

                if (clipInsideHex && !InPoly(p, hexPoly)) continue;

                if (zoomGridPrefab != null)
                {
                    var go = Instantiate(
                        zoomGridPrefab,
                        new Vector3(p.x, p.y, center.z + zOffset),
                        Quaternion.identity,
                        parentGO.transform
                    );

                    if (!string.IsNullOrEmpty(sortingLayerName))
                    {
                        var sr = go.GetComponent<SpriteRenderer>();
                        if (sr) { sr.sortingLayerName = sortingLayerName; sr.sortingOrder = sortingOrder; }
                    }
                }

                if (flowField != null)
                {
                    flowField.MarkWalkable(p.x, p.y);
                }
            }
        }

        if (flowField != null && rebuildAfterEachCell)
        {
            flowField.Rebuild();
        }
    }

    // ★追加：外から「ここだけ強制で敷いて！」と呼ぶためのAPI
    public void ForceCreateAtWorld(Vector3 worldPos)
    {
        if (!hexTilemap) return;
        var cell = hexTilemap.WorldToCell(worldPos);
        CreateForCell(cell, true);
    }

    void SetActiveOrDestroy(Vector3Int cell, bool active)
    {
        if (!_loadedParents.TryGetValue(cell, out var t) || !t) return;

        if (cacheAndToggleActive)
        {
            t.gameObject.SetActive(active);
        }
        else
        {
            if (Application.isPlaying) Destroy(t.gameObject);
            else DestroyImmediate(t.gameObject);
            _loadedParents.Remove(cell);
        }
    }

    void ClearAll()
    {
        foreach (var kv in _loadedParents)
        {
            var t = kv.Value;
            if (t)
            {
                if (Application.isPlaying) Destroy(t.gameObject);
                else DestroyImmediate(t.gameObject);
            }
        }
        _loadedParents.Clear();
    }

    // ===== Wanted set : center + 6 neighbors =====
    void BuildWantedSet(Vector3Int center)
    {
        _wanted.Clear();
        if (hexTilemap.HasTile(center)) _wanted.Add(center);

        foreach (var off in NeighborOffsets(center))
        {
            var c = center + off;
            if (hexTilemap.HasTile(c)) _wanted.Add(c);
        }
    }

    IEnumerable<Vector3Int> NeighborOffsets(Vector3Int cell)
    {
        if (orientation == HexOrientation.PointyTop)
        {
            bool odd = (cell.y & 1) != 0; // odd-r
            if (odd)
            {
                yield return new Vector3Int(+1, 0, 0);
                yield return new Vector3Int(-1, 0, 0);
                yield return new Vector3Int(+1, +1, 0);
                yield return new Vector3Int(0, +1, 0);
                yield return new Vector3Int(+1, -1, 0);
                yield return new Vector3Int(0, -1, 0);
            }
            else
            {
                yield return new Vector3Int(+1, 0, 0);
                yield return new Vector3Int(-1, 0, 0);
                yield return new Vector3Int(0, +1, 0);
                yield return new Vector3Int(-1, +1, 0);
                yield return new Vector3Int(0, -1, 0);
                yield return new Vector3Int(-1, -1, 0);
            }
        }
        else // FlatTop (odd-q)
        {
            bool odd = (cell.x & 1) != 0;
            if (odd)
            {
                yield return new Vector3Int(+1, 0, 0);
                yield return new Vector3Int(-1, 0, 0);
                yield return new Vector3Int(0, +1, 0);
                yield return new Vector3Int(0, -1, 0);
                yield return new Vector3Int(+1, +1, 0);
                yield return new Vector3Int(-1, +1, 0);
            }
            else
            {
                yield return new Vector3Int(+1, 0, 0);
                yield return new Vector3Int(-1, 0, 0);
                yield return new Vector3Int(0, +1, 0);
                yield return new Vector3Int(0, -1, 0);
                yield return new Vector3Int(+1, -1, 0);
                yield return new Vector3Int(-1, -1, 0);
            }
        }
    }

    // ===== Utilities =====
    float CurrentOrtho()
    {
        if (vcam != null) return vcam.Lens.OrthographicSize;
        if (fallbackOrthoCam && fallbackOrthoCam.orthographic) return fallbackOrthoCam.orthographicSize;
        return -1f;
    }

    Vector3 FocusWorld()
    {
        if (focusTarget) return focusTarget.position;
        var cam = fallbackOrthoCam ? fallbackOrthoCam : Camera.main;
        return cam ? (Vector3)cam.transform.position : Vector3.zero;
    }

    // x方向半径=rx, y方向半径=ry の「楕円半径」ベースで六角頂点を生成（縦横比を直接反映）
    List<Vector2> BuildHexXY(Vector3 c, float rx, float ry)
    {
        var verts = new List<Vector2>(6);

        // 基本角：PointyTop=90°, FlatTop=0°
        float baseDeg = (orientation == HexOrientation.PointyTop) ? 90f : 0f;
        baseDeg += rotationOffsetDeg;

        for (int i = 0; i < 6; i++)
        {
            float ang = Mathf.Deg2Rad * (baseDeg + i * 60f);
            // cos/sin に別半径を掛けることで縦横比を忠実に再現
            float vx = c.x + rx * Mathf.Cos(ang);
            float vy = c.y + ry * Mathf.Sin(ang);
            verts.Add(new Vector2(vx, vy));
        }
        return verts;
    }

    // 多角形内判定（射線法）
    bool InPoly(Vector2 p, List<Vector2> poly)
    {
        bool inside = false; int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            bool inter = ((a.y > p.y) != (b.y > p.y)) &&
                         (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y + 1e-9f) + a.x);
            if (inter) inside = !inside;
        }
        return inside;
    }
}