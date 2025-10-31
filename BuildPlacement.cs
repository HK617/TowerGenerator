using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Tilemaps;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 建築物を置く共通クラス。
/// FlowField連携と偶数サイズ補正を統合。
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
    public Transform pointerOK;
    public Transform pointerNG;

    [Header("=== Fallback delete (optional) ===")]
    public LayerMask placeableLayers = ~0;
    public float detectRadius = 0.35f;

    BuildingDef _current;
    GameObject _spawnedPreviewGO;
    Vector3Int _lastBigCell = new(int.MinValue, int.MinValue, 0);
    Vector2Int _lastFineCell = new(int.MinValue, int.MinValue);
    readonly Dictionary<Vector3Int, GameObject> _placedByCell = new();
    readonly Dictionary<Vector2Int, GameObject> _placedFine = new();
    readonly HashSet<Vector3Int> _protectedCells = new();

    // ========================================================================
    public void SetSelected(BuildingDef def)
    {
        _current = def;
        ClearPreview();
    }

    void Awake()
    {
        if (!grid) grid = GetComponentInParent<Grid>();
        RebuildMapFromParent();
    }

    void OnDisable()
    {
        ClearPreview();
        UpdatePointerActive(false, false);
    }

    void Update()
    {
        if (_current == null) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null || Camera.main == null) return;

        Vector2 sp = mouse.position.ReadValue();
        Vector3 world = Camera.main.ScreenToWorldPoint(new Vector3(sp.x, sp.y, -Camera.main.transform.position.z));
        world.z = 0f;

        if (useFineGrid)
        {
            UpdatePointerActive(false, false);
            Vector2Int fineCell = WorldToFineCell(world, fineCellSize);
            Vector3 fineCenter = FineCellToWorld(fineCell, fineCellSize) + hoverOffset;

            UpdatePreviewAndPointerFine(fineCell, fineCenter);

            if (mouse.leftButton.wasPressedThisFrame)
                PlaceAtFine(fineCell, fineCenter);
            if (mouse.rightButton.wasPressedThisFrame)
                DeleteAtFine(fineCell, fineCenter);
        }
        else
        {
            var cell = grid.WorldToCell(world);
            UpdatePreviewAndPointerBig(cell);

            if (mouse.leftButton.wasPressedThisFrame)
                PlaceAtBig(cell);
            if (mouse.rightButton.wasPressedThisFrame)
                DeleteAtBig(cell);
        }
#endif
    }

    // ========================================================================
    // でかいセル配置
    // ========================================================================
    void PlaceAtBig(Vector3Int cell)
    {
        if (_current?.prefab == null) return;
        if (!CanPlaceAtBig(cell)) return;

        // --- 偶数セル補正 ---
        float cs = grid.cellSize.x;
        bool evenX = (_current.cellsWidth % 2 == 0);
        bool evenY = (_current.cellsHeight % 2 == 0);
        float offsetX = evenX ? cs * 0.5f : 0f;
        float offsetY = evenY ? cs * 0.5f : 0f;

        Vector3 basePos = grid.GetCellCenterWorld(cell) + hoverOffset;
        Vector3 pos = basePos + new Vector3(offsetX, offsetY, 0f);

        var go = Instantiate(_current.prefab, pos, Quaternion.identity, prefabParent);
        _placedByCell[cell] = go;

        // Base出現（あなたのゲーム仕様通り）
        if (_current != null && _current.isHexTile)
        {
            var ui = Object.FindFirstObjectByType<StartMenuUI>();
            if (ui != null)
            {
                bool spawned = ui.TrySpawnBaseAt(pos);
                if (spawned)
                    _protectedCells.Add(cell);
            }
        }

        // --- FlowField 連携 ---
        if (flowField != null)
        {
            RegisterBuildingToFlowField(_current, pos, true);
        }
    }

    void DeleteAtBig(Vector3Int cell)
    {
        if (_protectedCells.Contains(cell))
            return;

        if (_placedByCell.TryGetValue(cell, out var go) && go)
        {
            Vector3 w = go.transform.position;
            _placedByCell.Remove(cell);
            Destroy(go);

            if (flowField != null)
                RegisterBuildingToFlowField(_current, w, false);
            return;
        }

        // 物理フォールバック
        Vector3 c = grid.GetCellCenterWorld(cell) + hoverOffset;
        var hits = Physics2D.OverlapCircleAll(c, detectRadius, placeableLayers);
        foreach (var h in hits)
        {
            var hc = grid.WorldToCell(h.transform.position - hoverOffset);
            if (_protectedCells.Contains(hc)) continue;

            _placedByCell.Remove(hc);
            Vector3 w = h.transform.position;
            Destroy(h.transform.gameObject);

            if (flowField != null)
                RegisterBuildingToFlowField(_current, w, false);
            return;
        }
    }

    bool CanPlaceAtBig(Vector3Int cell)
    {
        if (requireUnderlyingTile && groundTilemap != null && !groundTilemap.HasTile(cell))
            return false;
        if (_placedByCell.ContainsKey(cell))
            return false;
        return true;
    }

    void UpdatePreviewAndPointerBig(Vector3Int cell)
    {
        if (cell != _lastBigCell)
        {
            _lastBigCell = cell;
            MovePreview(grid.GetCellCenterWorld(cell) + hoverOffset);
        }

        bool can = CanPlaceAtBig(cell);
        Vector3 center = grid.GetCellCenterWorld(cell) + hoverOffset;

        if (pointerOK || pointerNG)
        {
            if (can) UpdatePointerActive(true, false, center);
            else UpdatePointerActive(false, true, center);
        }

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
        if (!CanPlaceAtFine(fcell, worldCenter)) return;

        var go = Instantiate(_current.prefab, worldCenter, Quaternion.identity, prefabParent);
        _placedFine[fcell] = go;

        if (flowField != null)
            RegisterBuildingToFlowField(_current, worldCenter, true);
    }

    void DeleteAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_placedFine.TryGetValue(fcell, out var go) && go)
        {
            _placedFine.Remove(fcell);
            Destroy(go);

            if (flowField != null)
                RegisterBuildingToFlowField(_current, worldCenter, false);
            return;
        }

        var hits = Physics2D.OverlapCircleAll(worldCenter, detectRadius, placeableLayers);
        foreach (var h in hits)
        {
            Vector2Int hc = WorldToFineCell(h.transform.position, fineCellSize);
            _placedFine.Remove(hc);
            Vector3 w = h.transform.position;
            Destroy(h.transform.gameObject);

            if (flowField != null)
                RegisterBuildingToFlowField(_current, w, false);
            return;
        }
    }

    bool CanPlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_placedFine.ContainsKey(fcell))
            return false;

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

        return true;
    }

    void UpdatePreviewAndPointerFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (fcell != _lastFineCell)
        {
            _lastFineCell = fcell;
            MovePreview(worldCenter);
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
    // FlowField 反映ロジック（新規追加）
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
    }

    void UpdatePointerActive(bool showOK, bool showNG, Vector3? moveTo = null)
    {
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

    static Vector3 FineCellToWorld(Vector2Int cell, float cellSize)
    {
        float x = cell.x * cellSize + cellSize * 0.5f;
        float y = cell.y * cellSize + cellSize * 0.5f;
        return new Vector3(x, y, 0f);
    }
}
