using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Tilemaps;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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

    [Header("=== FlowField ===")]
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

    // 配置済みを記録
    readonly Dictionary<Vector3Int, GameObject> _placedByCell = new();
    readonly Dictionary<Vector2Int, GameObject> _placedFine = new();
    readonly HashSet<Vector3Int> _protectedCells = new();

    // 論理セル(0.25×0.25)の占有
    HashSet<Vector2Int> _occupiedLogical = new();

    void Awake()
    {
        if (!grid) grid = GetComponentInParent<Grid>();
        RebuildMapFromParent();

        // 起動時にポインタを隠す
        if (pointerOK) pointerOK.gameObject.SetActive(false);
        if (pointerNG) pointerNG.gameObject.SetActive(false);
    }

    void OnDisable()
    {
        ClearPreview();
        UpdatePointerActive(false, false);
    }

    public void SetSelected(BuildingDef def)
    {
        _current = def;
        ClearPreview();
    }

    void Update()
    {
        // カメラが無いときは何もしない
        if (Camera.main == null) return;

        // いまのズーム量を取る（ズームアウト時ポインタ用）
        float camSize = Camera.main.orthographicSize;

#if ENABLE_INPUT_SYSTEM
    // マウス位置（Input System版）
    var mouse = Mouse.current;
    if (mouse == null) return;
    Vector2 sp = mouse.position.ReadValue();
    Vector3 world = Camera.main.ScreenToWorldPoint(
        new Vector3(sp.x, sp.y, -Camera.main.transform.position.z));
    world.z = 0f;
#else
        // マウス位置（旧Input版）
        Vector3 world = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        world.z = 0f;
#endif

        // ① ズームアウト時（size>3）はとりあえず六角の位置にポインタだけ出す
        //    ※ 建築を選んでいなくても出したい要件なので、_current の前でやる
        if (camSize > 3f && !useFineGrid)
        {
            Vector3Int camCell = grid.WorldToCell(world);
            Vector3 center = grid.GetCellCenterWorld(camCell) + hoverOffset;
            if (pointerOK)
            {
                pointerOK.position = center;
                pointerOK.gameObject.SetActive(true);
            }
        }

        // ② 建物が選ばれていないならここで終わり（上のポインタだけ出す）
        if (_current == null) return;

        // ③ UIの上では建てたり壊したりさせない
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

#if ENABLE_INPUT_SYSTEM
    // ④ 細かいグリッドモード（0.25×0.25）のとき
    if (useFineGrid)
    {
        // マウスの世界座標をそのまま使ってセルを計算
        Vector2Int fcell = WorldToFineCell(world, fineCellSize);
        Vector3 center = FineCellToWorld(fcell, fineCellSize) + hoverOffset;

        // プレビューとポインタの表示更新
        UpdatePreviewAndPointerFine(fcell, center);

        // 左クリックで配置
        if (mouse.leftButton.wasPressedThisFrame)
            PlaceAtFine(fcell, center);

        // 右クリックで削除（←今回のポイント。グリッド中心ではなくマウス位置を渡す）
        if (mouse.rightButton.wasPressedThisFrame)
            DeleteAtFine(fcell, world);     // center じゃなく world を渡す
    }
    else
    {
        // ⑤ 大きいグリッド（六角/1マス）モードのとき
        Vector3Int cell = grid.WorldToCell(world);

        // プレビューとOK/NGポインタを更新
        UpdatePreviewAndPointerBig(cell);

        // 左クリックで配置
        if (mouse.leftButton.wasPressedThisFrame)
            PlaceAtBig(cell);

        // 右クリックで削除（←マウス位置で消す版）
        if (mouse.rightButton.wasPressedThisFrame)
            DeleteAtBig(cell, world);
    }
#else
        // 旧InputSystem版を使うならここに同じ処理を書く
#endif
    }

    // 細かいセル（0.25×0.25）用のプレビュー＆ポインタ更新
    void UpdatePreviewAndPointerFine(Vector2Int fcell, Vector3 worldCenter)
    {
        // 前回と違うセルならプレビューを動かす
        if (fcell != _lastFineCell)
        {
            _lastFineCell = fcell;
            MovePreview(worldCenter);
        }

        bool can = CanPlaceAtFine(fcell, worldCenter);

        // ゴーストの色をOK/NGで変える
        if (_spawnedPreviewGO)
        {
            var col = can
                ? new Color(1f, 1f, 1f, previewAlpha)
                : new Color(1f, 0.4f, 0.4f, previewAlpha);
            SetSpriteColor(_spawnedPreviewGO.transform, col);
        }

        // 細かいモードのときは OK/NG ポインタは基本消しておく
        if (pointerOK) pointerOK.gameObject.SetActive(false);
        if (pointerNG) pointerNG.gameObject.SetActive(false);
    }

    // 細かいセルに今の建物を置けるかどうか
    bool CanPlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        // すでにこの細かいセルに置いてあるならNG
        if (_placedFine.ContainsKey(fcell))
            return false;

        // 六角タイルの上だけに置きたいとき
        if (hexTilemap != null)
        {
            var hcell = hexTilemap.WorldToCell(worldCenter);
            if (!hexTilemap.HasTile(hcell))
                return false;
        }

        // Buildableタグの上だけに置きたいとき
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

        // FlowField用の論理占有にぶつかっていないか
        float cs = flowField ? flowField.cellSize : fineCellSize;
        foreach (var lc in EnumerateLogicalCells(_current, worldCenter, cs))
        {
            if (_occupiedLogical.Contains(lc))
                return false;
        }

        return true;
    }

    // ================== 大きいセル配置 ==================
    void PlaceAtBig(Vector3Int cell)
    {
        if (_current?.prefab == null) return;
        if (!CanPlaceAtBig(cell)) return;

        float cs = grid.cellSize.x;
        bool evenX = (_current.cellsWidth % 2 == 0);
        bool evenY = (_current.cellsHeight % 2 == 0);
        float offsetX = evenX ? cs * 0.5f : 0f;
        float offsetY = evenY ? cs * 0.5f : 0f;

        Vector3 basePos = grid.GetCellCenterWorld(cell) + hoverOffset;
        Vector3 pos = basePos + new Vector3(offsetX, offsetY, 0f);

        var go = Instantiate(_current.prefab, pos, Quaternion.identity, prefabParent);
        _placedByCell[cell] = go;

        // ★ ここでDefを保存しておく（削除時に使う）
        var info = go.AddComponent<PlacedBuildingInfo>();
        info.def = _current;

        // Baseを守る処理がある場合
        if (_current.isHexTile)
        {
            var ui = Object.FindFirstObjectByType<StartMenuUI>();
            if (ui != null)
            {
                bool spawned = ui.TrySpawnBaseAt(pos);
                if (spawned)
                    _protectedCells.Add(cell);
            }
        }

        // 占有登録
        RegisterOccupancyFor(_current, pos, true);

        // FlowField登録
        if (flowField != null)
            RegisterBuildingToFlowField(_current, pos, true);
    }

    void DeleteAtBig(Vector3Int cell, Vector3 mouseWorld)
    {
        // mouseWorld は Update() の中で計算した「マウスのワールド座標」を渡す想定
        // もし今の関数シグネチャが変えられないなら、関数の中で Camera.main から取ってもいいです

        // 1) Baseタイルは消さない
        if (grid != null)
        {
            var big = grid.WorldToCell(mouseWorld - hoverOffset);
            if (_protectedCells.Contains(big))
                return;
        }

        float r = detectRadius;

        // 2D
        var hits2d = Physics2D.OverlapCircleAll(mouseWorld, r, placeableLayers);
        if (hits2d != null && hits2d.Length > 0)
        {
            foreach (var h in hits2d)
            {
                var big = grid.WorldToCell(h.transform.position - hoverOffset);
                if (_protectedCells.Contains(big))
                    continue;

                var info = h.GetComponent<PlacedBuildingInfo>();
                var def = info ? info.def : null;
                Vector3 w = h.transform.position;

                Destroy(h.gameObject);

                if (def != null)
                {
                    RegisterOccupancyFor(def, w, false);
                    if (flowField != null)
                        RegisterBuildingToFlowField(def, w, false);
                }
                return;
            }
        }

        // 3D
        var hits3d = Physics.OverlapBox(mouseWorld, Vector3.one * r, Quaternion.identity);
        if (hits3d != null && hits3d.Length > 0)
        {
            foreach (var h in hits3d)
            {
                var go = h.gameObject;

                var big = grid.WorldToCell(go.transform.position - hoverOffset);
                if (_protectedCells.Contains(big))
                    continue;

                var info = go.GetComponent<PlacedBuildingInfo>();
                var def = info ? info.def : null;
                Vector3 w = go.transform.position;

                Destroy(go);

                if (def != null)
                {
                    RegisterOccupancyFor(def, w, false);
                    if (flowField != null)
                        RegisterBuildingToFlowField(def, w, false);
                }
                return;
            }
        }
    }

    bool CanPlaceAtBig(Vector3Int cell)
    {
        if (_current == null) return false;

        // ① タイルが無いならNG
        if (requireUnderlyingTile && groundTilemap && !groundTilemap.HasTile(cell))
            return false;

        // ② まず自分が覚えてるセルと重なってないか
        //    （ここは今までのチェック）
        // ※ これだけだと「シーンに元からあるHex」は分からない
        // if (_placedByCell.ContainsKey(cell)) return false; ←これは残してもOKだけど、
        // 今回は collider のほうを優先するのでコメントしておきます
        // if (_placedByCell.ContainsKey(cell)) return false;

        // ③ 物理的に何かあるかを見る（←ここが新規）
        Vector3 worldCenter = grid.GetCellCenterWorld(cell) + hoverOffset;
        // 少し小さめでもいい
        var hits = Physics2D.OverlapCircleAll(worldCenter, detectRadius, placeableLayers);
        if (hits != null && hits.Length > 0)
        {
            // ここにあるのが「今選んでるPrefabそのもの」で、
            // かつ「重ね置きを許す」みたいな仕様ならここで分岐する
            return false; // なんかあるので置けない
        }

        // ④ 0.25 の論理グリッド側もチェック（3×3の重なり対策）
        float csLogic = flowField ? flowField.cellSize : 0.25f;
        float csBig = grid.cellSize.x;
        bool evenX = (_current.cellsWidth % 2 == 0);
        bool evenY = (_current.cellsHeight % 2 == 0);
        float offsetX = evenX ? csBig * 0.5f : 0f;
        float offsetY = evenY ? csBig * 0.5f : 0f;
        Vector3 pos = worldCenter + new Vector3(offsetX, offsetY, 0f);

        foreach (var lc in EnumerateLogicalCells(_current, pos, csLogic))
        {
            if (_occupiedLogical.Contains(lc))
                return false;
        }

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

    // ================== 細かいセル配置 ==================
    void PlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_current?.prefab == null) return;
        if (!CanPlaceAtFine(fcell, worldCenter)) return;

        var go = Instantiate(_current.prefab, worldCenter, Quaternion.identity, prefabParent);
        _placedFine[fcell] = go;

        var info = go.AddComponent<PlacedBuildingInfo>();
        info.def = _current;

        RegisterOccupancyFor(_current, worldCenter, true);

        if (flowField != null)
            RegisterBuildingToFlowField(_current, worldCenter, true);
    }

    void DeleteAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        // worldCenter は「マウスの真下」だと思ってOKという前提で書き直す

        // 1) Baseタイルは消さない
        if (grid != null)
        {
            var big = grid.WorldToCell(worldCenter - hoverOffset);
            if (_protectedCells.Contains(big))
                return;
        }

        // 2) まず 2D をマウス位置で拾う（←ここをグリッド中心じゃなくする）
        float r = detectRadius;
        var hits2d = Physics2D.OverlapCircleAll(worldCenter, r, placeableLayers);
        if (hits2d != null && hits2d.Length > 0)
        {
            foreach (var h in hits2d)
            {
                // Baseは消さない
                var big = grid.WorldToCell(h.transform.position - hoverOffset);
                if (_protectedCells.Contains(big))
                    continue;

                var info = h.GetComponent<PlacedBuildingInfo>();
                var def = info ? info.def : null;
                Vector3 w = h.transform.position;

                Destroy(h.gameObject);

                if (def != null)
                {
                    RegisterOccupancyFor(def, w, false);
                    if (flowField != null)
                        RegisterBuildingToFlowField(def, w, false);
                }
                return;
            }
        }

        // 3) 念のため 3D もマウス位置で拾う
        var hits3d = Physics.OverlapBox(worldCenter, Vector3.one * r, Quaternion.identity);
        if (hits3d != null && hits3d.Length > 0)
        {
            foreach (var h in hits3d)
            {
                var go = h.gameObject;

                var big = grid.WorldToCell(go.transform.position - hoverOffset);
                if (_protectedCells.Contains(big))
                    continue;

                var info = go.GetComponent<PlacedBuildingInfo>();
                var def = info ? info.def : null;
                Vector3 w = go.transform.position;

                Destroy(go);

                if (def != null)
                {
                    RegisterOccupancyFor(def, w, false);
                    if (flowField != null)
                        RegisterBuildingToFlowField(def, w, false);
                }
                return;
            }
        }

        // ここまで来たら「マウスの真下には何もなかった」
        // Debug.Log("[DeleteAtFine] nothing at " + worldCenter);
    }

    // ================== FlowField 反映（shape優先） ==================
    void RegisterBuildingToFlowField(BuildingDef def, Vector3 pos, bool blocked)
    {
        if (def == null || flowField == null) return;

        float cs = flowField.cellSize;
        int w = Mathf.Max(1, def.cellsWidth);
        int h = Mathf.Max(1, def.cellsHeight);
        bool hasShape = def.shapeData != null && def.shapeData.Count > 0;

        for (int iy = 0; iy < h; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                if (hasShape)
                {
                    if (ix >= def.shapeSize || iy >= def.shapeSize)
                        continue;
                    if (!def.GetShape(ix, iy))
                        continue;
                }

                float wx = pos.x + (ix - (w - 1) / 2f) * cs;
                float wy = pos.y + (iy - (h - 1) / 2f) * cs;

                if (blocked) flowField.MarkBlocked(wx, wy);
                else flowField.MarkWalkable(wx, wy);
            }
        }

        if (def.rebuildAfterPlace)
            flowField.Rebuild();
    }

    // ================== 占有の登録/解除 ==================
    void RegisterOccupancyFor(BuildingDef def, Vector3 worldCenter, bool occupy)
    {
        float cs = flowField ? flowField.cellSize : 0.25f;

        foreach (var lc in EnumerateLogicalCells(def, worldCenter, cs))
        {
            if (occupy) _occupiedLogical.Add(lc);
            else _occupiedLogical.Remove(lc);
        }
    }

    IEnumerable<Vector2Int> EnumerateLogicalCells(BuildingDef def, Vector3 center, float cellSize)
    {
        if (def == null) yield break;

        int w = Mathf.Max(1, def.cellsWidth);
        int h = Mathf.Max(1, def.cellsHeight);
        bool hasShape = def.shapeData != null && def.shapeData.Count > 0;

        for (int iy = 0; iy < h; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                if (hasShape)
                {
                    if (ix >= def.shapeSize || iy >= def.shapeSize)
                        continue;
                    if (!def.GetShape(ix, iy))
                        continue;
                }

                float wx = center.x + (ix - (w - 1) / 2f) * cellSize;
                float wy = center.y + (iy - (h - 1) / 2f) * cellSize;

                int gx = Mathf.FloorToInt(wx / cellSize);
                int gy = Mathf.FloorToInt(wy / cellSize);
                yield return new Vector2Int(gx, gy);
            }
        }
    }

    // ================== プレビューなど ==================
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
            pointerOK.gameObject.SetActive(showOK);
        }
        if (pointerNG)
        {
            if (moveTo.HasValue) pointerNG.position = moveTo.Value;
            pointerNG.gameObject.SetActive(showNG);
        }
    }

    void RebuildMapFromParent()
    {
        _placedByCell.Clear();
        _placedFine.Clear();
        _occupiedLogical.Clear();

        if (!prefabParent) return;

        foreach (Transform child in prefabParent)
        {
            // ここは「起動時にすでに置いてあるやつ」を再登録したいなら書く
            var info = child.GetComponent<PlacedBuildingInfo>();
            BuildingDef def = info ? info.def : _current;

            if (grid != null)
            {
                var cell = grid.WorldToCell(child.position - hoverOffset);
                if (!_placedByCell.ContainsKey(cell))
                    _placedByCell.Add(cell, child.gameObject);
            }

            RegisterOccupancyFor(def, child.position, true);
        }
    }

    static void SetSpriteColor(Transform root, Color col)
    {
        foreach (var r in root.GetComponentsInChildren<SpriteRenderer>(true))
            r.color = col;
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
