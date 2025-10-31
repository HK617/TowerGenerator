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

    [Header("=== Fine-cell placement (0.25 �~ 0.25) ===")]
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

    // �z�u�ς݂��L�^
    readonly Dictionary<Vector3Int, GameObject> _placedByCell = new();
    readonly Dictionary<Vector2Int, GameObject> _placedFine = new();
    readonly HashSet<Vector3Int> _protectedCells = new();

    // �_���Z��(0.25�~0.25)�̐�L
    HashSet<Vector2Int> _occupiedLogical = new();

    void Awake()
    {
        if (!grid) grid = GetComponentInParent<Grid>();
        RebuildMapFromParent();

        // �N�����Ƀ|�C���^���B��
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
        // �J�����������Ƃ��͉������Ȃ�
        if (Camera.main == null) return;

        // ���܂̃Y�[���ʂ����i�Y�[���A�E�g���|�C���^�p�j
        float camSize = Camera.main.orthographicSize;

#if ENABLE_INPUT_SYSTEM
    // �}�E�X�ʒu�iInput System�Łj
    var mouse = Mouse.current;
    if (mouse == null) return;
    Vector2 sp = mouse.position.ReadValue();
    Vector3 world = Camera.main.ScreenToWorldPoint(
        new Vector3(sp.x, sp.y, -Camera.main.transform.position.z));
    world.z = 0f;
#else
        // �}�E�X�ʒu�i��Input�Łj
        Vector3 world = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        world.z = 0f;
#endif

        // �@ �Y�[���A�E�g���isize>3�j�͂Ƃ肠�����Z�p�̈ʒu�Ƀ|�C���^�����o��
        //    �� ���z��I��ł��Ȃ��Ă��o�������v���Ȃ̂ŁA_current �̑O�ł��
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

        // �A �������I�΂�Ă��Ȃ��Ȃ炱���ŏI���i��̃|�C���^�����o���j
        if (_current == null) return;

        // �B UI�̏�ł͌��Ă���󂵂��肳���Ȃ�
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

#if ENABLE_INPUT_SYSTEM
    // �C �ׂ����O���b�h���[�h�i0.25�~0.25�j�̂Ƃ�
    if (useFineGrid)
    {
        // �}�E�X�̐��E���W�����̂܂܎g���ăZ�����v�Z
        Vector2Int fcell = WorldToFineCell(world, fineCellSize);
        Vector3 center = FineCellToWorld(fcell, fineCellSize) + hoverOffset;

        // �v���r���[�ƃ|�C���^�̕\���X�V
        UpdatePreviewAndPointerFine(fcell, center);

        // ���N���b�N�Ŕz�u
        if (mouse.leftButton.wasPressedThisFrame)
            PlaceAtFine(fcell, center);

        // �E�N���b�N�ō폜�i������̃|�C���g�B�O���b�h���S�ł͂Ȃ��}�E�X�ʒu��n���j
        if (mouse.rightButton.wasPressedThisFrame)
            DeleteAtFine(fcell, world);     // center ����Ȃ� world ��n��
    }
    else
    {
        // �D �傫���O���b�h�i�Z�p/1�}�X�j���[�h�̂Ƃ�
        Vector3Int cell = grid.WorldToCell(world);

        // �v���r���[��OK/NG�|�C���^���X�V
        UpdatePreviewAndPointerBig(cell);

        // ���N���b�N�Ŕz�u
        if (mouse.leftButton.wasPressedThisFrame)
            PlaceAtBig(cell);

        // �E�N���b�N�ō폜�i���}�E�X�ʒu�ŏ����Łj
        if (mouse.rightButton.wasPressedThisFrame)
            DeleteAtBig(cell, world);
    }
#else
        // ��InputSystem�ł��g���Ȃ炱���ɓ�������������
#endif
    }

    // �ׂ����Z���i0.25�~0.25�j�p�̃v���r���[���|�C���^�X�V
    void UpdatePreviewAndPointerFine(Vector2Int fcell, Vector3 worldCenter)
    {
        // �O��ƈႤ�Z���Ȃ�v���r���[�𓮂���
        if (fcell != _lastFineCell)
        {
            _lastFineCell = fcell;
            MovePreview(worldCenter);
        }

        bool can = CanPlaceAtFine(fcell, worldCenter);

        // �S�[�X�g�̐F��OK/NG�ŕς���
        if (_spawnedPreviewGO)
        {
            var col = can
                ? new Color(1f, 1f, 1f, previewAlpha)
                : new Color(1f, 0.4f, 0.4f, previewAlpha);
            SetSpriteColor(_spawnedPreviewGO.transform, col);
        }

        // �ׂ������[�h�̂Ƃ��� OK/NG �|�C���^�͊�{�����Ă���
        if (pointerOK) pointerOK.gameObject.SetActive(false);
        if (pointerNG) pointerNG.gameObject.SetActive(false);
    }

    // �ׂ����Z���ɍ��̌�����u���邩�ǂ���
    bool CanPlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        // ���łɂ��ׂ̍����Z���ɒu���Ă���Ȃ�NG
        if (_placedFine.ContainsKey(fcell))
            return false;

        // �Z�p�^�C���̏ゾ���ɒu�������Ƃ�
        if (hexTilemap != null)
        {
            var hcell = hexTilemap.WorldToCell(worldCenter);
            if (!hexTilemap.HasTile(hcell))
                return false;
        }

        // Buildable�^�O�̏ゾ���ɒu�������Ƃ�
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

        // FlowField�p�̘_����L�ɂԂ����Ă��Ȃ���
        float cs = flowField ? flowField.cellSize : fineCellSize;
        foreach (var lc in EnumerateLogicalCells(_current, worldCenter, cs))
        {
            if (_occupiedLogical.Contains(lc))
                return false;
        }

        return true;
    }

    // ================== �傫���Z���z�u ==================
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

        // �� ������Def��ۑ����Ă����i�폜���Ɏg���j
        var info = go.AddComponent<PlacedBuildingInfo>();
        info.def = _current;

        // Base����鏈��������ꍇ
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

        // ��L�o�^
        RegisterOccupancyFor(_current, pos, true);

        // FlowField�o�^
        if (flowField != null)
            RegisterBuildingToFlowField(_current, pos, true);
    }

    void DeleteAtBig(Vector3Int cell, Vector3 mouseWorld)
    {
        // mouseWorld �� Update() �̒��Ōv�Z�����u�}�E�X�̃��[���h���W�v��n���z��
        // �������̊֐��V�O�l�`�����ς����Ȃ��Ȃ�A�֐��̒��� Camera.main �������Ă������ł�

        // 1) Base�^�C���͏����Ȃ�
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

        // �@ �^�C���������Ȃ�NG
        if (requireUnderlyingTile && groundTilemap && !groundTilemap.HasTile(cell))
            return false;

        // �A �܂��������o���Ă�Z���Əd�Ȃ��ĂȂ���
        //    �i�����͍��܂ł̃`�F�b�N�j
        // �� ���ꂾ�����Ɓu�V�[���Ɍ����炠��Hex�v�͕�����Ȃ�
        // if (_placedByCell.ContainsKey(cell)) return false; ������͎c���Ă�OK�����ǁA
        // ����� collider �̂ق���D�悷��̂ŃR�����g���Ă����܂�
        // if (_placedByCell.ContainsKey(cell)) return false;

        // �B �����I�ɉ������邩������i���������V�K�j
        Vector3 worldCenter = grid.GetCellCenterWorld(cell) + hoverOffset;
        // ���������߂ł�����
        var hits = Physics2D.OverlapCircleAll(worldCenter, detectRadius, placeableLayers);
        if (hits != null && hits.Length > 0)
        {
            // �����ɂ���̂��u���I��ł�Prefab���̂��́v�ŁA
            // ���u�d�˒u���������v�݂����Ȏd�l�Ȃ炱���ŕ��򂷂�
            return false; // �Ȃ񂩂���̂Œu���Ȃ�
        }

        // �C 0.25 �̘_���O���b�h�����`�F�b�N�i3�~3�̏d�Ȃ�΍�j
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

    // ================== �ׂ����Z���z�u ==================
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
        // worldCenter �́u�}�E�X�̐^���v���Ǝv����OK�Ƃ����O��ŏ�������

        // 1) Base�^�C���͏����Ȃ�
        if (grid != null)
        {
            var big = grid.WorldToCell(worldCenter - hoverOffset);
            if (_protectedCells.Contains(big))
                return;
        }

        // 2) �܂� 2D ���}�E�X�ʒu�ŏE���i���������O���b�h���S����Ȃ�����j
        float r = detectRadius;
        var hits2d = Physics2D.OverlapCircleAll(worldCenter, r, placeableLayers);
        if (hits2d != null && hits2d.Length > 0)
        {
            foreach (var h in hits2d)
            {
                // Base�͏����Ȃ�
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

        // 3) �O�̂��� 3D ���}�E�X�ʒu�ŏE��
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

        // �����܂ŗ�����u�}�E�X�̐^���ɂ͉����Ȃ������v
        // Debug.Log("[DeleteAtFine] nothing at " + worldCenter);
    }

    // ================== FlowField ���f�ishape�D��j ==================
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

    // ================== ��L�̓o�^/���� ==================
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

    // ================== �v���r���[�Ȃ� ==================
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
            // �����́u�N�����ɂ��łɒu���Ă����v���ēo�^�������Ȃ珑��
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
