using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Tilemaps;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Unity.Cinemachine;   // ★ 追加：ズーム状態を見る

/// <summary>
/// 建築物を置く共通クラス。
/// FlowField連携と偶数サイズ補正を統合。
/// さらにズームアウト時は「選択なしでも六角タイルにポインタを出す」ようにしてある。
/// </summary>
public class BuildPlacement : MonoBehaviour
{
    [Header("=== Common targets ===")]
    public Grid grid;
    public Transform prefabParent;

    [Header("=== Big-cell placement (hex / normal) ===")]
    public Tilemap groundTilemap;
    public bool requireUnderlyingTile = true;

    [Header("=== Fine-cell placement (0.25 × 0.25) ===")]
    public bool useFineGrid = false;
    public float fineCellSize = 0.25f;
    public Tilemap hexTilemap;
    public bool requireBuildableForFine = true;
    public float fineBuildableRadius = 0.1f;

    [Tooltip("FlowField があるなら、置いた/消したときにブロック状態を伝える")]
    public FlowField025 flowField;

    [Header("=== Preview (ghost) ===")]
    public Transform prefabPreview;
    [Range(0f, 1f)] public float previewAlpha = 0.45f;
    public Vector3 hoverOffset = Vector3.zero;

    [Header("=== Pointer (OK/NG) ===")]
    public Transform pointerOK;   // ← SelectTile 相当
    public Transform pointerNG;   // ← SelectFalse 相当

    [Header("=== Fallback delete (optional) ===")]
    public LayerMask placeableLayers = ~0;
    public float detectRadius = 0.35f;

    [Header("=== Zoom auto switch ===")]
    [Tooltip("参照する CinemachineCamera（なければ MainCamera の Ortho を見る）")]
    public CinemachineCamera vcam;
    [Tooltip("このサイズ以下をズームイン扱い（細かいグリッド）にする")]
    public float fineGridThreshold = 3f;

    // 連続設置中かどうか
    bool _isDragging = false;

    // ドラッグでたまっているセル（確定前の仮おきデータ）
    readonly List<(Vector2Int cell, Vector3 center)> _previewCells = new();

    // ドラッグで表示するゴーストたち
    readonly List<GameObject> _dragGhosts = new();

    BuildingDef _current;
    GameObject _spawnedPreviewGO;
    Vector3Int _lastBigCell = new(int.MinValue, int.MinValue, 0);
    Vector2Int _lastFineCell = new(int.MinValue, int.MinValue);
    readonly Dictionary<Vector3Int, GameObject> _placedByCell = new();
    readonly Dictionary<Vector2Int, GameObject> _placedFine = new();
    readonly HashSet<Vector3Int> _protectedCells = new();

    void Awake()
    {
        if (!grid) grid = GetComponentInParent<Grid>();
        if (!vcam) vcam = FindFirstObjectByType<CinemachineCamera>();
        RebuildMapFromParent();
    }

    void OnDisable()
    {
        ClearPreview();
        UpdatePointerActive(false, false);
    }

    // ========================================================================
    // 外から建物を選んだとき
    // ========================================================================
    public void SetSelected(BuildingDef def)
    {
        _current = def;
        ClearPreview();
    }

    void Update()
    {
        // マウスがUIの上にあるなら何もしない
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            // UIの上にあるときはポインタも消す
            UpdatePointerActive(false, false);
            return;
        }

        // ★ ここでズームを見て useFineGrid を自動設定
        AutoUpdateUseFineGridByZoom();

#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null || Camera.main == null) return;

        Vector2 sp = mouse.position.ReadValue();
        Vector3 world = Camera.main.ScreenToWorldPoint(new Vector3(sp.x, sp.y, -Camera.main.transform.position.z));
        world.z = 0f;

