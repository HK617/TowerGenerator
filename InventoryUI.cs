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

    [Header("Craft (Building Kits)")]
    [Tooltip("クラフト可能な建物リスト")]
    public List<BuildingDef> craftableBuildings = new();

    // クラフト行をキャッシュして、毎フレーム Destroy/Instantiate しないようにする
    class CraftRowCache
    {
        public BuildingDef def;
        public GameObject root;
        public Image icon;
        public TMP_Text label;
        public Button button;
    }

    List<CraftRowCache> _craftRows = new List<CraftRowCache>();

    [Tooltip("建物クラフト行を並べる親 (VerticalLayoutGroup など)")]
    public Transform craftListRoot;

    [Tooltip("建物クラフト用の1行プレハブ (Icon + Label + Button)")]
    public GameObject craftRowPrefab;

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

        var inv = dm.GlobalInventory;

        var sorted = new List<KeyValuePair<string, int>>(inv);
        sorted.Sort((a, b) => string.Compare(a.Key, b.Key, System.StringComparison.Ordinal));

        foreach (var kv in sorted)
        {
            string itemName = kv.Key;
            int count = kv.Value;

            var row = Instantiate(rowPrefab, listRoot);

            Image img = null;
            TMP_Text txt = null;

            var iconTr = row.transform.Find("Icon");
            if (iconTr != null)
                img = iconTr.GetComponent<Image>();

            var labelTr = row.transform.Find("Label");
            if (labelTr == null)
                labelTr = row.transform.Find("Content");
            if (labelTr != null)
                txt = labelTr.GetComponent<TMP_Text>();

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

        // ★追加：建物クラフトリストも更新
        RefreshCraftList();
    }

    void RefreshCraftList()
    {
        if (craftListRoot == null || craftRowPrefab == null)
            return;

        var dm = DroneBuildManager.Instance;
        if (dm == null)
            return;

        // --- 行数が変わっていたら作り直し（頻繁には変わらない前提） ---
        bool needRebuild = (_craftRows.Count != craftableBuildings.Count) ||
                           (craftListRoot.childCount != craftableBuildings.Count);

        if (needRebuild)
        {
            // 古い行を全部削除
            for (int i = craftListRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(craftListRoot.GetChild(i).gameObject);
            }
            _craftRows.Clear();

            // craftableBuildings の数だけ新しく作る
            foreach (var def in craftableBuildings)
            {
                if (def == null) continue;

                var rowGO = Instantiate(craftRowPrefab, craftListRoot);

                var cache = new CraftRowCache();
                cache.def = def;
                cache.root = rowGO;

                var iconTr = rowGO.transform.Find("Icon");
                if (iconTr != null)
                    cache.icon = iconTr.GetComponent<Image>();

                var labelTr = rowGO.transform.Find("Label");
                if (labelTr == null)
                    labelTr = rowGO.transform.Find("Content");
                if (labelTr != null)
                    cache.label = labelTr.GetComponent<TMP_Text>();

                var btnTr = rowGO.transform.Find("CraftButton");
                if (btnTr != null)
                    cache.button = btnTr.GetComponent<Button>();
                if (cache.button == null)
                    cache.button = rowGO.GetComponentInChildren<Button>();

                // ボタンイベントはここで一度だけ登録
                if (cache.button != null)
                {
                    var capturedDef = def;
                    cache.button.onClick.RemoveAllListeners();
                    cache.button.onClick.AddListener(() => OnClickCraftBuilding(capturedDef));
                }

                _craftRows.Add(cache);
            }
        }

        // --- 表示内容の更新だけ行う（Destroy/Instantiate はしない） ---
        for (int i = 0; i < _craftRows.Count && i < craftableBuildings.Count; i++)
        {
            var cache = _craftRows[i];
            var def = craftableBuildings[i];
            if (def == null || cache.root == null) continue;

            // def が差し替えられていた場合に備えて更新しておく
            cache.def = def;

            if (cache.icon != null)
            {
                cache.icon.sprite = def.icon;
                cache.icon.enabled = (cache.icon.sprite != null);
            }

            int kitCount = dm.GetCraftedBuildingCount(def);
            bool canCraft = dm.CanCraftBuilding(def, 1);

            if (cache.label != null)
            {
                cache.label.text = kitCount.ToString();   // ★数字だけ
            }

            if (cache.button != null)
            {
                cache.button.interactable = canCraft;
            }
        }
    }

    void OnClickCraftBuilding(BuildingDef def)
    {
        var dm = DroneBuildManager.Instance;
        if (dm == null || def == null) return;

        if (!dm.TryCraftBuilding(def, 1))
        {
            Debug.Log($"[InventoryUI] {def.displayName} をクラフトできませんでした（素材不足？）");
            return;
        }

        // 素材在庫 & キット在庫が変わるので、一覧を更新
        RefreshList();
    }

    Sprite FindIcon(string itemName)
    {
        if (string.IsNullOrEmpty(itemName)) return null;
        if (_iconLookup.TryGetValue(itemName, out var sp))
            return sp;
        return null;
    }
}
