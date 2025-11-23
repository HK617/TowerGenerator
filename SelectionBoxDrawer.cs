using System.Collections.Generic;
using UnityEngine;

// 新 Input System 対応
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// ワールド空間型の選択長方形。
/// Shift + 左ドラッグで地面上に水色の半透明四角を出し、
/// その範囲に触れている 2D Collider を持つブロックをハイライトする。
/// </summary>
public class SelectionBoxDrawer : MonoBehaviour
{
    [Header("World Selection Rect")]
    [Tooltip("ワールド空間に置く選択枠のルート（中に SpriteRenderer を持つ子オブジェクトがある前提）")]
    public Transform selectionRectRoot;

    [Tooltip("選択枠の見た目に使う SpriteRenderer（半透明の四角）")]
    public SpriteRenderer selectionRectRenderer;

    [Header("Selection Target")]
    [Tooltip("ハイライト対象にするレイヤー（Machine など）。空なら全レイヤー")]
    public LayerMask selectableLayers = ~0;

    [Header("Highlight Colors")]
    [Tooltip("選択されているオブジェクトにかける色")]
    public Color highlightColor = new Color(1f, 0.9f, 0.4f, 1f); // 黄色っぽい
    [Tooltip("元に戻すときの色（通常は白）")]
    public Color defaultColor = Color.white;

    [Header("Rect Appearance")]
    [Tooltip("選択長方形の色（水色＋半透明推奨）")]
    public Color rectColor = new Color(0.4f, 0.8f, 1f, 0.25f);

    bool _isDragging = false;
    Vector2 _startWorldPos;
    Vector2 _currentWorldPos;

    // 直近でハイライトしているオブジェクト
    readonly List<GameObject> _lastHighlighted = new();

    void Awake()
    {
        if (!selectionRectRoot)
        {
            Debug.LogError("[SelectionBoxDrawer] selectionRectRoot が設定されていません。");
            enabled = false;
            return;
        }

        if (!selectionRectRenderer)
        {
            selectionRectRenderer = selectionRectRoot.GetComponentInChildren<SpriteRenderer>();
        }

        if (!selectionRectRenderer)
        {
            Debug.LogError("[SelectionBoxDrawer] selectionRectRenderer が見つかりません。SpriteRenderer を設定してください。");
            enabled = false;
            return;
        }

        // 初期状態では枠を隠す
        selectionRectRoot.gameObject.SetActive(false);
        selectionRectRenderer.color = rectColor;
    }

    void Update()
    {
        if (Camera.main == null) return;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null || keyboard == null) return;

        bool shiftHeld = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
        Vector2 mouseScreen = mouse.position.ReadValue();

        // スクリーン → ワールド
        Vector3 mouseWorld3 = ScreenToWorld(mouseScreen);
        Vector2 mouseWorld = new Vector2(mouseWorld3.x, mouseWorld3.y);

        // --- ドラッグ開始 ---
        if (shiftHeld && mouse.leftButton.wasPressedThisFrame)
        {
            _isDragging = true;
            _startWorldPos = mouseWorld;
            _currentWorldPos = mouseWorld;

            selectionRectRoot.gameObject.SetActive(true);
            UpdateRectTransform(_startWorldPos, _currentWorldPos);

            ClearHighlight();
        }

        // --- ドラッグ終了 ---
        if (_isDragging && mouse.leftButton.wasReleasedThisFrame)
        {
            _isDragging = false;
            selectionRectRoot.gameObject.SetActive(false);

            // 最終位置で選択確定（ハイライトは残す）
            UpdateHighlight(_startWorldPos, _currentWorldPos);
        }

        // --- ドラッグ中 ---
        if (_isDragging)
        {
            _currentWorldPos = mouseWorld;
            UpdateRectTransform(_startWorldPos, _currentWorldPos);
            UpdateHighlight(_startWorldPos, _currentWorldPos);
        }

#else
        // 旧 InputManager 用
        bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        Vector3 mouseScreen3 = Input.mousePosition;
        Vector2 mouseScreen = new Vector2(mouseScreen3.x, mouseScreen3.y);

        Vector3 mouseWorld3 = ScreenToWorld(mouseScreen);
        Vector2 mouseWorld = new Vector2(mouseWorld3.x, mouseWorld3.y);

