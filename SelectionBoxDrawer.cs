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
///   - Deleteボタン：**現在選択中のカテゴリに合致する建築物だけ**を BuildPlacement に解体予約させる
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

    [Header("Detail Panel (menu lower half)")]
    [Tooltip("メニューパネルの下半分に置く詳細メニューのルート")]
    public RectTransform detailPanelRoot;
    [Tooltip("詳細メニューのタイトル (選択中カテゴリ名など)")]
    public TMPro.TMP_Text detailTitleText;
    [Tooltip("詳細メニューを閉じるボタン（✕）")]
    public Button detailCloseButton;
    [Tooltip("選択している建物を削除予約するボタン")]
    public Button detailDeleteButton;

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
    [Tooltip("削除予約を投げる先の BuildPlacement を指定してください")]
    public BuildPlacement buildPlacement;

    [Header("Settings")]
    public float minDragDistance = 0.2f;

    bool _isDragging;
    Vector2 _startWorldPos;
    Vector2 _currentWorldPos;

    readonly List<GameObject> _lastHighlighted = new();

    // 直近で押されたカテゴリボタン
    Button _currentCategoryButton;
    // 全カテゴリボタンの配列（色をまとめて変える用）
    Button[] _categoryButtons;
    // 現在選択中カテゴリのタグ配列（Delete 用）
    string[] _currentCategoryTags;

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
        HideMenuOnly(); // メニューと詳細、ボタン選択もクリア

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

        DeselectCategoryButton();
    }

    void OnCategoryButtonPressed(string categoryName, string[] tags, Button sourceButton)
    {
        _currentCategoryButton = sourceButton;
        _currentCategoryTags = tags;  // ← このカテゴリのタグを記録（Delete 用）

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
        if (detailPanelRoot)
            detailPanelRoot.gameObject.SetActive(false);

        // タグ色分けを解除して、選択中ハイライト色に戻す
        RestorePlainHighlight();

        // ボタンの選択を解除＆色も通常に
        DeselectCategoryButton();
        // 建築ロックは解除しない（ユーザーが建築モードを開始するときに解除）
    }

    /// <summary>
    /// 詳細メニュー内の Delete ボタンが押されたとき：
    /// 「現在選択中のカテゴリのタグに合致する」建物だけを
    /// BuildPlacement に解体予約させる。
    /// </summary>
    void OnDetailDeleteClicked()
    {
        if (buildPlacement == null)
        {
            Debug.LogWarning("[SelectionBoxDrawer] BuildPlacement が設定されていません。Delete ボタンは無効です。");
            return;
        }

        if (_lastHighlighted.Count == 0)
            return;

        if (_currentCategoryTags == null || _currentCategoryTags.Length == 0)
        {
            // 理論上、カテゴリボタンを押さないと詳細メニューは開かない想定
            Debug.LogWarning("[SelectionBoxDrawer] 現在選択中のカテゴリがありません。Delete は何もしません。");
            return;
        }

        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;

            // 現在のカテゴリのタグに合致するものだけ削除予約
            if (HasAnyTagInHierarchy(go, _currentCategoryTags))
            {
                buildPlacement.EnsureDemolitionPlannedForObject(go);
            }
        }

        // 解体アイコンは BuildPlacement 側のメソッド内で付くのでここでは何もしない。
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

    /// <summary>
    /// 選択中ボタンだけ categoryButtonSelectedColor にする。
    /// それ以外は categoryButtonNormalColor。
    /// </summary>
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
