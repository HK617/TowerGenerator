﻿using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Tilemaps;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Unity.Cinemachine;

public class BuildPlacement : MonoBehaviour
{
    // プレビュー中など「建築を一時的に禁止」するためのフラグ
    public static bool s_buildLocked = false;

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
    public Transform pointerOK;   // SelectTile
    public Transform pointerNG;   // SelectFalse

    [Header("=== Fallback delete (optional) ===")]
    public LayerMask placeableLayers = ~0;
    public float detectRadius = 0.35f;

    [Header("=== Zoom auto switch ===")]
    public CinemachineCamera vcam;
    public float fineGridThreshold = 3f;

    [Header("=== Drone build ===")]
    [Tooltip("trueなら、建物はすぐには建たず、ドローンが来てから建つ")]
    public bool useDroneBuild = true;
    public DroneBuildManager droneManager;

    [Header("=== Base auto place ===")]
    [Tooltip("最初の1回だけこれを建てる。ユーザーが別の建物を選んでいてもBaseになる")]
    public BuildingDef baseDef;

    [Tooltip("Baseと同時に下に敷く六角タイルの BuildingDef")]
    public BuildingDef baseHexDef;

    // Base が実際に「完成」したかどうか（ドローンで建て終わった時点で true）
    public static bool s_baseBuilt = false;

    [Header("=== Machine auto connect ===")]
    [Tooltip("Machine レイヤー（コンベアー / ドリルなど）が乗っているレイヤー")]
    public LayerMask machineLayerMask;

    [Tooltip("上下左右にどれだけ離れた位置を隣マスとみなすか（Fine グリッドなら 0.25）")]
    public float machineCellOffset = 0.25f;

    [Tooltip("隣マスチェック用の半径（小さい円でピンポイントに拾う）")]
    public float machineProbeRadius = 0.12f;

    // ★ 追加: ゴースト状態の Machine 用レイヤー名
    [Header("=== Ghost Machine Layer ===")]
    [Tooltip("ゴースト状態のMachine系ビルドに使うLayer名（例: GhostMachine）")]
    public string ghostMachineLayerName = "GhostMachine";

    int _ghostMachineLayer = -2; // -2 = 未初期化, -1 = 見つからなかった

    [Header("=== Rotation ===")]
    [Tooltip("Rキーを押したときに回転する角度（度）")]
    public float rotationStepDeg = 90f;
    float _currentRotationDeg = 0f;

    [Header("=== Demolition Icon ===")]
    public Sprite hexDemolitionIconSprite;    // 六角タイル / Big グリッド用
    public Sprite fineDemolitionIconSprite;   // 細かい四角グリッド用

    [Tooltip("六角タイル用アイコンの基準スケール")]
    public float hexDemolitionIconScale = 1f;

    [Tooltip("四角グリッド用アイコンの基準スケール")]
    public float fineDemolitionIconScale = 1f;

    [Tooltip("六角タイル用アイコンの高さオフセット")]
    public float hexDemolitionIconYOffset = 0.6f;

    [Tooltip("四角グリッド用アイコンの高さオフセット")]
    public float fineDemolitionIconYOffset = 0.6f;


    // 連続設置中かどうか
    bool _isDragging = false;
    readonly List<(Vector2Int cell, Vector3 center)> _previewCells = new();
    readonly List<GameObject> _dragGhosts = new();

    // ★ 右クリックドラッグ削除用（Fineは「セル」ではなく「建物単位」で管理）
    bool _isRightDraggingFine = false;
    readonly HashSet<GameObject> _rightDragDeletedFine = new();

    bool _isRightDraggingBig = false;
    readonly HashSet<Vector3Int> _rightDragDeletedBig = new();


    BuildingDef _current;
    GameObject _spawnedPreviewGO;
    Vector3Int _lastBigCell = new(int.MinValue, int.MinValue, 0);
    Vector2Int _lastFineCell = new(int.MinValue, int.MinValue);
    readonly Dictionary<Vector3Int, GameObject> _placedByCell = new();
    readonly Dictionary<Vector2Int, GameObject> _placedFine = new();
    readonly HashSet<Vector3Int> _protectedCells = new();

    // ★ ドローン建築用：スペースキーでまとめて着工するまで待つ「計画中ゴースト」のリスト
    class PlannedGhost
    {
        public BuildingDef def;
        public bool isFine;
        public Vector3Int bigCell;
        public Vector2Int fineCell;
        public Vector3 worldPos;
        public GameObject ghostGO;
    }
    readonly List<PlannedGhost> _plannedGhosts = new();

    // ★ この GameObject が「計画中ゴースト」かどうか
    bool IsPlannedGhostGO(GameObject go)
    {
        if (go == null) return false;
        for (int i = 0; i < _plannedGhosts.Count; i++)
        {
            if (_plannedGhosts[i].ghostGO == go)
                return true;
        }
        return false;
    }

    class PlannedDemolition
    {
        public bool isFine;
        public Vector3Int bigCell;
        public Vector2Int fineCell;
        public GameObject target;      // 実物（既に建っているプレハブ）
    }
    readonly List<PlannedDemolition> _plannedDemolitions = new();

    /// <summary>
    /// SelectionBoxDrawer の「キャンセル」ボタンから呼ばれる想定。
    /// すべての削除予約を取り消し、アイコンも消す。
    /// </summary>
    public void ClearAllPlannedDemolitions()
    {
        if (_plannedDemolitions.Count == 0) return;

        // 今たまっている削除予約すべてに対してアイコンを消す
        for (int i = 0; i < _plannedDemolitions.Count; i++)
        {
            var d = _plannedDemolitions[i];
            if (d.target != null)
            {
                RemoveDemolitionIcon(d.target);
            }
        }

        // リスト自体も空にする
        _plannedDemolitions.Clear();
    }

