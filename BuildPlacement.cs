using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Tilemaps;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Unity.Cinemachine;

public class BuildPlacement : MonoBehaviour
{
    [Header("=== Common targets ===")]
    public Grid grid;
    public Transform prefabParent;

    [Header("=== Big-cell placement (hex / normal) ===")]
    public Tilemap groundTilemap;
    public bool requireUnderlyingTile = true;

    [Header("=== Fine-cell placement (0.25 �~ 0.25) ===")]
    public bool useFineGrid = false;
    public float fineCellSize = 0.25f;
    public Tilemap hexTilemap;
    public bool requireBuildableForFine = true;
    public float fineBuildableRadius = 0.1f;

    [Tooltip("FlowField ������Ȃ�A�u����/�������Ƃ��Ƀu���b�N��Ԃ�`����")]
    public FlowField025 flowField;

    [Header("=== Preview (ghost) ===")]
    public Transform prefabPreview;
    [Range(0f, 1f)] public float previewAlpha = 0.45f;
    public Vector3 hoverOffset = Vector3.zero;

    [Header("=== Pointer (OK/NG) ===")]
    public Transform pointerOK;   // SelectTile
    public Transform pointerNG;   // SelectFalse

    [Header("=== Fallback delete (optional) ===")]
    public LayerMask placeableLayers = ~0;
    public float detectRadius = 0.35f;

    [Header("=== Zoom auto switch ===")]
    public CinemachineCamera vcam;
    public float fineGridThreshold = 3f;

    [Header("=== Drone build ===")]
    [Tooltip("true�Ȃ�A�����͂����ɂ͌������A�h���[�������Ă��猚��")]
    public bool useDroneBuild = true;
    public DroneBuildManager droneManager;

    [Header("=== Base auto place ===")]
    [Tooltip("�ŏ���1�񂾂���������Ă�B���[�U�[���ʂ̌�����I��ł��Ă�Base�ɂȂ�")]
    public BuildingDef baseDef;

    [Tooltip("Base�Ɠ����ɉ��ɕ~���Z�p�^�C���� BuildingDef")]
    public BuildingDef baseHexDef;

    // Base �����ۂɁu�����v�������ǂ����i�h���[���Ō��ďI��������_�� true�j
    public static bool s_baseBuilt = false;

    // �A���ݒu�����ǂ���
    bool _isDragging = false;
    readonly List<(Vector2Int cell, Vector3 center)> _previewCells = new();
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
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();
        RebuildMapFromParent();
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
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            UpdatePointerActive(false, false);
            return;
        }

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
            Vector3 baseCenter = FineCellToWorld(fineCell, fineCellSize) + hoverOffset;
            Vector3 evenOff = GetEvenSizeOffsetForFine(_current);
            Vector3 finalCenter = baseCenter + evenOff;

            bool canHere = CanPlaceAtFine(fineCell, finalCenter);

            EnsurePrefabPreview();
            if (_spawnedPreviewGO)
            {
                _spawnedPreviewGO.transform.position = finalCenter;
                var col = canHere ? new Color(1f, 1f, 1f, previewAlpha)
                                  : new Color(1f, 0.4f, 0.4f, previewAlpha);
                SetSpriteColor(_spawnedPreviewGO.transform, col);
            }

            if (mouse.leftButton.wasPressedThisFrame)
            {
                _isDragging = true;
                _previewCells.Clear();
                ClearDragGhosts();
            }

            if (_isDragging && mouse.leftButton.isPressed)
            {
                if (!FootprintOverlapsAny(fineCell, _current, _previewCells))
                {
                    CreateDragGhost(finalCenter, CanPlaceAtFine(fineCell, finalCenter));
                    _previewCells.Add((fineCell, finalCenter));
                }
            }

            if (_isDragging && mouse.leftButton.wasReleasedThisFrame)
            {
                // �� �������u���ԂɃh���[���ցv�ɂȂ�
                foreach (var p in _previewCells)
                {
                    if (CanPlaceAtFine(p.cell, p.center))
                        PlaceAtFine(p.cell, p.center);
                }
                _previewCells.Clear();
                ClearDragGhosts();
                _isDragging = false;
            }

            if (mouse.rightButton.wasPressedThisFrame)
            {
                if (_isDragging || _previewCells.Count > 0)
                {
                    _previewCells.Clear();
                    ClearDragGhosts();
                    _isDragging = false;
                }
                else
                {
                    DeleteAtFine(fineCell, finalCenter);
                }
            }

            return;
        }
        else
        {
            var cell = grid.WorldToCell(world);

            if (_current == null)
            {
                ShowPointerForHexHover(cell);
            }
            else
            {
                UpdatePreviewAndPointerBig(cell);

                if (mouse.leftButton.wasPressedThisFrame)
                    PlaceAtBig(cell);
                if (mouse.rightButton.wasPressedThisFrame)
                    DeleteAtBig(cell);
            }
        }
