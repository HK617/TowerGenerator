using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class InventoryUI : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("インベントリ全体をまとめたパネル (On/Off を切り替える)")]
    public GameObject panel;

    [Tooltip("アイテム行を並べる親 (VerticalLayoutGroup など)")]
    public Transform listRoot;

    [Tooltip("1 行分のプレハブ (Image + TMP_Text が付いているもの)")]
    public GameObject rowPrefab;

    [Header("Selection Menu (範囲選択メニュー)")]
    [Tooltip("範囲選択メニューの SelectionBoxDrawer。未設定なら自動検索します。")]
    public SelectionBoxDrawer selectionMenu;

    [Header("Fade 設定")]
    [Tooltip("フェードイン・アウトにかかる時間（秒）")]
    public float fadeDuration = 0.2f;

    [Tooltip("開いている間の更新間隔（秒）。0 にすると毎フレーム更新")]
    public float refreshInterval = 0.25f;

    [System.Serializable]
    public class ItemIconEntry
    {
        [Tooltip("GlobalInventory 内で使われるアイテム名 (例: ResourceDef の displayName)")]
        public string itemName;

        [Tooltip("このアイテムのアイコン")]
        public Sprite icon;
    }

    [Header("Item Icons")]
    public List<ItemIconEntry> iconTable = new();

    Dictionary<string, Sprite> _iconLookup = new();

    CanvasGroup _canvasGroup;
    bool _wantVisible = false;   // 「論理的に開いているか」のフラグ
    bool _isFading = false;
    float _fadeTime = 0f;
    float _fadeStartAlpha = 0f;
    float _fadeTargetAlpha = 0f;

    float _refreshTimer = 0f;

    void Awake()
    {
        BuildIconLookup();

        if (panel != null)
        {
            _canvasGroup = panel.GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
                _canvasGroup = panel.AddComponent<CanvasGroup>();

            // 初期状態は非表示
            _canvasGroup.alpha = 0f;
            panel.SetActive(false);
            _wantVisible = false;
        }
    }

    void BuildIconLookup()
    {
        _iconLookup.Clear();
        foreach (var entry in iconTable)
        {
            if (entry == null) continue;
            if (string.IsNullOrEmpty(entry.itemName)) continue;
            if (entry.icon == null) continue;

            if (!_iconLookup.ContainsKey(entry.itemName))
                _iconLookup.Add(entry.itemName, entry.icon);
        }
    }

    void Update()
    {
        // SelectionBoxDrawer を未設定なら一度だけ自動検索
        if (selectionMenu == null)
        {
            selectionMenu = FindFirstObjectByType<SelectionBoxDrawer>();
        }

        bool selectionMenuOpen = false;
        if (selectionMenu != null && selectionMenu.menuRoot != null)
        {
            selectionMenuOpen = selectionMenu.menuRoot.gameObject.activeSelf;
        }

        // 範囲選択メニューが開かれていたら、インベントリは自動で閉じる
        if (selectionMenuOpen && _wantVisible)
        {
            HidePanel();
        }

        // 範囲選択メニューが開いている間は、Eキーでの操作は無効
        if (!selectionMenuOpen)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.eKey.wasPressedThisFrame)
            {
                TogglePanel();
            }
#else
            if (Input.GetKeyDown(KeyCode.E))
            {
                TogglePanel();
            }
#endif
        }

        // フェード進行
        UpdateFade();

        // 開いている間は定期的にリスト更新
        if (panel != null && panel.activeSelf && _wantVisible)
        {
            if (refreshInterval <= 0f)
            {
                // 毎フレーム更新
                RefreshList();
            }
            else
            {
                _refreshTimer += Time.deltaTime;
                if (_refreshTimer >= refreshInterval)
                {
                    _refreshTimer = 0f;
                    RefreshList();
                }
            }
        }
    }

    void TogglePanel()
    {
        if (panel == null || listRoot == null || rowPrefab == null)
            return;

        if (_wantVisible)
            HidePanel();
        else
            ShowPanel();
    }

    void ShowPanel()
    {
        if (panel == null || _canvasGroup == null)
            return;

        _wantVisible = true;
        _refreshTimer = 0f;

        // 最初にリストを一度更新しておく
        RefreshList();

        panel.SetActive(true);

        _isFading = true;
        _fadeTime = 0f;
        _fadeStartAlpha = _canvasGroup.alpha;
        _fadeTargetAlpha = 1f;
    }

    void HidePanel()
    {
        if (panel == null || _canvasGroup == null)
            return;

        _wantVisible = false;

        _isFading = true;
        _fadeTime = 0f;
        _fadeStartAlpha = _canvasGroup.alpha;
        _fadeTargetAlpha = 0f;
    }

    void UpdateFade()
    {
        if (!_isFading || _canvasGroup == null || panel == null)
            return;

        _fadeTime += Time.deltaTime;
        float t = (fadeDuration > 0f) ? Mathf.Clamp01(_fadeTime / fadeDuration) : 1f;
        float a = Mathf.Lerp(_fadeStartAlpha, _fadeTargetAlpha, t);
        _canvasGroup.alpha = a;

        if (t >= 1f)
        {
            _isFading = false;
            _canvasGroup.alpha = _fadeTargetAlpha;

            if (_fadeTargetAlpha <= 0f)
            {
                panel.SetActive(false);
            }
        }
    }

    void RefreshList()
    {
        if (listRoot == null || rowPrefab == null)
            return;

        // 既存行を全部削除
        for (int i = listRoot.childCount - 1; i >= 0; i--)
        {
            Destroy(listRoot.GetChild(i).gameObject);
        }

        var dm = DroneBuildManager.Instance;
        if (dm == null)
            return;

        // ★ GlobalInventory = Base に納品済みの合計だけを持つ辞書
        var inv = dm.GlobalInventory;    // IReadOnlyDictionary<string, int>:contentReference[oaicite:1]{index=1}

        // 名前でソートして表示 (任意)
        var sorted = new List<KeyValuePair<string, int>>(inv);
        sorted.Sort((a, b) => string.Compare(a.Key, b.Key, System.StringComparison.Ordinal));

        foreach (var kv in sorted)
        {
            string itemName = kv.Key;   // 例: "鉄鉱石"
            int count = kv.Value;

            var row = Instantiate(rowPrefab, listRoot);

            // ★ 名前で子を探して、それぞれのコンポーネントを取る
            Image img = null;
            TMP_Text txt = null;

            // 「Icon」という名前の子から Image を取る
            var iconTr = row.transform.Find("Icon");
            if (iconTr != null)
                img = iconTr.GetComponent<Image>();

            // 「Label」や「Content」など、名前に合わせて取得
            var labelTr = row.transform.Find("Label");
            if (labelTr == null)
                labelTr = row.transform.Find("Content");
            if (labelTr != null)
                txt = labelTr.GetComponent<TMP_Text>();

            // 念のため、見つからなかったときは従来どおり
            if (img == null)
                img = row.GetComponentInChildren<Image>();
            if (txt == null)
                txt = row.GetComponentInChildren<TMP_Text>();

            if (img != null)
            {
                img.sprite = FindIcon(itemName);
                img.enabled = (img.sprite != null);
            }

            if (txt != null)
            {
                txt.text = $"{itemName} x {count}";
            }
        }
    }

    Sprite FindIcon(string itemName)
    {
        if (string.IsNullOrEmpty(itemName)) return null;
        if (_iconLookup.TryGetValue(itemName, out var sp))
            return sp;
        return null;
    }
}
