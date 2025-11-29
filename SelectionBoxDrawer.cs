using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// 新 Input System 対応
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// ワールド空間の範囲選択＋右側固定メニュー。
/// Shift + 左ドラッグで地面上に水色の四角を出し、
/// その範囲に触れている 2D Collider を持つブロックをハイライトする。
///
/// ・ドラッグ開始時に BuildPlacement.s_buildLocked = true で建築モード停止
/// ・BuildBar から建築モードが開始されたら
///   SelectionBoxDrawer.NotifyBuildModeStartedFromOutside() でロック解除＆UI閉じ
/// ・上部カテゴリボタン（Block / Turret …）
///   - 該当タグのブロックだけ色を変える
///   - 選択中ボタンだけ色を変える
/// ・下部の詳細パネル
///   - ✕ボタン：詳細パネルを閉じる＋カテゴリボタン選択解除＋色を普通の選択色に戻す
///   - Deleteボタン：現在のカテゴリの建物に「削除予約アイコン」を付ける
///   - 確定ボタン：BuildPlacement.StartPlannedDemolitions() を呼んでドローン解体開始
///   - キャンセルボタン：すべての削除予約をキャンセル（アイコンも消える）
/// </summary>
public class SelectionBoxDrawer : MonoBehaviour
{
    public static SelectionBoxDrawer Instance { get; private set; }

    [Header("World Selection Rect")]
    public Transform selectionRectRoot;
    public SpriteRenderer selectionRectRenderer;

    [Header("Selection Target")]
    public LayerMask selectableLayers = ~0;

    [Header("Highlight Colors")]
    public Color highlightColor = new Color(1f, 0.9f, 0.4f, 1f);   // 通常の「選択中」色
    public Color defaultColor = Color.white;                        // 元の色に戻すとき用

    [Header("Rect Appearance")]
    public Color rectColor = new Color(0.4f, 0.8f, 1f, 0.25f);

    [Header("Fixed Menu UI")]
    public RectTransform menuRoot;
    public Button blockButton;
    public Button turretButton;
    public Button machineButton;
    public Button resourceButton;
    public Button conveyorButton;

    [Header("Resource Mining Icon")]
    [Tooltip("Resource ブロックに付ける採掘アイコンのPrefab（任意）")]
    public GameObject resourceMiningIconPrefab;
    [Header("Resource Mining Icon")]
    [Tooltip("Resource ブロックに付ける採掘アイコン")]
    public Sprite resourceMiningIconSprite;
    [Tooltip("採掘アイコンのスケール")]
    public float resourceMiningIconScale = 1f;
    [Tooltip("採掘アイコンのYオフセット（ブロック中心からの高さ）")]
    public float resourceMiningIconYOffset = 0.6f;

    [Header("Detail Panel (menu lower half)")]
    [Tooltip("メニューパネルの下半分に置く詳細メニューのルート")]
    public RectTransform detailPanelRoot;
    [Tooltip("詳細メニューのタイトル (選択中カテゴリ名など)")]
    public TMPro.TMP_Text detailTitleText;
    [Tooltip("詳細メニューを閉じるボタン（✕）")]
    public Button detailCloseButton;
    [Tooltip("選択している建物に削除予約を付けるボタン")]
    public Button detailDeleteButton;
    [Tooltip("削除予約の『確定』ボタン（ドローン解体開始）")]
    public Button detailDeleteConfirmButton;
    [Tooltip("削除予約の『キャンセル』ボタン（予約全部クリア）")]
    public Button detailDeleteCancelButton;

    [Header("Tag Groups (multiple tags allowed)")]
    public string[] blockTags;
    public string[] turretTags;
    public string[] machineTags;
    public string[] resourceTags;
    public string[] conveyorTags;

    [Header("Tagged Color")]
    [Tooltip("カテゴリに合致したブロックの色")]
    public Color tagMatchedColor = new Color(0.7f, 1f, 0.7f, 1f);

    [Header("Category Button Visuals")]
    [Tooltip("カテゴリボタンの通常色")]
    public Color categoryButtonNormalColor = Color.white;
    [Tooltip("カテゴリボタンの選択中の色")]
    public Color categoryButtonSelectedColor = new Color(1f, 0.9f, 0.5f, 1f);