        if (shiftHeld && Input.GetMouseButtonDown(0))
        {
            _isDragging = true;
            _startWorldPos = mouseWorld;
            _currentWorldPos = mouseWorld;

            selectionRectRoot.gameObject.SetActive(true);
            UpdateRectTransform(_startWorldPos, _currentWorldPos);

            ClearHighlight();
        }

        if (_isDragging && Input.GetMouseButtonUp(0))
        {
            _isDragging = false;
            selectionRectRoot.gameObject.SetActive(false);

            UpdateHighlight(_startWorldPos, _currentWorldPos);
        }

        if (_isDragging)
        {
            _currentWorldPos = mouseWorld;
            UpdateRectTransform(_startWorldPos, _currentWorldPos);
            UpdateHighlight(_startWorldPos, _currentWorldPos);
        }
#endif
    }

    // ========================= ワールド座標変換 =========================

    Vector3 ScreenToWorld(Vector2 screenPos)
    {
        var cam = Camera.main;
        // 2Dオーソカメラ想定: z はカメラから原点までの距離
        float z = -cam.transform.position.z;
        return cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, z));
    }

    // ========================= 長方形の見た目更新（ワールド空間） =========================

    void UpdateRectTransform(Vector2 start, Vector2 end)
    {
        float minX = Mathf.Min(start.x, end.x);
        float minY = Mathf.Min(start.y, end.y);
        float maxX = Mathf.Max(start.x, end.x);
        float maxY = Mathf.Max(start.y, end.y);

        float width = Mathf.Max(0.0001f, maxX - minX);
        float height = Mathf.Max(0.0001f, maxY - minY);

        // 中心位置
        Vector3 center = new Vector3(minX + width * 0.5f, minY + height * 0.5f, 0f);

        selectionRectRoot.position = center;

        // Sprite が「1ユニット四方」の大きさなら、そのまま scale = (width, height)
        // もし違う場合はスプライトのサイズに応じて調整してください。
        selectionRectRoot.localScale = new Vector3(width, height, 1f);

        // 色を毎フレーム念のため反映
        selectionRectRenderer.color = rectColor;
    }

    // ========================= ハイライト処理 =========================

    void UpdateHighlight(Vector2 startWorld, Vector2 endWorld)
    {
        // 一度前のハイライトを全部解除
        ClearHighlight();

        float minX = Mathf.Min(startWorld.x, endWorld.x);
        float minY = Mathf.Min(startWorld.y, endWorld.y);
        float maxX = Mathf.Max(startWorld.x, endWorld.x);
        float maxY = Mathf.Max(startWorld.y, endWorld.y);

        Vector2 a = new Vector2(minX, minY);
        Vector2 b = new Vector2(maxX, maxY);

        Collider2D[] hits = Physics2D.OverlapAreaAll(a, b, selectableLayers);
        if (hits == null || hits.Length == 0) return;

        var uniqueObjects = new HashSet<GameObject>();

        foreach (var h in hits)
        {
            if (!h) continue;

            // 「ブロック単位」で扱いたいので、まず SpriteRenderer を持つ親を探す
            GameObject target = null;

            var srParent = h.GetComponentInParent<SpriteRenderer>();
            if (srParent != null)
            {
                target = srParent.gameObject;
            }
            else
            {
                // どうしても見つからなければ、そのコライダー自身を対象にする
                target = h.gameObject;
            }

            if (!target) continue;
            if (!uniqueObjects.Add(target)) continue;
        }

        foreach (var go in uniqueObjects)
        {
            HighlightObject(go);
            _lastHighlighted.Add(go);
        }
    }

    void HighlightObject(GameObject go)
    {
        if (!go) return;

        // このオブジェクト配下の SpriteRenderer をすべてハイライト
        var renderers = go.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var r in renderers)
        {
            r.color = highlightColor;
        }
    }

    void ClearHighlight()
    {
        if (_lastHighlighted.Count == 0) return;

        foreach (var go in _lastHighlighted)
        {
            if (!go) continue;

            var renderers = go.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var r in renderers)
            {
                r.color = defaultColor;
            }
        }

        _lastHighlighted.Clear();
    }
}