#endif
    }

    void AutoUpdateUseFineGridByZoom()
    {
        float ortho = -1f;
        if (vcam != null) ortho = vcam.Lens.OrthographicSize;
        else if (Camera.main && Camera.main.orthographic) ortho = Camera.main.orthographicSize;

        if (ortho < 0f) return;

        bool wantFine = ortho <= fineGridThreshold;
        if (wantFine != useFineGrid)
        {
            useFineGrid = wantFine;
            ClearPreview();
            UpdatePointerActive(false, false);
        }
    }

    // ========================= �Y�[���A�E�g(�Z�p)�n =========================
    void ShowPointerForHexHover(Vector3Int cell)
    {
        if (groundTilemap == null)
        {
            Vector3 center = grid.GetCellCenterWorld(cell) + hoverOffset;
            UpdatePointerActive(true, false, center);
            return;
        }

        bool hasTile = groundTilemap.HasTile(cell);

        Vector3 pos = grid.GetCellCenterWorld(cell) + hoverOffset;
        if (hasTile)
            UpdatePointerActive(true, false, pos);
        else
            UpdatePointerActive(false, true, pos);
    }

    void PlaceAtBig(Vector3Int cell)
    {
        // �����I��łȂ��Ȃ�u���Ȃ�
        if (_current?.prefab == null && (baseDef == null || s_baseBuilt))
            return;
        if (!CanPlaceAtBig(cell)) return;

        // ���������|�C���g�F
        // �܂�Base�������Ă��Ȃ��Ȃ�A���[�U�[������I��ł��Ă� baseDef �����Ă�
        BuildingDef defToPlace = _current;
        bool isFirstBase = false;
        if (!s_baseBuilt && baseDef != null)
        {
            defToPlace = baseDef;
            isFirstBase = true;
        }

        if (defToPlace == null) return; // �O�̂���

        Vector3 pos = grid.GetCellCenterWorld(cell) + hoverOffset;

        if (useDroneBuild && droneManager != null)
        {
            // 1) �S�[�X�g��u���ė\��
            GameObject ghost = Instantiate(defToPlace.prefab, pos, Quaternion.identity, prefabParent);
            DisableColliders(ghost.transform);
            SetSpriteColor(ghost.transform, new Color(1f, 1f, 1f, previewAlpha));

            // ���Ő�L�BBase�Ȃ�󂹂Ȃ��悤�ɂ���
            _placedByCell[cell] = ghost;
            if (isFirstBase)
                _protectedCells.Add(cell);

            // 2) �h���[���Ɉ˗��i�������Ŏ��ۂɌ��j
            droneManager.EnqueueBigBuild(this, defToPlace, cell, pos, ghost);
        }
        else
        {
            // �h���[�����g��Ȃ��Ƃ��͑�����
            var go = Instantiate(defToPlace.prefab, pos, Quaternion.identity, prefabParent);
            _placedByCell[cell] = go;

            // Base �Ȃ�󂹂Ȃ��悤��
            if (isFirstBase)
                _protectedCells.Add(cell);

            // �����Ɠ����ɁuBase�ł�����v��ʒm
            if (isFirstBase)
                s_baseBuilt = true;

            // �����O�̕����� StartMenuUI �ɂ��`�������Ȃ炱����
            var ui = Object.FindFirstObjectByType<StartMenuUI>();
            if (isFirstBase && ui != null)
            {
                ui.TrySpawnBaseAt(pos);
            }

            // FlowField ���X�V�������Ȃ炱����
            if (flowField != null)
                RegisterBuildingToFlowField(defToPlace, pos, true);
        }
    }

    // �h���[�������������Ƃ��ɌĂԁA�Z�p(�r�b�O)�Z���p�̌y�ʊ�������
    // Destroy��Instantiate�������ɁA�ŏ��ɒu�����S�[�X�g���g�����`�h�ɂ��邾���B
    // Base�����Ƃ�FlowField�̏d�������� DroneBuildManager ���Ōォ����B
    public void FinalizeBigPlacement(BuildingDef def, Vector3Int cell, Vector3 pos, GameObject ghost)
    {
        if (def == null) return;

        GameObject placedGo = null;

        // �������ŏ��߂āuBase�����������v�Ƃ���
        if (!s_baseBuilt && baseDef != null && def == baseDef)
        {
            s_baseBuilt = true;
            _protectedCells.Add(cell);

            // FlowField �̃S�[���������ɂ���
            if (flowField != null)
                flowField.SetTargetWorld(pos);

            // ������ ������ǉ� ������
            // �ׂ����O���b�h���u���̃^�C���ɂ��K���~���āv��HexPerTileFineGrid�ɓ`����
            var hexFine = Object.FindFirstObjectByType<HexPerTileFineGrid>();
            if (hexFine != null)
                hexFine.ForceCreateAtWorld(pos);
            // ������ �����܂Œǉ� ������

            // UI�̋�����
            var ui = Object.FindFirstObjectByType<StartMenuUI>();
            if (ui != null)
            {
                ui.TrySpawnBaseAt(pos);
            }

            // ���ɘZ�p���o�����
            if (baseHexDef != null && baseHexDef.prefab != null)
            {
                var hexGo = Instantiate(baseHexDef.prefab, pos, Quaternion.identity, prefabParent);
                foreach (var c in hexGo.GetComponentsInChildren<Collider2D>(true))
                    c.enabled = false;
                foreach (var c in hexGo.GetComponentsInChildren<Collider>(true))
                    c.enabled = false;
            }
        }

        if (ghost != null)
        {
            // �S�[�X�g�������`��
            SetSpriteColor(ghost.transform, Color.white);
            foreach (var c in ghost.GetComponentsInChildren<Collider2D>(true))
                c.enabled = true;
            foreach (var c in ghost.GetComponentsInChildren<Collider>(true))
                c.enabled = true;
            ghost.transform.position = pos;

            _placedByCell[cell] = ghost;
            placedGo = ghost;
        }
        else
        {
            var go = Instantiate(def.prefab, pos, Quaternion.identity, prefabParent);
            _placedByCell[cell] = go;
            placedGo = go;
        }

        // �� ����������̒ǉ����� ��
        // �u���̌�����Base�������v���u���ɘZ�p�^�C�����~�������v
        bool isBase = (!s_baseBuilt && baseDef != null && def == baseDef)    // �����Base
                      || (baseDef != null && def == baseDef);                // �O�̂���2��ڈȍ~���`�F�b�N

        if (isBase)
        {
            // Base�����������Ƃ��� static �t���O�𗧂Ă�
            s_baseBuilt = true;
            _protectedCells.Add(cell);

            // �������Łu�ꏏ�ɕ~���Z�p�^�C���v�𐶐�
            if (baseHexDef != null && baseHexDef.prefab != null)
            {
                // �����ʒu�ɕ~���B�����w�ʂɂ������Ȃ� z �� -0.1f �Ƃ��ɂ��Ă�����
                Vector3 hexPos = pos;
                // hexPos.z = pos.z + 0.01f; // �K�v�Ȃ�

                var hexGo = Instantiate(baseHexDef.prefab, hexPos, Quaternion.identity, prefabParent);
                // �Z�p�͂Ԃ���Ȃ��Ă����Ȃ�R���C�_�[��؂��Ă��悢
                foreach (var c in hexGo.GetComponentsInChildren<Collider2D>(true))
                    c.enabled = false;
                foreach (var c in hexGo.GetComponentsInChildren<Collider>(true))
                    c.enabled = false;
            }

            // �i�]���ǂ��� StartMenuUI �ɂ��m�点�����ꍇ�j
            var ui = Object.FindFirstObjectByType<StartMenuUI>();
            if (ui != null)
            {
                ui.TrySpawnBaseAt(pos);
            }
        }

        // FlowField �ɂ��`����
        if (flowField != null)
            RegisterBuildingToFlowField(def, pos, true);
    }

    void DeleteAtBig(Vector3Int cell)
    {
        if (_protectedCells.Contains(cell))
            return;

        if (_placedByCell.TryGetValue(cell, out var go) && go)
        {
            _placedByCell.Remove(cell);
            Destroy(go);
            return;
        }

        Vector3 c = grid.GetCellCenterWorld(cell) + hoverOffset;
        var hits = Physics2D.OverlapCircleAll(c, detectRadius, placeableLayers);
        foreach (var h in hits)
        {
            var hc = grid.WorldToCell(h.transform.position - hoverOffset);
            if (_protectedCells.Contains(hc)) continue;

            _placedByCell.Remove(hc);
            Destroy(h.gameObject);
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

        if (can) UpdatePointerActive(true, false, center);
        else UpdatePointerActive(false, true, center);

        if (_spawnedPreviewGO)
        {
            var col = can ? new Color(1f, 1f, 1f, previewAlpha)
                          : new Color(1f, 0.4f, 0.4f, previewAlpha);
            SetSpriteColor(_spawnedPreviewGO.transform, col);
        }
    }

    // ========================= �ׂ����O���b�h�n =========================
    void PlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_current?.prefab == null) return;

        Vector3 finalCenter = worldCenter;
        if (!CanPlaceAtFine(fcell, finalCenter)) return;

        // �܂��́u�S�[�X�g�ŗ\��v
        GameObject ghost = Instantiate(_current.prefab, finalCenter, Quaternion.identity, prefabParent);
        DisableColliders(ghost.transform);
        SetSpriteColor(ghost.transform, new Color(1f, 1f, 1f, previewAlpha));

        // ��L�Z�����S�[�X�g�Ŗ��߂Ă����i���̘A���ݒu�ł��Ԃ�Ȃ��悤�ɂ���j
        ReserveFineCells(fcell, _current, ghost);

        // �h���[���ɓ�����
        if (useDroneBuild && droneManager != null)
        {
            droneManager.EnqueueFineBuild(this, _current, fcell, finalCenter, ghost);
        }
        else
        {
            // �h���[�������Ȃ�/�g��Ȃ��Ƃ��͂�����������
            FinalizeFinePlacement(_current, fcell, finalCenter, ghost);
        }
    }

    // �h���[���������ɌĂ΂��
    // �h���[�����I������Ƃ��ɌĂ΂��E�y���Łi�ׂ����O���b�h�j
    public void FinalizeFinePlacement(BuildingDef def, Vector2Int fcell, Vector3 pos, GameObject ghost)
    {
        if (ghost == null) return;

        // �S�[�X�g �� �{����
        SetSpriteColor(ghost.transform, Color.white);
        foreach (var c in ghost.GetComponentsInChildren<Collider2D>(true)) c.enabled = true;
        foreach (var c in ghost.GetComponentsInChildren<Collider>(true)) c.enabled = true;
        ghost.transform.position = pos;

        // �\��Z�������̃I�u�W�F�N�g�ɍ����ւ���
        var tmp = new List<Vector2Int>();
        foreach (var kv in _placedFine)
            if (kv.Value == ghost)
                tmp.Add(kv.Key);
        foreach (var k in tmp)
            _placedFine[k] = ghost;

        // ��������ǉ����遚
        if (flowField != null && def != null)
            RegisterBuildingToFlowField(def, pos, true);
    }

    void DeleteAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        Vector3 finalCenter = worldCenter;

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

        // 1) �Z�p�^�C���̑��݃`�F�b�N
        bool onValidHex = true;

        if (hexTilemap != null)
        {
            var hcell = hexTilemap.WorldToCell(worldCenter);
            onValidHex = hexTilemap.HasTile(hcell);

            if (!onValidHex)
            {
                // �� Tilemap�Ƀ^�C���͖������ǁAHexPerTileFineGrid �������������Ă邩������Ȃ�
                var hexFine = Object.FindFirstObjectByType<HexPerTileFineGrid>();
                if (hexFine != null && hexFine.HasFineAtWorld(worldCenter))
                {
                    onValidHex = true;
                }
            }

            if (!onValidHex)
                return false;
        }

        // 2) Buildable �`�F�b�N
        if (requireBuildableForFine)
        {
            bool hasBuildable = false;
            var hits = Physics2D.OverlapCircleAll((Vector2)worldCenter, fineBuildableRadius);
            foreach (var h in hits)
            {
                if (h.CompareTag("Buildable"))
                {
                    hasBuildable = true;
                    break;
                }
            }

            if (!hasBuildable)
            {
                // Buildable�������Ă��A�������́u�����O���b�h�v�Ȃ�OK�ɂ���
                var hexFine = Object.FindFirstObjectByType<HexPerTileFineGrid>();
                if (hexFine == null || !hexFine.HasFineAtWorld(worldCenter))
                {
                    return false;
                }
            }
        }

        // 3) �����̐�L�`�F�b�N�i�����͂��̂܂܁j
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

    void ReserveFineCells(Vector2Int fcell, BuildingDef def, GameObject go)
    {
        int w = Mathf.Max(1, def.cellsWidth);
        int h = Mathf.Max(1, def.cellsHeight);

        for (int iy = 0; iy < h; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                if (def.shape != null &&
                    ix < def.shape.GetLength(0) &&
                    iy < def.shape.GetLength(1) &&
                    !def.shape[ix, iy])
                    continue;

                int cx = fcell.x + ix - Mathf.FloorToInt(w / 2f);
                int cy = fcell.y + iy - Mathf.FloorToInt(h / 2f);

                var key = new Vector2Int(cx, cy);
                _placedFine[key] = go;
            }
        }
    }

    void UpdatePreviewAndPointerFine(Vector2Int fcell, Vector3 worldCenter)
    {
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

    // ========================= FlowField =========================
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

    // ========================= ���ʃ��[�e�B���e�B =========================
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

        DisableColliders(_spawnedPreviewGO.transform);
        SetSpriteColor(_spawnedPreviewGO.transform, new Color(1f, 1f, 1f, previewAlpha));
    }

    void DisableColliders(Transform root)
    {
        foreach (var c in root.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
        foreach (var c in root.GetComponentsInChildren<Collider>(true)) c.enabled = false;
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

        ClearDragGhosts();
    }

    void CreateDragGhost(Vector3 pos, bool canPlace)
    {
        if (_current?.prefab == null) return;

        var go = Instantiate(_current.prefab, pos, Quaternion.identity);
        if (prefabPreview) go.transform.SetParent(prefabPreview, true);

        DisableColliders(go.transform);

        var col = canPlace ? new Color(1f, 1f, 1f, previewAlpha)
                           : new Color(1f, 0.4f, 0.4f, previewAlpha);
        SetSpriteColor(go.transform, col);

        _dragGhosts.Add(go);
    }

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

    bool FootprintOverlaps(Vector2Int aCell, BuildingDef aDef,
                           Vector2Int bCell, BuildingDef bDef)
    {
        int aw = Mathf.Max(1, aDef.cellsWidth);
        int ah = Mathf.Max(1, aDef.cellsHeight);
        int bw = Mathf.Max(1, bDef.cellsWidth);
        int bh = Mathf.Max(1, bDef.cellsHeight);

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

    Vector3 GetEvenSizeOffsetForFine(BuildingDef def)
    {
        if (def == null) return Vector3.zero;

        float cs = fineCellSize;
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