    [Header("Build System")]
    [Tooltip("削除予約・解体開始を投げる先の BuildPlacement を指定してください")]
    public BuildPlacement buildPlacement;

    [Header("Settings")]
    public float minDragDistance = 0.2f;

    bool _isDragging;
    Vector2 _startWorldPos;
    Vector2 _currentWorldPos;

    // 直近の範囲選択で拾われたオブジェクト
    readonly List<GameObject> _lastHighlighted = new();

    // 直近で押されたカテゴリボタン
    Button _currentCategoryButton;
    // 全カテゴリボタンの配列（色をまとめて変える用）
    Button[] _categoryButtons;

    // 現在選択中カテゴリに対応するタグ群（Block / Turret など）
    string[] _currentCategoryTags;

    // 詳細メニューの Delete ボタンが「削除モード」か「採掘モード」か
    enum DetailMode
    {
        Demolish,
        Mining
    }
    DetailMode _detailMode = DetailMode.Demolish;

    // Delete/採掘ボタンのラベル
    TMPro.TMP_Text _detailDeleteButtonLabel;

    void Awake()
    {
        // シングルトン
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        if (!selectionRectRoot)
        {
            Debug.LogError("[SelectionBoxDrawer] selectionRectRoot missing!");
            enabled = false;
            return;
        }

        if (!selectionRectRenderer)
            selectionRectRenderer = selectionRectRoot.GetComponentInChildren<SpriteRenderer>();

        if (!selectionRectRenderer)
        {
            Debug.LogError("[SelectionBoxDrawer] SpriteRenderer missing!");
            enabled = false;
            return;
        }

        selectionRectRoot.gameObject.SetActive(false);
        selectionRectRenderer.color = rectColor;

        if (menuRoot)
            menuRoot.gameObject.SetActive(false);

        if (detailPanelRoot)
            detailPanelRoot.gameObject.SetActive(false);

        // Delete 確定 / キャンセルボタンは初期状態では非表示
        if (detailDeleteConfirmButton)
            detailDeleteConfirmButton.gameObject.SetActive(false);
        if (detailDeleteCancelButton)
            detailDeleteCancelButton.gameObject.SetActive(false);

        // カテゴリボタン配列を作成
        var list = new List<Button>();
        if (blockButton) list.Add(blockButton);
        if (turretButton) list.Add(turretButton);
        if (machineButton) list.Add(machineButton);
        if (resourceButton) list.Add(resourceButton);
        if (conveyorButton) list.Add(conveyorButton);
        _categoryButtons = list.ToArray();

        // 初期色を設定（全部通常色）
        InitializeCategoryButtonColors();

        // ボタン登録（カテゴリ＋タグフィルタ＋詳細メニュー）
        if (blockButton)
            blockButton.onClick.AddListener(() => OnCategoryButtonPressed("Block", blockTags, blockButton));
        if (turretButton)
            turretButton.onClick.AddListener(() => OnCategoryButtonPressed("Turret", turretTags, turretButton));
        if (machineButton)
            machineButton.onClick.AddListener(() => OnCategoryButtonPressed("Machine", machineTags, machineButton));
        if (resourceButton)
            resourceButton.onClick.AddListener(() => OnCategoryButtonPressed("Resource", resourceTags, resourceButton));
        if (conveyorButton)
            conveyorButton.onClick.AddListener(() => OnCategoryButtonPressed("Conveyor", conveyorTags, conveyorButton));

        if (detailCloseButton)
            detailCloseButton.onClick.AddListener(CloseDetailPanelOnly);

        if (detailDeleteButton)
            detailDeleteButton.onClick.AddListener(OnDetailDeleteClicked);

        if (detailDeleteConfirmButton)
            detailDeleteConfirmButton.onClick.AddListener(OnDetailDeleteConfirmClicked);

        if (detailDeleteCancelButton)
            detailDeleteCancelButton.onClick.AddListener(OnDetailDeleteCancelClicked);

        if (detailDeleteButton)
            _detailDeleteButtonLabel = detailDeleteButton.GetComponentInChildren<TMPro.TMP_Text>(true);
    }