        if (useFineGrid)
        {
            UpdatePointerActive(false, false);

            if (_current == null)
            {
                ClearPreview();
                ClearDragGhosts();
                _previewCells.Clear();
                _isDragging = false;
                return;
            }

            Vector2Int fineCell = WorldToFineCell(world, fineCellSize);
            // 基本のセル中心
            Vector3 baseCenter = FineCellToWorld(fineCell, fineCellSize) + hoverOffset;

            // ★ even補正を追加
            Vector3 evenOff = GetEvenSizeOffsetForFine(_current);
            Vector3 finalCenter = baseCenter + evenOff;

            // 今のセルに置けるか
            bool canHere = CanPlaceAtFine(fineCell, finalCenter);

            // ── 常にメインプレビューを出す ──
            EnsurePrefabPreview();
            if (_spawnedPreviewGO)
            {
                _spawnedPreviewGO.transform.position = finalCenter;
                var col = canHere ? new Color(1f, 1f, 1f, previewAlpha)
                                  : new Color(1f, 0.4f, 0.4f, previewAlpha);
                SetSpriteColor(_spawnedPreviewGO.transform, col);
            }

            // ── 左押しでドラッグ開始 ──
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _isDragging = true;
                _previewCells.Clear();
                ClearDragGhosts();
            }

            // ── ドラッグ中は通ったセルをゴースト化 ──
            if (_isDragging && mouse.leftButton.isPressed)
            {
                // すでに「footprint がかぶる」ゴーストがあるなら作らない
                if (!FootprintOverlapsAny(fineCell, _current, _previewCells))
                {
                    CreateDragGhost(finalCenter, CanPlaceAtFine(fineCell, finalCenter));
                    _previewCells.Add((fineCell, finalCenter));
                }
            }


            // ── 左を離したら一括設置 ──
            if (_isDragging && mouse.leftButton.wasReleasedThisFrame)
            {
                foreach (var p in _previewCells)
                {
                    if (CanPlaceAtFine(p.cell, p.center))
                        PlaceAtFine(p.cell, p.center);   // ★ ここでも center は偶数補正後
                }
                _previewCells.Clear();
                ClearDragGhosts();
                _isDragging = false;
            }

            // ── 右クリック ──
            if (mouse.rightButton.wasPressedThisFrame)
            {
                if (_isDragging || _previewCells.Count > 0)
                {
                    // ① ドラッグ中ならプレビューだけキャンセル
                    _previewCells.Clear();
                    ClearDragGhosts();
                    _isDragging = false;
                }
                else
                {
                    // ② ドラッグしてないときは実ブロックを消す（同じ補正位置で）
                    DeleteAtFine(fineCell, finalCenter);
                }
            }

            return;
        }
        else
        {
            // ズームアウトモード：建築が選ばれていなくても
            // 「マウスがいる六角タイルにポインタを出す」
            var cell = grid.WorldToCell(world);

            if (_current == null)
            {
                // 建築なし用の表示ロジック
                ShowPointerForHexHover(cell);
            }
            else
            {
                // 建築ありの従来ロジック
                UpdatePreviewAndPointerBig(cell);

                if (mouse.leftButton.wasPressedThisFrame)
                    PlaceAtBig(cell);
                if (mouse.rightButton.wasPressedThisFrame)
                    DeleteAtBig(cell);
            }
        }