    // ===== セーブ用構造体 =====
    public struct SavedBuilding
    {
        public string defName;
        public Vector3 position;
        public bool isFine;
        public bool isBase;
    }

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
        // 選択を変えたら回転もリセットしておく
        _currentRotationDeg = 0f;
    }

    bool IsRotatable(BuildingDef def)
    {
        if (def == null) return false;
        if (def.allowRotation) return true;
        if (def.prefab == null) return false;
        return def.prefab.GetComponentInChildren<ConveyorBelt>(true) != null;
    }

    bool IsCurrentRotatable()
    {
        return IsRotatable(_current);
    }

    void Update()
    {
        // ★ プレビュー中は建築しない
        if (s_buildLocked)
        {
            UpdatePointerActive(false, false);
            ClearPreview();
            return;
        }
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            UpdatePointerActive(false, false);
            return;
        }

        AutoUpdateUseFineGridByZoom();

#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null || keyboard == null || Camera.main == null) return;

        // ★ Rキーで回転
        if (keyboard.rKey.wasPressedThisFrame && IsCurrentRotatable())
        {
            _currentRotationDeg -= rotationStepDeg;
            if (_currentRotationDeg <= -360f) _currentRotationDeg += 360f;
            if (_currentRotationDeg >= 360f)  _currentRotationDeg -= 360f;
        }

        // ★ Spaceキーで「その時点で存在する計画中ゴースト」を一括でドローンキューへ
        if (keyboard.spaceKey.wasPressedThisFrame)
        {
            StartPlannedBuilds();
            StartPlannedDemolitions(); 
        }

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

                if (IsCurrentRotatable())
                    _spawnedPreviewGO.transform.rotation = Quaternion.Euler(0f, 0f, _currentRotationDeg);
                else
                    _spawnedPreviewGO.transform.rotation = Quaternion.identity;

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
                foreach (var p in _previewCells)
                {
                    if (CanPlaceAtFine(p.cell, p.center))
                        PlaceAtFine(p.cell, p.center);
                }
                _previewCells.Clear();
                ClearDragGhosts();
                _isDragging = false;
            }

            // 右クリック長押しで削除（ドラッグ中ならプレビューキャンセル）
            if (mouse.rightButton.wasPressedThisFrame)
            {
                if (_isDragging || _previewCells.Count > 0)
                {
                    // 左ドラッグ中なら「設置プレビューキャンセル」のみ
                    _previewCells.Clear();
                    ClearDragGhosts();
                    _isDragging = false;

                    _isRightDraggingFine = false;
                    _rightDragDeletedFine.Clear();
                }
                else
                {
                    // 削除ドラッグ開始
                    _isRightDraggingFine = true;
                    _rightDragDeletedFine.Clear();
                }
            }

            if (_isRightDraggingFine && mouse.rightButton.isPressed)
            {
                // そのセルに建物があるか確認
                if (_placedFine.TryGetValue(fineCell, out var go) && go != null)
                {
                    // ★ このドラッグ中に「まだこの建物を処理していなければ」だけ実行
                    if (!_rightDragDeletedFine.Contains(go))
                    {
                        DeleteAtFine(fineCell, finalCenter);
                        _rightDragDeletedFine.Add(go);
                    }
                }
            }


            if (_isRightDraggingFine && mouse.rightButton.wasReleasedThisFrame)
            {
                _isRightDraggingFine = false;
                _rightDragDeletedFine.Clear();
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

                // 右クリック長押しで削除
                if (mouse.rightButton.wasPressedThisFrame)
                {
                    _isRightDraggingBig = true;
                    _rightDragDeletedBig.Clear();
                }

                if (_isRightDraggingBig && mouse.rightButton.isPressed)
                {
                    if (!_rightDragDeletedBig.Contains(cell))
                    {
                        DeleteAtBig(cell);
                        _rightDragDeletedBig.Add(cell);
                    }
                }

                if (_isRightDraggingBig && mouse.rightButton.wasReleasedThisFrame)
                {
                    _isRightDraggingBig = false;
                    _rightDragDeletedBig.Clear();
                }
            }
        }
