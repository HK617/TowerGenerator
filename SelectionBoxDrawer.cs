using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// 新 Input System 対応
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// ワールド空間型の選択長方形。
/// Shift + 左ドラッグで地面上に水色の半透明四角を出し、
/// その範囲に触れている 2D Collider を持つブロックをハイライトする。
/// 範囲選択が終わったら右側の固定メニューを表示して、
/// 各ボタンごとにタググループで色分けする。
///
/// ・選択開始時に BuildPlacement.s_buildLocked = true で建築モード停止
/// ・建築ボタンが押されたら NotifyBuildModeStartedFromOutside() から
///   ハイライト / メニューを消してロック解除
/// </summary>
public class SelectionBoxDrawer : MonoBehaviour
{
    // 他クラスからアクセスする用
    public static SelectionBoxDrawer Instance { get; private set; }

    [Header("World Selection Rect")]
    public Transform selectionRectRoot;
    public SpriteRenderer selectionRectRenderer;

    [Header("Selection Target")]
    public LayerMask selectableLayers = ~0;

    [Header("Highlight Colors")]
    public Color highlightColor = new Color(1f, 0.9f, 0.4f, 1f);
    public Color defaultColor = Color.white;

    [Header("Rect Appearance")]
    public Color rectColor = new Color(0.4f, 0.8f, 1f, 0.25f);

    [Header("Fixed Menu UI")]
    public RectTransform menuRoot;
    public Button blockButton;
    public Button turretButton;
    public Button machineButton;
    public Button resourceButton;
    public Button conveyorButton;

    [Header("Tag Groups (multiple tags allowed)")]
    public string[] blockTags;
    public string[] turretTags;
    public string[] machineTags;
    public string[] resourceTags;
    public string[] conveyorTags;

    [Header("Tagged Color")]
    public Color tagMatchedColor = new Color(0.7f, 1f, 0.7f, 1f);

    [Header("Settings")]
    public float minDragDistance = 0.2f;

    bool _isDragging;
    Vector2 _startWorldPos;
    Vector2 _currentWorldPos;

    readonly List<GameObject> _lastHighlighted = new();

    void Awake()
    {
        // シングルトンっぽく
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

        // ボタン登録
        if (blockButton)
            blockButton.onClick.AddListener(() => ApplyTagFilter(blockTags));
        if (turretButton)
            turretButton.onClick.AddListener(() => ApplyTagFilter(turretTags));
        if (machineButton)
            machineButton.onClick.AddListener(() => ApplyTagFilter(machineTags));
        if (resourceButton)
            resourceButton.onClick.AddListener(() => ApplyTagFilter(resourceTags));
        if (conveyorButton)
            conveyorButton.onClick.AddListener(() => ApplyTagFilter(conveyorTags));
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

    // ================= Drag operations =================

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
        if (menuRoot)
            menuRoot.gameObject.SetActive(false);

        // ★ 選択開始時に建築モードを完全に止める
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
            // ほぼ動いていない → 何もしない
            ClearHighlight();
            HideMenuOnly();           // ロックは解除しない
            return;
        }

        UpdateHighlight(_startWorldPos, _currentWorldPos);
        ShowMenu();                   // メニュー表示中もロックは維持
    }

    void Dragging(Vector2 pos)
    {
        _currentWorldPos = pos;
        UpdateRectTransform(_startWorldPos, pos);
        UpdateHighlight(_startWorldPos, pos);
    }

    // ================= UI / Rect =================

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

    // ================= Highlight =================

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

    // ================= Menu =================

    void ShowMenu()
    {
        if (!menuRoot) return;

        if (_lastHighlighted.Count == 0)
        {
            HideMenuOnly();
            return;
        }

        // 位置は固定。 anchoredPosition はいじらない
        menuRoot.gameObject.SetActive(true);
    }

    /// <summary>
    /// メニューだけ閉じる（建築ロックは触らない）
    /// </summary>
    void HideMenuOnly()
    {
        if (menuRoot)
            menuRoot.gameObject.SetActive(false);
    }

    // ================= Tag Filtering =================

    void ApplyTagFilter(string[] tags)
    {
        if (tags == null || tags.Length == 0) return;
        if (_lastHighlighted.Count == 0) return;

        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;
            bool match = HasAnyTag(go, tags);

            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs)
                sr.color = match ? tagMatchedColor : highlightColor;
        }
    }

    bool HasAnyTag(GameObject go, string[] tags)
    {
        foreach (var t in tags)
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (go.CompareTag(t)) return true;
        }
        return false;
    }

    // ================= UI Detection =================

    bool IsPointerOverUI()
    {
        if (EventSystem.current == null) return false;
        return EventSystem.current.IsPointerOverGameObject();
    }

    // ================= 外部からの「建築モード開始」通知 =================

    /// <summary>
    /// BuildBarUI などから「建築スタートするよ」と呼んでもらう。
    /// メニュー＆ハイライト＆選択枠を消して、建築ロックを解除する。
    /// </summary>
    public static void NotifyBuildModeStartedFromOutside()
    {
        if (Instance == null) return;
        Instance.OnBuildModeStarted();
    }

    void OnBuildModeStarted()
    {
        // ハイライトと選択枠を全部消す
        ClearHighlight();

        if (selectionRectRoot)
            selectionRectRoot.gameObject.SetActive(false);

        HideMenuOnly();

        // ★ ここで初めて建築ロック解除 → 以降は通常通り建築モードが動く
        BuildPlacement.s_buildLocked = false;
    }
}