#endif
    }

    // ========================================================================
    // ズームから useFineGrid を自動判定
    // ========================================================================
    void AutoUpdateUseFineGridByZoom()
    {
        float ortho = -1f;
        if (vcam != null) ortho = vcam.Lens.OrthographicSize;
        else if (Camera.main && Camera.main.orthographic) ortho = Camera.main.orthographicSize;

        if (ortho < 0f) return; // 判定できないときは何もしない

        bool wantFine = ortho <= fineGridThreshold;
        if (wantFine != useFineGrid)
        {
            useFineGrid = wantFine;
            // モードが変わったらプレビューとポインタを消す
            ClearPreview();
            UpdatePointerActive(false, false);
        }
    }

    // ========================================================================
    // 「建築が選ばれていないズームアウト時」のポインタ表示
    // ========================================================================
    void ShowPointerForHexHover(Vector3Int cell)
    {
        if (groundTilemap == null)
        {
            // Ground がないなら「常にOK表示」でいい
            Vector3 center = grid.GetCellCenterWorld(cell) + hoverOffset;
            UpdatePointerActive(true, false, center);
            return;
        }

        bool hasTile = groundTilemap.HasTile(cell);

        Vector3 pos = grid.GetCellCenterWorld(cell) + hoverOffset;
        if (hasTile)
            UpdatePointerActive(true, false, pos);   // ← SelectTile
        else
            UpdatePointerActive(false, true, pos);   // ← SelectFalse
    }

    // ========================================================================
    // でかいセル配置（＝六角タイル専用の簡略版）
    // ========================================================================
    void PlaceAtBig(Vector3Int cell)
    {
        // 選択されていない / プレハブなし
        if (_current?.prefab == null) return;

        // このセルにすでに何か置いていたら置けない
        if (!CanPlaceAtBig(cell)) return;

        // タイルの中心にそのまま置く（オフセットだけ反映）
        Vector3 pos = grid.GetCellCenterWorld(cell) + hoverOffset;

        var go = Instantiate(_current.prefab, pos, Quaternion.identity, prefabParent);
        _placedByCell[cell] = go;

        // 六角タイルが「Baseを出すタイプ」ならここで処理
        if (_current.isHexTile)
        {
            var ui = Object.FindFirstObjectByType<StartMenuUI>();
            if (ui != null)
            {
                bool spawned = ui.TrySpawnBaseAt(pos);
                if (spawned)
                {
                    // ここは削除させない
                    _protectedCells.Add(cell);
                }
            }
        }
    }

    // 六角タイルを消すだけ（FlowFieldや複数セルは見ない）
    void DeleteAtBig(Vector3Int cell)
    {
        // Baseを置いたセルは守る
        if (_protectedCells.Contains(cell))
            return;

        if (_placedByCell.TryGetValue(cell, out var go) && go)
        {
            _placedByCell.Remove(cell);
            Destroy(go);
            return;
        }

        // 念のための物理フォールバック（六角タイルがプレハブの場合用）
        Vector3 c = grid.GetCellCenterWorld(cell) + hoverOffset;
        var hits = Physics2D.OverlapCircleAll(c, detectRadius, placeableLayers);
        foreach (var h in hits)
        {
            // セルにスナップする
            var hc = grid.WorldToCell(h.transform.position - hoverOffset);
            if (_protectedCells.Contains(hc)) continue;

            _placedByCell.Remove(hc);
            Destroy(h.gameObject);
            return;
        }
    }

    // 「この六角セルにすでに何か置いてるか」だけを見る超シンプル版
    bool CanPlaceAtBig(Vector3Int cell)
    {
        // 地面タイルが必要ならここでチェック
        if (requireUnderlyingTile && groundTilemap != null && !groundTilemap.HasTile(cell))
            return false;

        // すでにこのセルに何か置いていたらNG
        if (_placedByCell.ContainsKey(cell))
            return false;

        return true;
    }

    // プレビューとOK/NGポインタも六角1セル前提の軽量版
    void UpdatePreviewAndPointerBig(Vector3Int cell)
    {
        if (cell != _lastBigCell)
        {
            _lastBigCell = cell;
            MovePreview(grid.GetCellCenterWorld(cell) + hoverOffset);
        }

        bool can = CanPlaceAtBig(cell);
        Vector3 center = grid.GetCellCenterWorld(cell) + hoverOffset;

        if (can) UpdatePointerActive(true, false, center);
        else UpdatePointerActive(false, true, center);

        if (_spawnedPreviewGO)
        {
            var col = can ? new Color(1f, 1f, 1f, previewAlpha)
                          : new Color(1f, 0.4f, 0.4f, previewAlpha);
            SetSpriteColor(_spawnedPreviewGO.transform, col);
        }
    }

    // ========================================================================
    // 細かいセル配置（同様にFlowField反映）
    // ========================================================================
    void PlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_current?.prefab == null) return;

        // worldCenter はすでに偶数補正済みなのでそのまま使う
        Vector3 finalCenter = worldCenter;

        if (!CanPlaceAtFine(fcell, finalCenter)) return;

        var go = Instantiate(_current.prefab, finalCenter, Quaternion.identity, prefabParent);

        int w = Mathf.Max(1, _current.cellsWidth);
        int height = Mathf.Max(1, _current.cellsHeight);

        for (int iy = 0; iy < height; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                if (_current.shape != null &&
                    ix < _current.shape.GetLength(0) &&
                    iy < _current.shape.GetLength(1) &&
                    !_current.shape[ix, iy])
                    continue;

                int cx = fcell.x + ix - Mathf.FloorToInt(w / 2f);
                int cy = fcell.y + iy - Mathf.FloorToInt(height / 2f);

                var key = new Vector2Int(cx, cy);
                _placedFine[key] = go;
            }
        }

        if (flowField != null)
            RegisterBuildingToFlowField(_current, finalCenter, true);
    }

    void DeleteAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        Vector3 finalCenter = worldCenter; // worldCenterは補正済み

        if (!_placedFine.TryGetValue(fcell, out var go) || go == null)
            return;

        var keysToRemove = new List<Vector2Int>();
        foreach (var kv in _placedFine)
            if (kv.Value == go)
                keysToRemove.Add(kv.Key);
        foreach (var k in keysToRemove)
            _placedFine.Remove(k);

        Destroy(go);

        if (flowField != null)
            RegisterBuildingToFlowField(_current, finalCenter, false);
    }

    bool CanPlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_current == null) return false;

        if (hexTilemap != null)
        {
            var hcell = hexTilemap.WorldToCell(worldCenter);
            if (!hexTilemap.HasTile(hcell))
                return false;
        }

        if (requireBuildableForFine)
        {
            var hits = Physics2D.OverlapCircleAll((Vector2)worldCenter, fineBuildableRadius);
            bool found = false;
            foreach (var h in hits)
            {
                if (h.CompareTag("Buildable"))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }

        int w = Mathf.Max(1, _current.cellsWidth);
        int height = Mathf.Max(1, _current.cellsHeight);

        for (int iy = 0; iy < height; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                if (_current.shape != null &&
                    ix < _current.shape.GetLength(0) &&
                    iy < _current.shape.GetLength(1) &&
                    !_current.shape[ix, iy])
                    continue;

                int cx = fcell.x + ix - Mathf.FloorToInt(w / 2f);
                int cy = fcell.y + iy - Mathf.FloorToInt(height / 2f);
                var test = new Vector2Int(cx, cy);

                if (_placedFine.ContainsKey(test))
                    return false;
            }
        }

        return true;
    }

    void UpdatePreviewAndPointerFine(Vector2Int fcell, Vector3 worldCenter)
    {
        // ← 追加：evenサイズならここでずらす
        Vector3 evenOff = GetEvenSizeOffsetForFine(_current);
        Vector3 finalPos = worldCenter + evenOff;

        if (fcell != _lastFineCell)
        {
            _lastFineCell = fcell;
            MovePreview(finalPos);
        }

        bool can = CanPlaceAtFine(fcell, worldCenter);

        if (_spawnedPreviewGO)
        {
            var col = can ? new Color(1f, 1f, 1f, previewAlpha)
                          : new Color(1f, 0.4f, 0.4f, previewAlpha);
            SetSpriteColor(_spawnedPreviewGO.transform, col);
        }
    }

    // ========================================================================
    // FlowField 反映ロジック
    // ========================================================================
    void RegisterBuildingToFlowField(BuildingDef def, Vector3 pos, bool blocked)
    {
        if (def == null || flowField == null) return;

        float cs = flowField.cellSize;
        int w = Mathf.Max(1, def.cellsWidth);
        int h = Mathf.Max(1, def.cellsHeight);

        for (int iy = 0; iy < h; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                if (def.shape != null &&
                    (ix < def.shape.GetLength(0)) &&
                    (iy < def.shape.GetLength(1)) &&
                    !def.shape[ix, iy])
                    continue;

                float wx = pos.x + (ix - (w - 1) / 2f) * cs;
                float wy = pos.y + (iy - (h - 1) / 2f) * cs;

                if (blocked) flowField.MarkBlocked(wx, wy);
                else flowField.MarkWalkable(wx, wy);
            }
        }

        if (def.rebuildAfterPlace)
            flowField.Rebuild();
    }

    // ========================================================================
    // プレビュー、復元など既存処理
    // ========================================================================
    void MovePreview(Vector3 world)
    {
        if (_current?.prefab == null) return;
        EnsurePrefabPreview();
        if (_spawnedPreviewGO)
            _spawnedPreviewGO.transform.position = world;
    }

    void EnsurePrefabPreview()
    {
        if (_spawnedPreviewGO != null) return;
        if (_current?.prefab == null) return;

        _spawnedPreviewGO = Instantiate(_current.prefab, Vector3.zero, Quaternion.identity);
        if (prefabPreview) _spawnedPreviewGO.transform.SetParent(prefabPreview, true);

        foreach (var c in _spawnedPreviewGO.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
        foreach (var c in _spawnedPreviewGO.GetComponentsInChildren<Collider>(true)) c.enabled = false;

        SetSpriteColor(_spawnedPreviewGO.transform, new Color(1f, 1f, 1f, previewAlpha));
    }

    void ClearPreview()
    {
        _lastBigCell = new Vector3Int(int.MinValue, int.MinValue, 0);
        _lastFineCell = new Vector2Int(int.MinValue, int.MinValue);

        if (_spawnedPreviewGO != null)
        {
#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(_spawnedPreviewGO);
        else Destroy(_spawnedPreviewGO);
#else
            Destroy(_spawnedPreviewGO);
#endif
            _spawnedPreviewGO = null;
        }

        // ★ 追加：ドラッグゴーストも消す
        ClearDragGhosts();
    }

    void CreateDragGhost(Vector3 pos, bool canPlace)
    {
        if (_current?.prefab == null) return;

        var go = Instantiate(_current.prefab, pos, Quaternion.identity);
        if (prefabPreview) go.transform.SetParent(prefabPreview, true);

        foreach (var c in go.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;

        var col = canPlace ? new Color(1f, 1f, 1f, previewAlpha)
                           : new Color(1f, 0.4f, 0.4f, previewAlpha);
        SetSpriteColor(go.transform, col);

        _dragGhosts.Add(go);
    }

    // candidateCell に current を置いたときの footprint が
    // すでに登録済みのどれかとかぶるなら true
    bool FootprintOverlapsAny(Vector2Int candidateCell,
                              BuildingDef def,
                              List<(Vector2Int cell, Vector3 center)> existing)
    {
        foreach (var e in existing)
        {
            if (FootprintOverlaps(candidateCell, def, e.cell, def))
                return true;
        }
        return false;
    }

    // 2つのセルを「同じ BuildingDef の幅×高さで置いた」としたときに
    // 占有セルが1マスでもかぶるかどうか
    bool FootprintOverlaps(Vector2Int aCell, BuildingDef aDef,
                           Vector2Int bCell, BuildingDef bDef)
    {
        int aw = Mathf.Max(1, aDef.cellsWidth);
        int ah = Mathf.Max(1, aDef.cellsHeight);
        int bw = Mathf.Max(1, bDef.cellsWidth);
        int bh = Mathf.Max(1, bDef.cellsHeight);

        // 中心セルから左右上下に広がるので「中心をずらした矩形」としてみる
        // 中心 = aCell, 幅 = aw, 高さ = ah
        // 0.25 グリッドなので、単純にセル番号で矩形を作っていい
        int aMinX = aCell.x - aw / 2;
        int aMaxX = aMinX + aw - 1;
        int aMinY = aCell.y - ah / 2;
        int aMaxY = aMinY + ah - 1;

        int bMinX = bCell.x - bw / 2;
        int bMaxX = bMinX + bw - 1;
        int bMinY = bCell.y - bh / 2;
        int bMaxY = bMinY + bh - 1;

        bool xOverlap = !(aMaxX < bMinX || bMaxX < aMinX);
        bool yOverlap = !(aMaxY < bMinY || bMaxY < aMinY);

        return xOverlap && yOverlap;
    }

    void ClearDragGhosts()
    {
        foreach (var g in _dragGhosts)
        {
            if (g == null) continue;
#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(g);
        else Destroy(g);
#else
            Destroy(g);
#endif
        }
        _dragGhosts.Clear();
    }

    void UpdatePointerActive(bool showOK, bool showNG, Vector3? moveTo = null)
    {
        // useFineGrid のときは必ず非表示（ズームイン時は見せない）
        if (useFineGrid)
        {
            showOK = false;
            showNG = false;
        }

        if (pointerOK)
        {
            if (moveTo.HasValue) pointerOK.position = moveTo.Value;
            if (pointerOK.gameObject.activeSelf != showOK)
                pointerOK.gameObject.SetActive(showOK);
        }
        if (pointerNG)
        {
            if (moveTo.HasValue) pointerNG.position = moveTo.Value;
            if (pointerNG.gameObject.activeSelf != showNG)
                pointerNG.gameObject.SetActive(showNG);
        }
    }

    void RebuildMapFromParent()
    {
        _placedByCell.Clear();
        _placedFine.Clear();
        if (!prefabParent) return;

        foreach (Transform child in prefabParent)
        {
            if (grid != null)
            {
                var cell = grid.WorldToCell(child.position - hoverOffset);
                if (!_placedByCell.ContainsKey(cell))
                {
                    _placedByCell.Add(cell, child.gameObject);
                    continue;
                }
            }

            var fc = WorldToFineCell(child.position, fineCellSize);
            if (!_placedFine.ContainsKey(fc))
                _placedFine.Add(fc, child.gameObject);
        }
    }

    static void SetSpriteColor(Transform root, Color col)
    {
        foreach (var r in root.GetComponentsInChildren<SpriteRenderer>(true))
            r.color = col;
        foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = mr.materials;
            for (int i = 0; i < mats.Length; i++)
                mats[i].color = col;
        }
    }

    static Vector2Int WorldToFineCell(Vector3 world, float cellSize)
    {
        int gx = Mathf.FloorToInt(world.x / cellSize);
        int gy = Mathf.FloorToInt(world.y / cellSize);
        return new Vector2Int(gx, gy);
    }

    // 偶数サイズの建物は左下方向に0.5マスずらす（グリッド揃え）
    Vector3 GetEvenSizeOffsetForFine(BuildingDef def)
    {
        if (def == null) return Vector3.zero;

        float cs = fineCellSize; // 0.25f など
        float offX = (def.cellsWidth % 2 == 0) ? -cs * 0.5f : 0f;
        float offY = (def.cellsHeight % 2 == 0) ? -cs * 0.5f : 0f;

        return new Vector3(offX, offY, 0f);
    }
    static Vector3 FineCellToWorld(Vector2Int cell, float cellSize)
    {
        float x = cell.x * cellSize + cellSize * 0.5f;
        float y = cell.y * cellSize + cellSize * 0.5f;
        return new Vector3(x, y, 0f);
    }
}