#endif
    }

    /// <summary>
    /// Machine レイヤーのブロックが置かれた/完成したとき、
    /// そのブロックの「前方」と「真後ろ」にある Machine ブロックへ
    /// 接続再計算を依頼する。
    /// （互いの「前方同士」が常に最新になるイメージ）
    /// </summary>
    void NotifyMachineNeighbors(GameObject centerGo)
    {
        if (centerGo == null) return;

        // Machine レイヤーが未設定なら "Machine" を探す
        if (machineLayerMask.value == 0)
        {
            int idx = LayerMask.NameToLayer("Machine");
            if (idx >= 0)
                machineLayerMask = 1 << idx;
        }

        // ★ Machine と GhostMachine をまとめたマスクを作る
        LayerMask mask = machineLayerMask;
        int ghostLayer = GetGhostMachineLayer();
        if (ghostLayer >= 0)
        {
            mask |= 1 << ghostLayer;
        }

        Vector2 center = centerGo.transform.position;

        // centerGo の「前方」を transform.up として扱う
        Vector2 forward = centerGo.transform.up;

        Vector2[] worldDirs = { Vector2.up, Vector2.right, Vector2.down, Vector2.left };
        Vector2 bestDir = Vector2.up;
        float bestDot = -1f;
        foreach (var d in worldDirs)
        {
            float dot = Vector2.Dot(forward.normalized, d);
            if (dot > bestDot)
            {
                bestDot = dot;
                bestDir = d;
            }
        }

        Vector2[] checkDirs = { bestDir, -bestDir };

        foreach (var dir in checkDirs)
        {
            Vector2 checkPos = center + dir * machineCellOffset;

            // ★ ここを machineLayerMask → mask に変更
            Collider2D[] hits = Physics2D.OverlapCircleAll(checkPos, machineProbeRadius, mask);
            if (hits == null || hits.Length == 0) continue;

            foreach (var h in hits)
            {
                if (!h) continue;
                var root = h.GetComponentInParent<Transform>();
                if (root == null) continue;

                var belt = root.GetComponent<ConveyorBelt>();
                if (belt != null)
                {
                    var connector = root.GetComponent<ConveyorBeltAutoConnector>();
                    if (connector != null)
                    {
                        connector.RecalculatePattern(false);
                    }
                }

                var drill = root.GetComponent<DrillBehaviour>();
                if (drill != null)
                {
                    // 今は特に処理なし
                }
            }
        }
    }

    int GetGhostMachineLayer()
    {
        if (_ghostMachineLayer != -2)
            return _ghostMachineLayer;

        if (string.IsNullOrEmpty(ghostMachineLayerName))
        {
            _ghostMachineLayer = -1;
            return _ghostMachineLayer;
        }

        int layer = LayerMask.NameToLayer(ghostMachineLayerName);
        if (layer < 0)
        {
            Debug.LogWarning($"BuildPlacement: GhostMachine layer '{ghostMachineLayerName}' が見つかりませんでした。");
        }

        _ghostMachineLayer = layer;
        return _ghostMachineLayer;
    }

    void SetLayerRecursively(Transform root, int layer)
    {
        if (!root) return;
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
        {
            SetLayerRecursively(root.GetChild(i), layer);
        }
    }

    // ゴーストを GhostMachine レイヤーにする（レイヤーが見つからなければ従来通りコライダー無効）
    void SetupGhostMachineLayer(GameObject ghost)
    {
        if (!ghost) return;

        int ghostLayer = GetGhostMachineLayer();
        if (ghostLayer >= 0)
        {
            // レイヤーを GhostMachine に変更し、コライダーを有効化する
            SetLayerRecursively(ghost.transform, ghostLayer);
            foreach (var c in ghost.GetComponentsInChildren<Collider2D>(true)) c.enabled = true;
            foreach (var c in ghost.GetComponentsInChildren<Collider>(true)) c.enabled = true;
        }
        else
        {
            // GhostMachine レイヤーが設定されていない場合は、従来通りコライダー無効で運用
            DisableColliders(ghost.transform);
        }
    }

    // 完成時に Machine 側のレイヤーに戻す
    void RestoreMachineLayer(GameObject go, BuildingDef def)
    {
        if (!go) return;

        int targetLayer = go.layer;
        if (def != null && def.prefab != null)
        {
            targetLayer = def.prefab.layer;
        }

        SetLayerRecursively(go.transform, targetLayer);
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

    // ========================= ズームアウト(六角)系 =========================
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
        if (_current?.prefab == null && (baseDef == null || s_baseBuilt))
            return;
        if (!CanPlaceAtBig(cell)) return;

        // まだBaseが建っていないなら、ユーザーが何を選んでいても baseDef を建てる
        BuildingDef defToPlace = _current;
        bool isFirstBase = false;
        if (!s_baseBuilt && baseDef != null)
        {
            defToPlace = baseDef;
            isFirstBase = true;
        }

        if (defToPlace == null) return;

        Vector3 pos = grid.GetCellCenterWorld(cell) + hoverOffset;
        Quaternion rot = IsRotatable(defToPlace)
            ? Quaternion.Euler(0f, 0f, _currentRotationDeg)
            : Quaternion.identity;


        if (useDroneBuild && droneManager != null)
        {
            GameObject ghost = Instantiate(defToPlace.prefab, pos, rot, prefabParent);

            // ★ 追加：コンベアなら「ゴーストモード」にしてロジック停止
            var belt = ghost.GetComponent<ConveyorBelt>();
            if (belt != null)
                belt.SetGhostMode(true);

            // ★ ゴーストを GhostMachine レイヤーにして、Collider は有効のまま
            SetupGhostMachineLayer(ghost);
            SetSpriteColor(ghost.transform, new Color(1f, 1f, 1f, previewAlpha));

            _placedByCell[cell] = ghost;
            if (isFirstBase)
                _protectedCells.Add(cell);

            // ★ ゴースト設置時点で周囲のMachineに通知して、コンベアーのスプライト更新
            NotifyMachineNeighbors(ghost);

            // ★ ここではまだドローンを動かさず、「計画中ゴースト」として登録だけ
            AddPlannedGhostForBig(defToPlace, cell, pos, ghost);
        }

        else
        {
            var go = Instantiate(defToPlace.prefab, pos, rot, prefabParent);
            _placedByCell[cell] = go;

            if (isFirstBase)
                _protectedCells.Add(cell);

            if (isFirstBase)
                s_baseBuilt = true;

            var ui = Object.FindFirstObjectByType<StartMenuUI>();
            if (isFirstBase && ui != null)
            {
                ui.TrySpawnBaseAt(pos);
            }

            if (flowField != null)
                RegisterBuildingToFlowField(defToPlace, pos, true);
        }
    }

    public void FinalizeBigDemolish(Vector3Int cell, GameObject target)
    {
        // アイコンを消す（念のため）
        RemoveDemolitionIcon(target);

        // グリッド辞書から消す
        _placedByCell.Remove(cell);

        // FlowField / Conveyor への通知
        NotifyMachineNeighbors(target);              // つながり再計算
        if (flowField != null)
            RegisterBuildingToFlowField(null, target.transform.position, false);

        Destroy(target);
    }

    // ドローンが完了したときに呼ぶ、六角(ビッグ)セル用の軽量完成処理
    public void FinalizeBigPlacement(BuildingDef def, Vector3Int cell, Vector3 pos, GameObject ghost)
    {
        if (def == null) return;

        GameObject placedGo = null;

        // Baseが完成したときの処理（ドローン版）
        if (!s_baseBuilt && baseDef != null && def == baseDef)
        {
            // 基本はRestoreBaseAtと同じことをやる
            RestoreBaseAt(def, pos);
            // ただし、今のゴーストを採用するなら上書き
            if (ghost != null)
            {
                SetSpriteColor(ghost.transform, Color.white);
                foreach (var c in ghost.GetComponentsInChildren<Collider2D>(true))
                    c.enabled = true;
                foreach (var c in ghost.GetComponentsInChildren<Collider>(true))
                    c.enabled = true;
                ghost.transform.position = pos;

                // ★ 完成したので Machine 側のレイヤーに戻す
                RestoreMachineLayer(ghost, def);

                _placedByCell[cell] = ghost;
            }
            return;
        }

        if (ghost != null)
        {
            SetSpriteColor(ghost.transform, Color.white);
            foreach (var c in ghost.GetComponentsInChildren<Collider2D>(true))
                c.enabled = true;
            foreach (var c in ghost.GetComponentsInChildren<Collider>(true))
                c.enabled = true;
            ghost.transform.position = pos;

            // ★ 完成したので Machine 側のレイヤーに戻す
            RestoreMachineLayer(ghost, def);

            // ★ 追加：コンベアならゴーストモード解除 → ロジックに復帰
            var belt = ghost.GetComponent<ConveyorBelt>();
            if (belt != null)
                belt.SetGhostMode(false);

            _placedByCell[cell] = ghost;
            placedGo = ghost;
        }
        else
        {
            var go = Instantiate(def.prefab, pos, Quaternion.identity, prefabParent);
            _placedByCell[cell] = go;
            placedGo = go;
        }


        // ★ここで Machine 周囲更新を呼ぶ
        NotifyMachineNeighbors(placedGo);

        if (flowField != null)
            RegisterBuildingToFlowField(def, pos, true);
    }

    // ★ Base専用の復元（ロード時やドローン完了時に共通で使う）
    void RestoreBaseAt(BuildingDef baseDefToUse, Vector3 pos)
    {
        if (baseDefToUse == null) return;

        // 1) Base本体
        var baseGO = Instantiate(baseDefToUse.prefab, pos, Quaternion.identity, prefabParent);

        var cell = grid.WorldToCell(pos - hoverOffset);
        _placedByCell[cell] = baseGO;

        // 2) Baseフラグと保護
        s_baseBuilt = true;
        _protectedCells.Add(cell);

        // ★★★ ここを追加：Base の足元を細かいグリッドでも占有しておく
        //     （Base の BuildingDef の cellsWidth / cellsHeight を使います）
        if (fineCellSize > 0f)
        {
            var fcell = WorldToFineCell(pos, fineCellSize);
            ReserveFineCells(fcell, baseDefToUse, baseGO);
        }

        // 3) 下に六角タイルを敷く
        if (baseHexDef != null && baseHexDef.prefab != null)
        {
            var hexGo = Instantiate(baseHexDef.prefab, pos, Quaternion.identity, prefabParent);
            foreach (var c in hexGo.GetComponentsInChildren<Collider2D>(true))
                c.enabled = false;
            foreach (var c in hexGo.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
        }

        // 4) 細かいグリッドを強制生成
        var hexFine = Object.FindFirstObjectByType<HexPerTileFineGrid>();
        if (hexFine != null)
        {
            hexFine.ForceCreateAtWorld(pos);
        }

        // 5) FlowFieldゴールにする
        if (flowField != null)
        {
            flowField.SetTargetWorld(pos);
        }

        // 6) UIへ
        var ui = Object.FindFirstObjectByType<StartMenuUI>();
        if (ui != null)
        {
            ui.TrySpawnBaseAt(pos);
        }
    }

    void DeleteAtBig(Vector3Int cell)
    {
        if (_protectedCells.Contains(cell))
            return;

        // まず _placedByCell に登録されているオブジェクトを調べる
        if (_placedByCell.TryGetValue(cell, out var go) && go)
        {
            // ★ ゴーストなら即削除
            if (IsPlannedGhostGO(go))
            {
                _placedByCell.Remove(cell);
                RemovePlannedGhostByGO(go);
                Destroy(go);
                return;
            }

            // 完成済みなら解体予約トグル
            ToggleDemolitionForBig(cell, go);
            return;
        }

        // フォールバック：辞書に無いがそこに何かある場合
        Vector3 c = grid.GetCellCenterWorld(cell) + hoverOffset;
        var hits = Physics2D.OverlapCircleAll(c, detectRadius, placeableLayers);
        foreach (var h in hits)
        {
            var hc = grid.WorldToCell(h.transform.position - hoverOffset);
            if (_protectedCells.Contains(hc)) continue;

            if (IsPlannedGhostGO(h.gameObject))
            {
                _placedByCell.Remove(hc);
                RemovePlannedGhostByGO(h.gameObject);
                Destroy(h.gameObject);
            }
            else
            {
                ToggleDemolitionForBig(hc, h.gameObject);
            }
            return;
        }
    }

    // ★ Big用：解体予約のオン/オフ
    void ToggleDemolitionForBig(Vector3Int cell, GameObject go)
    {
        // すでに解体予約されているか探す
        int idx = -1;
        for (int i = 0; i < _plannedDemolitions.Count; i++)
        {
            var d = _plannedDemolitions[i];
            if (!d.isFine && d.target == go)
            {
                idx = i;
                break;
            }
        }

        if (idx >= 0)
        {
            // すでに予約済み → 予約キャンセル
            _plannedDemolitions.RemoveAt(idx);
            RemoveDemolitionIcon(go);
        }
        else
        {
            // まだ予約されていない → 予約追加
            var d = new PlannedDemolition
            {
                isFine = false,
                bigCell = cell,
                fineCell = default,
                target = go
            };
            _plannedDemolitions.Add(d);

            // ★ 六角タイル用アイコン
            AddDemolitionIcon(go, isFineGrid: false);
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
            if (IsCurrentRotatable())
                _spawnedPreviewGO.transform.rotation = Quaternion.Euler(0f, 0f, _currentRotationDeg);
            else
                _spawnedPreviewGO.transform.rotation = Quaternion.identity;

            var col = can ? new Color(1f, 1f, 1f, previewAlpha)
                          : new Color(1f, 0.4f, 0.4f, previewAlpha);
            SetSpriteColor(_spawnedPreviewGO.transform, col);
        }
    }

    // ========================= 細かいグリッド系 =========================
    void PlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_current?.prefab == null) return;

        Vector3 finalCenter = worldCenter;
        if (!CanPlaceAtFine(fcell, finalCenter)) return;

        Quaternion rot = IsCurrentRotatable()
            ? Quaternion.Euler(0f, 0f, _currentRotationDeg)
            : Quaternion.identity;

        GameObject ghost = Instantiate(_current.prefab, finalCenter, rot, prefabParent);

        // ★ 追加：コンベアなら「ゴーストモード」にしてロジック停止
        var belt = ghost.GetComponent<ConveyorBelt>();
        if (belt != null)
            belt.SetGhostMode(true);

        DisableColliders(ghost.transform);
        SetSpriteColor(ghost.transform, new Color(1f, 1f, 1f, previewAlpha));

        ReserveFineCells(fcell, _current, ghost);

        if (useDroneBuild && droneManager != null)
        {
            // ★ ゴーストを GhostMachine レイヤーにして、Collider を有効化
            SetupGhostMachineLayer(ghost);

            // ★ ゴースト設置時点で周囲のMachineに通知して、コンベアーのスプライト更新
            NotifyMachineNeighbors(ghost);

            // ★ ここでもまだドローンを動かさず、「計画中ゴースト」として登録だけ
            AddPlannedGhostForFine(_current, fcell, finalCenter, ghost);
        }
        else
        {
            FinalizeFinePlacement(_current, fcell, finalCenter, ghost);
        }

    }

    public void FinalizeFineDemolish(Vector2Int fcell, GameObject target)
    {
        // この建物に紐づいているすべてのセルを削除
        var keysToRemove = new List<Vector2Int>();
        foreach (var kv in _placedFine)
            if (kv.Value == target)
                keysToRemove.Add(kv.Key);
        foreach (var k in keysToRemove)
            _placedFine.Remove(k);

        RemoveDemolitionIcon(target);

        NotifyMachineNeighbors(target);
        if (flowField != null)
            RegisterBuildingToFlowField(null, target.transform.position, false);

        Destroy(target);
    }

    public void FinalizeFinePlacement(BuildingDef def, Vector2Int fcell, Vector3 pos, GameObject ghost)
    {
        if (ghost == null) return;

        SetSpriteColor(ghost.transform, Color.white);
        foreach (var c in ghost.GetComponentsInChildren<Collider2D>(true)) c.enabled = true;
        foreach (var c in ghost.GetComponentsInChildren<Collider>(true)) c.enabled = true;
        ghost.transform.position = pos;

        // ★ 完成したので Machine 側のレイヤーに戻す
        RestoreMachineLayer(ghost, def);

        // ★ 追加：コンベアならゴーストモード解除 → ロジックに復帰
        var belt = ghost.GetComponent<ConveyorBelt>();
        if (belt != null)
            belt.SetGhostMode(false);

        var tmp = new List<Vector2Int>();
        foreach (var kv in _placedFine)
            if (kv.Value == ghost)
                tmp.Add(kv.Key);
        foreach (var k in tmp)
            _placedFine[k] = ghost;

        // ★ここで Machine 周囲更新を呼ぶ
        NotifyMachineNeighbors(ghost);

        if (flowField != null && def != null)
            RegisterBuildingToFlowField(def, pos, true);
    }

    // ============================================================
    // ★ ドローン建築：スペースキーでまとめて着工する仕組み
    // ============================================================

    void AddPlannedGhostForBig(BuildingDef def, Vector3Int cell, Vector3 pos, GameObject ghost)
    {
        if (def == null || ghost == null) return;

        _plannedGhosts.Add(new PlannedGhost
        {
            def = def,
            isFine = false,
            bigCell = cell,
            fineCell = default,
            worldPos = pos,
            ghostGO = ghost,
        });
    }

    void AddPlannedGhostForFine(BuildingDef def, Vector2Int fcell, Vector3 pos, GameObject ghost)
    {
        if (def == null || ghost == null) return;

        _plannedGhosts.Add(new PlannedGhost
        {
            def = def,
            isFine = true,
            bigCell = default,
            fineCell = fcell,
            worldPos = pos,
            ghostGO = ghost,
        });
    }

    void RemovePlannedGhostByGO(GameObject go)
    {
        if (go == null || _plannedGhosts.Count == 0) return;

        for (int i = _plannedGhosts.Count - 1; i >= 0; i--)
        {
            if (_plannedGhosts[i].ghostGO == go)
            {
                _plannedGhosts.RemoveAt(i);
            }
        }
    }

    void StartPlannedBuilds()
    {
        // プレビュー中などは無効
        if (s_buildLocked) return;
        if (!useDroneBuild || droneManager == null) return;
        if (_plannedGhosts.Count == 0) return;

        // Space が押された瞬間に存在していた「計画中ゴースト」のスナップショットを取る
        var snapshot = new List<PlannedGhost>(_plannedGhosts);
        _plannedGhosts.Clear();

        foreach (var g in snapshot)
        {
            if (g.def == null || g.ghostGO == null) continue;

            if (g.isFine)
            {
                droneManager.EnqueueFineBuild(this, g.def, g.fineCell, g.worldPos, g.ghostGO);
            }
            else
            {
                droneManager.EnqueueBigBuild(this, g.def, g.bigCell, g.worldPos, g.ghostGO);
            }
        }
    }

    public void StartPlannedDemolitions()
    {
        if (s_buildLocked) return;
        if (!useDroneBuild || droneManager == null) return;
        if (_plannedDemolitions.Count == 0) return;

        var snapshot = new List<PlannedDemolition>(_plannedDemolitions);
        _plannedDemolitions.Clear();

        foreach (var d in snapshot)
        {
            if (d.target == null) continue;

            if (d.isFine)
            {
                droneManager.EnqueueFineDemolish(this, d.fineCell, d.target);
            }
            else
            {
                droneManager.EnqueueBigDemolish(this, d.bigCell, d.target);
            }
        }
    }

    // ============================================================
    // ★ 解体アイコンの追加・削除
    // ============================================================
    void RemoveDemolitionIcon(GameObject target)
    {
        if (target == null) return;

        // 新方式：ルートごと消す
        var root = target.transform.Find("DemolitionIconRoot");
        if (root != null)
        {
            Destroy(root.gameObject);
            return;
        }

        // 互換性のため、昔の単体アイコンも消しておく
        var icon = target.transform.Find("DemolitionIcon");
        if (icon != null)
            Destroy(icon.gameObject);
    }

    // isFineGrid = true なら 四角グリッド用アイコン / false なら 六角タイル用
    void AddDemolitionIcon(GameObject target, bool isFineGrid)
    {
        if (target == null) return;

        // いったん既存アイコンを消してリセット
        var oldRoot = target.transform.Find("DemolitionIconRoot");
        if (oldRoot != null)
            Destroy(oldRoot.gameObject);
        var oldSingle = target.transform.Find("DemolitionIcon");
        if (oldSingle != null)
            Destroy(oldSingle.gameObject);

        GameObject root = new GameObject("DemolitionIconRoot");
        root.transform.SetParent(target.transform, false);
        root.transform.localPosition = Vector3.zero;

        // 親スケールの逆数（建物の大きさに左右されないようにする）
        var parentScale = target.transform.lossyScale;
        float invX = (parentScale.x != 0f) ? 1f / parentScale.x : 1f;
        float invY = (parentScale.y != 0f) ? 1f / parentScale.y : 1f;

        if (!isFineGrid)
        {
            // ★ 六角タイル / Big グリッド用：建物の中心に1個だけ
            float yOffset = hexDemolitionIconYOffset;

            var icon = new GameObject("DemolitionIcon");
            icon.transform.SetParent(root.transform, false);
            icon.transform.localPosition = new Vector3(0, yOffset, 0);

            var sr = icon.AddComponent<SpriteRenderer>();
            sr.sprite = hexDemolitionIconSprite;
            sr.sortingOrder = 999;

            icon.transform.localScale = new Vector3(invX, invY, 1f) * hexDemolitionIconScale;
        }
        else
        {
            // ★ 四角グリッド用：占有しているすべての Fine セルにアイコンを置く
            var usedCells = new HashSet<Vector2Int>();
            foreach (var kv in _placedFine)
            {
                if (kv.Value == target)
                    usedCells.Add(kv.Key);
            }

            foreach (var cell in usedCells)
            {
                // セルの中心のワールド座標
                Vector3 cellWorld = FineCellToWorld(cell, fineCellSize);
                cellWorld.y += fineDemolitionIconYOffset;

                // 建物ローカル座標に変換
                Vector3 localPos = target.transform.InverseTransformPoint(cellWorld);

                var icon = new GameObject("DemolitionIcon");
                icon.transform.SetParent(root.transform, false);
                icon.transform.localPosition = localPos;

                var sr = icon.AddComponent<SpriteRenderer>();
                sr.sprite = fineDemolitionIconSprite;
                sr.sortingOrder = 999;

                icon.transform.localScale = new Vector3(invX, invY, 1f) * fineDemolitionIconScale;
            }
        }
    }


    void DeleteAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        Vector3 finalCenter = worldCenter;

        if (!_placedFine.TryGetValue(fcell, out var go) || go == null)
            return;

        // ★ Base は細かいグリッドからも削除不可にする
        if (baseDef != null &&
            go.name.Replace("(Clone)", "").Trim() == baseDef.prefab.name)
        {
            return;
        }

        // ★ ゴーストなら即削除（占有している全てのセルも解放）
        if (IsPlannedGhostGO(go))
        {
            var keysToRemove = new List<Vector2Int>();
            foreach (var kv in _placedFine)
                if (kv.Value == go)
                    keysToRemove.Add(kv.Key);
            foreach (var k in keysToRemove)
                _placedFine.Remove(k);

            RemovePlannedGhostByGO(go);
            Destroy(go);
            // ゴーストは FlowField にまだ登録していないので何もしない
            return;
        }

        // 完成済みなら解体予約トグル
        ToggleDemolitionForFine(fcell, go);
    }

    // ★ Fine用：解体予約のオン/オフ
    void ToggleDemolitionForFine(Vector2Int fcell, GameObject go)
    {
        int idx = -1;
        for (int i = 0; i < _plannedDemolitions.Count; i++)
        {
            var d = _plannedDemolitions[i];
            if (d.isFine && d.target == go)
            {
                idx = i;
                break;
            }
        }

        if (idx >= 0)
        {
            // すでに予約済み → 予約キャンセル
            _plannedDemolitions.RemoveAt(idx);
            RemoveDemolitionIcon(go);
        }
        else
        {
            // まだ予約されていない → 予約追加
            var d = new PlannedDemolition
            {
                isFine = true,
                bigCell = default,
                fineCell = fcell,
                target = go
            };
            _plannedDemolitions.Add(d);

            // ★ 四角グリッド用アイコン
            AddDemolitionIcon(go, isFineGrid: true);
        }
    }

    // ============================================================
    // ★ 外部UI（SelectionBoxDrawer）からの一括解体予約
    // ============================================================
    public void EnsureDemolitionPlannedForObject(GameObject target)
    {
        if (target == null) return;

        // すでに計画済みなら何もしない
        for (int i = 0; i < _plannedDemolitions.Count; i++)
        {
            if (_plannedDemolitions[i].target == target)
                return;
        }

        // まず Fine グリッド側（四角グリッド）のセルを探す
        foreach (var kv in _placedFine)   // Dictionary<Vector2Int, GameObject>
        {
            if (kv.Value == target)
            {
                var d = new PlannedDemolition
                {
                    isFine = true,
                    bigCell = default,
                    fineCell = kv.Key,
                    target = target
                };
                _plannedDemolitions.Add(d);

                // 四角グリッド用アイコン
                AddDemolitionIcon(target, isFineGrid: true);
                return;
            }
        }

        // 次に Big グリッド側（六角タイル）のセルを探す
        foreach (var kv in _placedByCell) // Dictionary<Vector3Int, GameObject>
        {
            if (kv.Value == target)
            {
                var d = new PlannedDemolition
                {
                    isFine = false,
                    bigCell = kv.Key,
                    fineCell = default,
                    target = target
                };
                _plannedDemolitions.Add(d);

                // 六角タイル用アイコン
                AddDemolitionIcon(target, isFineGrid: false);
                return;
            }
        }

        // どの辞書にも無ければ何もしない（例：ゴーストなど）
    }

    bool CanPlaceAtFine(Vector2Int fcell, Vector3 worldCenter)
    {
        if (_current == null) return false;

        bool onValidHex = true;

        if (hexTilemap != null)
        {
            var hcell = hexTilemap.WorldToCell(worldCenter);
            onValidHex = hexTilemap.HasTile(hcell);

            if (!onValidHex)
            {
                var hexFine = Object.FindFirstObjectByType<HexPerTileFineGrid>();
                if (hexFine != null && hexFine.HasFineAtWorld(worldCenter))
                {
                    onValidHex = true;
                }
            }

            if (!onValidHex)
                return false;
        }

        // 下に資源が無いと置けない建物の判定（Drillタグの有無で判定）
        bool requiresResource = false;
        if (_current.prefab != null)
        {
            // prefab自体、または子オブジェクトに "Drill" タグがあるか？
            if (_current.prefab.CompareTag("Drill"))
                requiresResource = true;
            else
            {
                foreach (var t in _current.prefab.GetComponentsInChildren<Transform>(true))
                {
                    if (t.CompareTag("Drill"))
                    {
                        requiresResource = true;
                        break;
                    }
                }
            }
        }

        if (requiresResource)
        {
            bool hasResourceBelow = false;

            // ★ ResourceBlock の「Fineセル」が同じものだけ許可する
            // fcell がドリルを置こうとしている Fine セル
            float r = fineCellSize * 0.7f; // ちょっと余裕のある半径
            var hits = Physics2D.OverlapCircleAll((Vector2)worldCenter, r);
            foreach (var h in hits)
            {
                if (!h.CompareTag("ResourceBlock")) continue;

                // この ResourceBlock が属する Fineセルを求める
                Vector2Int resCell = WorldToFineCell(h.transform.position, fineCellSize);

                // ドリルのセルと同じなら「真下にある資源」とみなす
                if (resCell == fcell)
                {
                    hasResourceBelow = true;
                    break;
                }
            }

            if (!hasResourceBelow)
                return false; // 同じ Fineセルに ResourceBlock がなければ置けない
        }

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
                var hexFine = Object.FindFirstObjectByType<HexPerTileFineGrid>();
                if (hexFine == null || !hexFine.HasFineAtWorld(worldCenter))
                {
                    return false;
                }
            }
        }

        // ★ Base がある細かいセルの上には何も置けないようにする
        if (s_baseBuilt && baseDef != null)
        {
            float rBase = fineCellSize * 0.6f; // 少し狭めの半径
            var hitsBase = Physics2D.OverlapCircleAll((Vector2)worldCenter, rBase);
            foreach (var h in hitsBase)
            {
                if (!h) continue;

                var root = h.transform.root;
                bool isBase = false;

                // ① Base プレハブに "Base" タグを付けている場合
                if (root.CompareTag("Base"))
                {
                    isBase = true;
                }
                // ② タグが無い場合は、プレハブ名で判定する
                else if (baseDef.prefab != null)
                {
                    string rootName = root.name;
                    if (rootName.EndsWith("(Clone)"))
                        rootName = rootName.Substring(0, rootName.Length - 7).TrimEnd();

                    if (rootName == baseDef.prefab.name)
                        isBase = true;
                }

                if (isBase)
                    return false;
            }
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

    // ========================= 共通ユーティリティ =========================
    void MovePreview(Vector3 world)
    {
        if (_current?.prefab == null) return;
        EnsurePrefabPreview();
        if (_spawnedPreviewGO)
        {
            _spawnedPreviewGO.transform.position = world;
            _spawnedPreviewGO.transform.rotation = Quaternion.Euler(0f, 0f, _currentRotationDeg);
        }
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

        var go = Instantiate(_current.prefab, pos, Quaternion.Euler(0f, 0f, _currentRotationDeg));
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

                    // ★ Base プレハブなら細かいグリッドも占有しておく
                    if (baseDef != null && child.name.Replace("(Clone)", "").Trim() == baseDef.prefab.name)
                    {
                        var fcell = WorldToFineCell(child.position, fineCellSize);
                        ReserveFineCells(fcell, baseDef, child.gameObject);
                    }

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

    // ===== ここからセーブ/ロードで使う公開API =====

    // いまシーンに置かれている建物を全部列挙する
    public List<SavedBuilding> CollectForSave()
    {
        var list = new List<SavedBuilding>();

        // 六角(大)
        foreach (var kv in _placedByCell)
        {
            var go = kv.Value;
            if (!go) continue;

            // ★ BuildingMarker は使わない。プレハブ名から取る
            string defName = go.name.Replace("(Clone)", "").Trim();

            bool isBase = false;
            // baseDef が設定されていて、プレハブ名がそれと同じなら Base とみなす
            if (baseDef != null && defName == baseDef.prefab.name)
                isBase = true;

            list.Add(new SavedBuilding
            {
                defName = defName,
                position = go.transform.position,
                isFine = false,
                isBase = isBase
            });
        }

        // 細かいグリッド
        // 同じGameObjectが複数のセルに入ってる可能性があるので、一意にする
        var added = new HashSet<GameObject>();
        foreach (var kv in _placedFine)
        {
            var go = kv.Value;
            if (!go) continue;
            if (added.Contains(go)) continue;
            added.Add(go);

            string defName = go.name.Replace("(Clone)", "").Trim();

            list.Add(new SavedBuilding
            {
                defName = defName,
                position = go.transform.position,
                isFine = true,
                isBase = false   // 細かいグリッドのやつはBaseにはしない
            });
        }

        return list;
    }

    // いま置いてある建物を全部消す（ロード時の最初で呼ぶ）
    public void ClearAllPlaced()
    {
        foreach (var kv in _placedByCell)
        {
            if (kv.Value) Destroy(kv.Value);
        }
        foreach (var kv in _placedFine)
        {
            if (kv.Value) Destroy(kv.Value);
        }
        _placedByCell.Clear();
        _placedFine.Clear();
        _protectedCells.Clear();
    }

    // セーブデータから1個ぶん復元する
    public void RestoreBuilding(BuildingDef def, Vector3 pos, bool fine, bool isBase)
    {
        if (isBase)
        {
            RestoreBaseAt(def, pos);
            return;
        }

        if (fine)
        {
            var go = Instantiate(def.prefab, pos, Quaternion.identity, prefabParent);
            var fcell = WorldToFineCell(pos, fineCellSize);
            ReserveFineCells(fcell, def, go);
            SetSpriteColor(go.transform, Color.white);
            foreach (var c in go.GetComponentsInChildren<Collider2D>(true)) c.enabled = true;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = true;
            if (flowField != null)
                RegisterBuildingToFlowField(def, pos, true);
        }
        else
        {
            var go = Instantiate(def.prefab, pos, Quaternion.identity, prefabParent);
            var cell = grid.WorldToCell(pos - hoverOffset);
            _placedByCell[cell] = go;
            if (flowField != null)
                RegisterBuildingToFlowField(def, pos, true);
        }
    }

    // ロード時に「このタスクのゴーストを再生成したい」ためのAPI
    public GameObject CreateGhostForDef(BuildingDef def, Vector3 pos, bool fine)
    {
        if (def == null) return null;
        var go = Instantiate(def.prefab, pos, Quaternion.identity, prefabParent);
        DisableColliders(go.transform);
        SetSpriteColor(go.transform, new Color(1f, 1f, 1f, previewAlpha));

        if (fine)
        {
            var fcell = WorldToFineCell(pos, fineCellSize);
            ReserveFineCells(fcell, def, go);
        }
        else
        {
            var cell = grid.WorldToCell(pos - hoverOffset);
            _placedByCell[cell] = go;
        }

        return go;
    }
}