    void Update()
    {
        if (Camera.main == null) return;
        if (IsPointerOverUI()) return;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null || kb == null) return;

        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
        Vector2 scr = mouse.position.ReadValue();
        Vector2 world2 = ScreenToWorld(scr);

        if (shift && mouse.leftButton.wasPressedThisFrame)
            BeginDrag(world2);

        if (_isDragging && mouse.leftButton.wasReleasedThisFrame)
            EndDrag(world2);

        if (_isDragging)
            Dragging(world2);
#else
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        Vector2 scr = Input.mousePosition;
        Vector2 world2 = ScreenToWorld(scr);

        if (shift && Input.GetMouseButtonDown(0))
            BeginDrag(world2);

        if (_isDragging && Input.GetMouseButtonUp(0))
            EndDrag(world2);

        if (_isDragging)
            Dragging(world2);
#endif
    }

    // ========================================================
    // Drag operations
    // ========================================================

    void BeginDrag(Vector2 pos)
    {
        _isDragging = true;
        _startWorldPos = pos;
        _currentWorldPos = pos;

        if (selectionRectRoot)
        {
            selectionRectRoot.gameObject.SetActive(true);
            UpdateRectTransform(pos, pos);
        }

        ClearHighlight();
        HideMenuOnly(); // メニューと詳細、ボタン選択・Delete確定UIもクリア

        // 選択開始時に建築モードを完全に止める
        BuildPlacement.s_buildLocked = true;
    }

    void EndDrag(Vector2 pos)
    {
        _isDragging = false;
        _currentWorldPos = pos;

        if (selectionRectRoot)
            selectionRectRoot.gameObject.SetActive(false);

        float dist = Vector2.Distance(_startWorldPos, _currentWorldPos);
        if (dist < minDragDistance)
        {
            // ほぼ動いていない → 何もしない（ロックは維持）
            ClearHighlight();
            HideMenuOnly();
            return;
        }

        UpdateHighlight(_startWorldPos, _currentWorldPos);
        ShowMenu(); // メニュー表示（ロックは維持）
    }

    void Dragging(Vector2 pos)
    {
        _currentWorldPos = pos;
        UpdateRectTransform(_startWorldPos, pos);
        UpdateHighlight(_startWorldPos, pos);
    }

    // ========================================================
    // UI / Rect
    // ========================================================

    Vector2 ScreenToWorld(Vector2 screen)
    {
        var cam = Camera.main;
        float z = -cam.transform.position.z;
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, z));
        return new Vector2(w.x, w.y);
    }

    void UpdateRectTransform(Vector2 a, Vector2 b)
    {
        float minX = Mathf.Min(a.x, b.x);
        float minY = Mathf.Min(a.y, b.y);
        float maxX = Mathf.Max(a.x, b.x);
        float maxY = Mathf.Max(a.y, b.y);

        float w = maxX - minX;
        float h = maxY - minY;

        if (!selectionRectRoot) return;

        selectionRectRoot.position = new Vector3(minX + w * 0.5f, minY + h * 0.5f, 0f);
        selectionRectRoot.localScale = new Vector3(Mathf.Max(w, 0.0001f), Mathf.Max(h, 0.0001f), 1f);
        selectionRectRenderer.color = rectColor;
    }

    // ========================================================
    // Highlight
    // ========================================================

    void UpdateHighlight(Vector2 a, Vector2 b)
    {
        ClearHighlight();

        Vector2 min = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
        Vector2 max = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        var hits = Physics2D.OverlapAreaAll(min, max, selectableLayers);
        if (hits == null || hits.Length == 0) return;

        foreach (var hit in hits)
        {
            if (!hit) continue;
            GameObject go = hit.transform.gameObject;

            if (!_lastHighlighted.Contains(go))
            {
                Highlight(go);
                _lastHighlighted.Add(go);
            }
        }
    }

    void Highlight(GameObject go)
    {
        var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in srs)
            sr.color = highlightColor;
    }

    void ClearHighlight()
    {
        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs)
                sr.color = defaultColor;
        }
        _lastHighlighted.Clear();
    }

    /// <summary>
    /// タグ色分けを解除して、「選択中」の色 (highlightColor) に戻す。
    /// ✕ボタンで呼ぶ。
    /// </summary>
    void RestorePlainHighlight()
    {
        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs)
                sr.color = highlightColor;
        }
    }

    // ========================================================
    // Menu / Detail
    // ========================================================

    void ShowMenu()
    {
        if (!menuRoot) return;

        if (_lastHighlighted.Count == 0)
        {
            HideMenuOnly();
            return;
        }

        menuRoot.gameObject.SetActive(true);
    }

    /// <summary>
    /// メニューだけ閉じる（建築ロックは触らない）
    /// 詳細パネルとカテゴリボタンの選択もすべて解除
    /// </summary>
    void HideMenuOnly()
    {
        if (menuRoot)
            menuRoot.gameObject.SetActive(false);
        if (detailPanelRoot)
            detailPanelRoot.gameObject.SetActive(false);

        HideDeleteConfirmUI();
        DeselectCategoryButton();

        ClearHighlight();

        // ★ ここを「新しく」追加してください
        SyncMiningIconsWithMiningQueue();
    }

    void OnCategoryButtonPressed(string categoryName, string[] tags, Button sourceButton)
    {
        // ★ 他の項目が押されたときは採掘アイコンをキャンセル
        ClearMiningIconsFromCurrentSelection();

        // ★ 他の項目が押されたときは削除予約アイコンも全部キャンセル
        if (buildPlacement != null)
        {
            buildPlacement.ClearAllPlannedDemolitions();
        }

        _currentCategoryButton = sourceButton;
        _currentCategoryTags = tags;

        // ★ Resource カテゴリだけ「採掘モード」、それ以外は「削除モード」
        if (sourceButton == resourceButton)
            _detailMode = DetailMode.Mining;
        else
            _detailMode = DetailMode.Demolish;

        // Delete 確定 UI はカテゴリ切り替えで一旦リセット
        HideDeleteConfirmUI();

        // EventSystem 的にもこのボタンを「選択中」にしておく
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(sourceButton.gameObject);
        }

        // ボタンの見た目変更（このボタンだけ selectedColor）
        SetCategoryButtonVisual(sourceButton);

        // タグフィルタで色分け
        ApplyTagFilter(tags);
        // 詳細メニュー表示
        ShowDetailPanel(categoryName);

        // ★ モードに応じてボタン表示・テキスト更新
        UpdateDetailButtonsForCurrentMode();
    }

    /// <summary>
    /// 現在のモード（削除 or 採掘）に応じて、詳細ボタンの表示を更新する
    /// </summary>
    void UpdateDetailButtonsForCurrentMode()
    {
        if (_detailDeleteButtonLabel != null)
        {
            _detailDeleteButtonLabel.text =
                (_detailMode == DetailMode.Mining) ? "Mining" : "Remove";
        }

        // モードが変わったタイミングでは、確定/キャンセルは一度隠す
        HideDeleteConfirmUI();
    }

    void ShowDetailPanel(string categoryName)
    {
        if (!menuRoot) return;
        if (!menuRoot.gameObject.activeSelf)
            menuRoot.gameObject.SetActive(true);

        if (detailPanelRoot)
            detailPanelRoot.gameObject.SetActive(true);

        if (detailTitleText)
            detailTitleText.text = categoryName;
    }

    /// <summary>
    /// 詳細メニューだけ閉じる（メニュー上半分は残す）
    /// このときカテゴリボタンの選択も解除し、
    /// タグ色分けを解除して「普通の選択色」に戻す。
    /// </summary>
    void CloseDetailPanelOnly()
    {
        if (!detailPanelRoot) return;

        detailPanelRoot.gameObject.SetActive(false);

        // プレビューだけのアイコンを消す
        ClearMiningIconsFromCurrentSelection();

        // 採掘キューの内容に合わせて MiningIcon を再配布
        SyncMiningIconsWithMiningQueue();

        // Delete 確定 UI をリセット
        HideDeleteConfirmUI();

        // ★ ここを変更：ハイライト色ではなく defaultColor に戻す
        // タグ色分け / ハイライトをすべて解除して、色を defaultColor に戻す
        ClearHighlight();   // ← RestorePlainHighlight() の代わりにこれを呼ぶ

        // ボタンの選択を解除＆色も通常に
        DeselectCategoryButton();

        // ボタンラベルなどもモードに合わせてリセット
        UpdateDetailButtonsForCurrentMode();
    }

    /// <summary>
    /// 詳細メニュー内の Delete ボタン：
    /// 現在のカテゴリタグに合致するオブジェクトに「削除予約」を付ける。
    /// （BuildPlacement.EnsureDemolitionPlannedForObject を呼ぶ）
    /// </summary>
    void OnDetailDeleteClicked()
    {
        // ★ Resource カテゴリのときは「採掘」モード
        if (_detailMode == DetailMode.Mining)
        {
            bool miningAny = ApplyMiningToCurrentResources();

            // アイコンを付けた Resource が 1つ以上あれば「確定／キャンセル」を表示
            if (miningAny)
            {
                if (detailDeleteConfirmButton)
                    detailDeleteConfirmButton.gameObject.SetActive(true);
                if (detailDeleteCancelButton)
                    detailDeleteCancelButton.gameObject.SetActive(true);
            }
            else
            {
                // 何もなければ消しておく
                HideDeleteConfirmUI();
            }

            return;
        }

        // ここから下は従来どおり「削除予約」モード
        if (buildPlacement == null)
        {
            Debug.LogWarning("[SelectionBoxDrawer] BuildPlacement が設定されていません。Delete ボタンは無効です。");
            return;
        }

        if (_lastHighlighted.Count == 0)
            return;

        if (_currentCategoryTags == null || _currentCategoryTags.Length == 0)
        {
            Debug.LogWarning("[SelectionBoxDrawer] カテゴリが選択されていないため、削除予約を付けられません。");
            return;
        }

        bool any = false;

        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;

            bool match = HasAnyTagInHierarchy(go, _currentCategoryTags);
            if (match)
            {
                buildPlacement.EnsureDemolitionPlannedForObject(go);
                any = true;
            }
        }

        if (!any)
        {
            Debug.Log("[SelectionBoxDrawer] 現在のカテゴリに合致する削除対象がありません。");
            HideDeleteConfirmUI();
            return;
        }

        // 削除予約が1つ以上付いたので、「確定」「キャンセル」を表示
        if (detailDeleteConfirmButton)
            detailDeleteConfirmButton.gameObject.SetActive(true);
        if (detailDeleteCancelButton)
            detailDeleteCancelButton.gameObject.SetActive(true);
    }

    /// <summary>
    /// 「確定」ボタン：
    /// BuildPlacement.StartPlannedDemolitions() を呼んで
    /// 削除予約中の建物に対してドローン解体を開始する。
    /// </summary>
    void OnDetailDeleteConfirmClicked()
    {
        // ★ まず採掘モードかどうかを見る
        if (_detailMode == DetailMode.Mining)
        {
            StartMiningJobsForCurrentIcons();
            HideDeleteConfirmUI();
            return;
        }

        // ここから下は従来どおり「削除確定」
        if (buildPlacement == null)
        {
            Debug.LogWarning("[SelectionBoxDrawer] BuildPlacement が設定されていません。確定ボタンは無効です。");
            return;
        }

        bool prevLock = BuildPlacement.s_buildLocked;
        BuildPlacement.s_buildLocked = false;
        buildPlacement.StartPlannedDemolitions();
        BuildPlacement.s_buildLocked = prevLock;

        HideDeleteConfirmUI();
    }

    /// <summary>
    /// 「キャンセル」ボタン：
    /// BuildPlacement に入っている削除予約を全部消す。
    /// （右ドラッグなどで予約していたものも含めて全てクリア）
    /// </summary>
    void OnDetailDeleteCancelClicked()
    {
        // ★ 採掘モードならアイコンだけキャンセル
        if (_detailMode == DetailMode.Mining)
        {
            ClearMiningIconsFromCurrentSelection();
            HideDeleteConfirmUI();
            return;
        }

        // 削除モードなら従来どおり解体予約を全消し
        if (buildPlacement != null)
        {
            buildPlacement.ClearAllPlannedDemolitions();
        }

        HideDeleteConfirmUI();
    }

    /// <summary>
    /// Delete 確定 / キャンセル UI の非表示
    /// </summary>
    void HideDeleteConfirmUI()
    {
        if (detailDeleteConfirmButton)
            detailDeleteConfirmButton.gameObject.SetActive(false);
        if (detailDeleteCancelButton)
            detailDeleteCancelButton.gameObject.SetActive(false);
    }

    // ========================================================
    // Category Button Visuals
    // ========================================================

    void InitializeCategoryButtonColors()
    {
        if (_categoryButtons == null) return;

        foreach (var b in _categoryButtons)
        {
            if (!b) continue;
            var cb = b.colors;
            cb.normalColor = categoryButtonNormalColor;
            cb.highlightedColor = categoryButtonNormalColor;
            cb.selectedColor = categoryButtonNormalColor;
            cb.pressedColor = categoryButtonNormalColor;
            b.colors = cb;
        }
    }

    void SetCategoryButtonVisual(Button selected)
    {
        if (_categoryButtons == null) return;

        foreach (var b in _categoryButtons)
        {
            if (!b) continue;
            var cb = b.colors;
            bool isSel = (selected != null && b == selected);
            var col = isSel ? categoryButtonSelectedColor : categoryButtonNormalColor;
            cb.normalColor = col;
            cb.highlightedColor = col;
            cb.selectedColor = col;
            cb.pressedColor = col;
            b.colors = cb;
        }
    }

    void DeselectCategoryButton()
    {
        // EventSystem の選択を解除
        if (EventSystem.current != null &&
            EventSystem.current.currentSelectedGameObject != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }

        _currentCategoryButton = null;
        _currentCategoryTags = null;

        // 全ボタンを通常色に戻す
        SetCategoryButtonVisual(null);
    }

    // ========================================================
    // Tag Filtering
    // ========================================================

    void ApplyTagFilter(string[] tags)
    {
        if (tags == null || tags.Length == 0)
            return;
        if (_lastHighlighted.Count == 0)
            return;

        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;

            bool match = HasAnyTagInHierarchy(go, tags);
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);

            foreach (var sr in srs)
            {
                sr.color = match ? tagMatchedColor : highlightColor;
            }
        }
    }

    bool HasAnyTagInHierarchy(GameObject go, string[] tags)
    {
        if (go == null || tags == null) return false;

        foreach (var t in tags)
        {
            if (string.IsNullOrEmpty(t)) continue;

            // 自身
            if (go.CompareTag(t))
                return true;

            // 子オブジェクト
            foreach (var tr in go.GetComponentsInChildren<Transform>(true))
            {
                if (tr.CompareTag(t))
                    return true;
            }
        }

        return false;
    }

    // 現在の選択範囲に付いている採掘アイコンをすべて消す
    void ClearMiningIconsFromCurrentSelection()
    {
        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;
            RemoveMiningIcon(go);
        }
    }

    // ★ 追加：シーン内の全 Resource ブロックから採掘アイコンを消す
    void ClearAllMiningIconsInScene()
    {
        var markers = FindObjectsOfType<ResourceMarker>();
        foreach (var m in markers)
        {
            if (!m) continue;

            // ★ ResourceMarker の階層内にある MiningIconRoot を全部消す
            foreach (var tr in m.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null) continue;
                if (tr.name == "MiningIconRoot")
                {
                    Destroy(tr.gameObject);
                }
            }
        }
    }

    // ★ 採掘キューの内容に合わせて MiningIcon を付け直す
    void SyncMiningIconsWithMiningQueue()
    {
        var manager = DroneBuildManager.Instance;
        if (manager == null)
        {
            ClearAllMiningIconsInScene();
            return;
        }

        // 一時バッファ
        var tmpTargets = new List<Vector3>();

        var markers = FindObjectsOfType<ResourceMarker>();
        foreach (var m in markers)
        {
            if (!m) continue;

            // まず、この ResourceMarker 配下のアイコンを全部消す
            foreach (var tr in m.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null) continue;
                if (tr.name == "MiningIconRoot")
                {
                    Destroy(tr.gameObject);
                }
            }

            // この ResourceMarker に対する採掘ターゲット座標を全部取得
            if (!manager.TryGetMiningTargets(m, tmpTargets))
                continue; // 何もキューされていなければスキップ

            // blocksRoot から子ブロックたちを取る（blocksRoot を使っている前提です）
            Transform blocksRoot = m.BlocksRoot != null ? m.BlocksRoot : m.transform;

            foreach (var targetPos in tmpTargets)
            {
                Transform best = null;
                float bestSqr = float.MaxValue;

                foreach (Transform child in blocksRoot)
                {
                    if (child == null) continue;

                    float d2 = (child.position - targetPos).sqrMagnitude;
                    if (d2 < bestSqr)
                    {
                        bestSqr = d2;
                        best = child;
                    }
                }

                if (best != null)
                {
                    // ★ 実際に採掘される Resource ブロックにアイコンを付ける
                    AddMiningIcon(best.gameObject);
                }
            }
        }
    }

    // ========================================================
    // Resource Mining Icon
    // ========================================================

    void RemoveMiningIcon(GameObject target)
    {
        if (target == null) return;

        var root = target.transform.Find("MiningIconRoot");
        if (root != null)
        {
            Destroy(root.gameObject);
        }
    }

    void AddMiningIcon(GameObject target)
    {
        if (target == null) return;

        // Prefab も Sprite も両方空なら何もできない
        if (resourceMiningIconPrefab == null && resourceMiningIconSprite == null)
        {
            Debug.LogWarning("[SelectionBoxDrawer] resourceMiningIconPrefab / resourceMiningIconSprite のどちらも設定されていません。");
            return;
        }

        // すでに建物がある細かいセルなら付けない
        if (buildPlacement != null &&
            buildPlacement.HasBuildingOnFineCellAtWorldPos(target.transform.position))
        {
            return;
        }

        // 既存を消してリセット
        var oldRoot = target.transform.Find("MiningIconRoot");
        if (oldRoot != null)
            Destroy(oldRoot.gameObject);

        GameObject root = new GameObject("MiningIconRoot");
        root.transform.SetParent(target.transform, false);

        // スプライトの中心の真上に root を置く
        Vector3 worldCenter = target.transform.position;
        var srTarget = target.GetComponentInChildren<SpriteRenderer>();
        if (srTarget != null)
        {
            worldCenter = srTarget.bounds.center;
        }
        root.transform.position = worldCenter;

        // ★ ここから Prefab or Sprite でアイコンを作る
        GameObject icon;

        if (resourceMiningIconPrefab != null)
        {
            // Prefab が指定されている場合はそれを使う
            icon = Instantiate(resourceMiningIconPrefab, root.transform);
            icon.transform.localPosition = new Vector3(0f, resourceMiningIconYOffset, 0f);
        }
        else
        {
            // Prefab がない場合は従来通り Sprite から作る
            icon = new GameObject("MiningIcon");
            icon.transform.SetParent(root.transform, false);
            icon.transform.localPosition = new Vector3(0f, resourceMiningIconYOffset, 0f);

            var sr = icon.AddComponent<SpriteRenderer>();
            sr.sprite = resourceMiningIconSprite;
            sr.sortingOrder = 998;

            // Sprite 版はここで Blinker を付ける
            var blinker = icon.AddComponent<MiningIconBlinker>();
            blinker.SetBlinking(true);    // ★ 最初から点滅ON
        }

        // Prefab 版でも Blinker が付いていたら点滅ONにする
        var prefabBlinker = icon.GetComponent<MiningIconBlinker>();
        if (prefabBlinker != null)
        {
            prefabBlinker.SetBlinking(true);  // ★ プレハブも点滅ON
        }

        // 親スケールの逆数でアイコンサイズを一定に保つ
        var parentScale = target.transform.lossyScale;
        float invX = (parentScale.x != 0f) ? 1f / parentScale.x : 1f;
        float invY = (parentScale.y != 0f) ? 1f / parentScale.y : 1f;

        icon.transform.localScale = new Vector3(invX, invY, 1f) * resourceMiningIconScale;
    }

    /// <summary>
    /// 現在の選択範囲の中で MiningIconRoot が付いている Resource に
    /// ドローンの採掘ジョブを投げる
    /// </summary>
    void StartMiningJobsForCurrentIcons()
    {
        var manager = DroneBuildManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning("[SelectionBoxDrawer] DroneBuildManager がシーンにありません。");
            return;
        }

        // ここは今まで通り：一度キューをリセットして、_lastHighlighted からキューを積む
        manager.ClearAllMiningReservations();

        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;

            var root = go.transform.Find("MiningIconRoot");
            if (root == null) continue;

            var resource = go.GetComponentInParent<ResourceMarker>();
            if (resource == null) continue;

            Vector3 targetPos = go.transform.position;
            manager.EnqueueResourceMining(resource, targetPos);
        }

        // プレビュー用アイコンはとりあえず全部消す
        ClearMiningIconsFromCurrentSelection();

        // ★ 追加：採掘キューの内容に合わせて、シーン全体の MiningIcon を付け直す
        SyncMiningIconsWithMiningQueue();
    }

    // ========================================================
    // Resource Mining Icon
    // ========================================================

    /// <summary>
    /// 現在の範囲選択＋Resource カテゴリに対して「採掘アイコン」をトグルする
    /// </summary>
    bool ApplyMiningToCurrentResources()
    {
        if (_lastHighlighted.Count == 0)
            return false;

        if (_currentCategoryTags == null || _currentCategoryTags.Length == 0)
        {
            Debug.LogWarning("[SelectionBoxDrawer] カテゴリが選択されていないため、採掘アイコンを付けられません。");
            return false;
        }

        bool any = false;

        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;

            bool match = HasAnyTagInHierarchy(go, _currentCategoryTags);
            if (!match) continue;

            // すでに MiningIcon が付いていれば外し、なければ付ける（トグル）
            var existingRoot = go.transform.Find("MiningIconRoot");
            if (existingRoot != null)
            {
                RemoveMiningIcon(go);
            }
            else
            {
                AddMiningIcon(go);
            }

            any = true;
        }

        if (!any)
        {
            Debug.Log("[SelectionBoxDrawer] Resource カテゴリに合致する対象がありません。");
        }

        return any;
    }

    // ========================================================
    // Buildモード側からの通知
    // ========================================================

    /// <summary>
    /// BuildBar など「外部」から建築モードが開始されたときに呼ぶ。
    /// ハイライト / メニュー / 詳細パネルを閉じて、BuildPlacement のロックを解除。
    /// このときカテゴリボタンの選択も解除。
    /// </summary>
    public static void NotifyBuildModeStartedFromOutside()
    {
        if (Instance == null) return;
        Instance.OnBuildModeStartedFromOutside_Internal();
    }

    void OnBuildModeStartedFromOutside_Internal()
    {
        // ★ 建築モードに入るときは、採掘キューに合わせて MiningIcon を同期
        // （キューにない Resource からはアイコンを消す）
        SyncMiningIconsWithMiningQueue();
        // 以前の ClearAllMiningIconsInScene(); は削除して OK

        // ロック解除
        BuildPlacement.s_buildLocked = false;

        // ハイライト・枠・メニューを全部閉じる
        ClearHighlight();

        if (selectionRectRoot)
            selectionRectRoot.gameObject.SetActive(false);
        if (menuRoot)
            menuRoot.gameObject.SetActive(false);
        if (detailPanelRoot)
            detailPanelRoot.gameObject.SetActive(false);

        HideDeleteConfirmUI();
        DeselectCategoryButton();
    }

    // ========================================================
    // Utility
    // ========================================================

    bool IsPointerOverUI()
    {
        if (EventSystem.current == null) return false;
        return EventSystem.current.IsPointerOverGameObject();
    }
}