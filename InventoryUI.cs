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

    [Header("インベントリ内の建物ボタン (Eキーで開く画面・ゴースト選択)")]
    [Tooltip("ここに並べた BuildingDef をインベントリパネル内に表示します")]
    public List<BuildingDef> craftableBuildings = new();

    // 共通で使う行キャッシュ
    class CraftRowCache
    {
        public BuildingDef def;
        public GameObject root;
        public Image icon;
        public TMP_Text label;
        public Button button;
    }

    // Eキー側（インベントリパネル）の建物リスト
    List<CraftRowCache> _inventoryBuildRows = new();

    [Tooltip("インベントリパネル内の建物ボタンを並べる親 (VerticalLayoutGroup など)")]
    public Transform craftListRoot;

    [Tooltip("インベントリパネル内の建物ボタン用1行プレハブ (Icon + Label + Button)")]
    public GameObject craftRowPrefab;

    [Header("クラフト画面 (Qキーで開く別パネル)")]
    [Tooltip("Qキーで開くクラフト専用パネル")]
    public GameObject craftPanel;

    [Tooltip("クラフト画面の建物リスト親 (VerticalLayoutGroup など)")]
    public Transform craftScreenListRoot;

    [Tooltip("クラフト画面用の1行プレハブ (Icon + Label + Button)")]
    public GameObject craftScreenRowPrefab;

    // Qキー側（クラフトパネル）の建物リスト
    List<CraftRowCache> _craftScreenRows = new();

    [Header("BuildPlacement 参照 (ゴーストプレビュー用)")]
    [Tooltip("建物のゴーストプレビューを出す BuildPlacement。未設定なら自動検索します。")]
    public BuildPlacement buildPlacement;

    CanvasGroup _canvasGroup;
    bool _wantVisible = false;   // インベントリパネル
    bool _isFading = false;
    float _fadeTime = 0f;
    float _fadeStartAlpha = 0f;
    float _fadeTargetAlpha = 0f;
    float _refreshTimer = 0f;

    // クラフトパネル側フラグ（フェードは付けずに ON/OFF のみ）
    bool _craftVisible = false;

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

        if (craftPanel != null)
        {
            craftPanel.SetActive(false);
            _craftVisible = false;
        }

        if (buildPlacement == null)
        {
            buildPlacement = FindFirstObjectByType<BuildPlacement>();
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

        // 範囲選択メニューが開かれていたら、両方のパネルを自動で閉じる
        if (selectionMenuOpen)
        {
            if (_wantVisible)
                HidePanel();
            if (_craftVisible)
                HideCraftPanel();
        }

        // 範囲選択メニューが開いている間は、E/Qキーでの操作は無効
        if (!selectionMenuOpen)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.eKey.wasPressedThisFrame)
                {
                    TogglePanel();
                }
                if (kb.qKey.wasPressedThisFrame)
                {
                    ToggleCraftPanel();
                }
            }
#else
            if (Input.GetKeyDown(KeyCode.E))
            {
                TogglePanel();
            }
            if (Input.GetKeyDown(KeyCode.Q))
            {
                ToggleCraftPanel();
            }
#endif
        }

        // インベントリパネル側フェード進行
        UpdateFade();

        // インベントリパネルが開いている間は定期的にリスト更新
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

        // クラフトパネルが開いている間も、内容を都度更新しておく
        if (craftPanel != null && craftPanel.activeSelf && _craftVisible)
        {
            RefreshCraftScreenList();
        }
    }

    // Eキー：インベントリパネル
    void TogglePanel()
    {
        if (panel == null || listRoot == null || rowPrefab == null)
            return;

        if (!_wantVisible)
        {
            ShowPanel();
        }
        else
        {
            HidePanel();
        }
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
        float t = Mathf.Clamp01(_fadeTime / fadeDuration);
        _canvasGroup.alpha = Mathf.Lerp(_fadeStartAlpha, _fadeTargetAlpha, t);

        if (t >= 1f)
        {
            _isFading = false;
            if (_fadeTargetAlpha <= 0f)
            {
                panel.SetActive(false);
            }
        }
    }

    // Qキー：クラフトパネル
    void ToggleCraftPanel()
    {
        if (craftPanel == null)
            return;

        if (_craftVisible)
        {
            HideCraftPanel();
        }
        else
        {
            ShowCraftPanel();
        }
    }

    void ShowCraftPanel()
    {
        if (craftPanel == null)
            return;

        _craftVisible = true;
        craftPanel.SetActive(true);

        RefreshCraftScreenList();
    }

    void HideCraftPanel()
    {
        if (craftPanel == null)
            return;

        _craftVisible = false;
        craftPanel.SetActive(false);
    }

    // インベントリパネル内のアイテム＋建物ボタン
    void RefreshList()
    {
        if (listRoot == null || rowPrefab == null)
            return;

        // アイテム行を全部削除
        for (int i = listRoot.childCount - 1; i >= 0; i--)
        {
            Destroy(listRoot.GetChild(i).gameObject);
        }

        var dm = DroneBuildManager.Instance;
        if (dm == null)
            return;

        var inv = dm.GlobalInventory;
        if (inv == null || inv.Count == 0)
        {
            // 何もないときでも建物リストは更新しておく
            RefreshInventoryBuildList();
            return;
        }

        // ソートして表示
        var sorted = new List<KeyValuePair<string, int>>(inv);
        sorted.Sort((a, b) => string.Compare(a.Key, b.Key, System.StringComparison.Ordinal));

        foreach (var kv in sorted)
        {
            string itemName = kv.Key;
            int count = kv.Value;

            // ★ 所持数 0 のアイテムは表示しない
            if (count <= 0)
                continue;

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
                // 個数だけを表示
                txt.text = count.ToString();
            }
        }

        // インベントリ内の建物ボタン（ゴースト選択用）も更新
        RefreshInventoryBuildList();
    }

    // Eキー側：インベントリパネル内の建物ボタン（ゴースト選択）
    void RefreshInventoryBuildList()
    {
        if (craftListRoot == null || craftRowPrefab == null)
            return;

        var dm = DroneBuildManager.Instance;
        if (dm == null)
            return;

        bool needRebuild = (_inventoryBuildRows.Count != craftableBuildings.Count) ||
                           (craftListRoot.childCount != craftableBuildings.Count);

        if (needRebuild)
        {
            // 古い行を全部削除
            for (int i = craftListRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(craftListRoot.GetChild(i).gameObject);
            }
            _inventoryBuildRows.Clear();

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

                if (cache.button != null)
                {
                    var capturedDef = def;
                    cache.button.onClick.RemoveAllListeners();
                    // インベントリ側はゴースト選択
                    cache.button.onClick.AddListener(() => OnClickSelectBuilding(capturedDef));
                }

                _inventoryBuildRows.Add(cache);
            }
        }

        // 内容の更新だけ
        for (int i = 0; i < _inventoryBuildRows.Count && i < craftableBuildings.Count; i++)
        {
            var cache = _inventoryBuildRows[i];
            var def = craftableBuildings[i];
            if (def == null || cache.root == null) continue;

            cache.def = def;

            // ★ ここで dm を再定義しない（既に上で定義済み）
            if (dm == null) break;

            // 所持しているキット数
            int kitCount = dm.GetCraftedBuildingCount(def);
            bool hasAny = kitCount > 0;

            // ★ 所持数 0 の建物ボタンはインベントリでは非表示にする
            cache.root.SetActive(hasAny);
            if (!hasAny)
                continue;

            if (cache.icon != null)
            {
                cache.icon.sprite = def.icon;
                cache.icon.enabled = (cache.icon.sprite != null);
            }

            if (cache.label != null)
            {
                cache.label.text = kitCount.ToString();
            }

            if (cache.button != null)
            {
                cache.button.interactable = true;
            }
        }
    }

    // Qキー側：クラフト専用パネル内の建物ボタン
    void RefreshCraftScreenList()
    {
        if (craftScreenListRoot == null || craftScreenRowPrefab == null)
            return;

        var dm = DroneBuildManager.Instance;
        if (dm == null)
            return;

        bool needRebuild = (_craftScreenRows.Count != craftableBuildings.Count) ||
                           (craftScreenListRoot.childCount != craftableBuildings.Count);

        if (needRebuild)
        {
            for (int i = craftScreenListRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(craftScreenListRoot.GetChild(i).gameObject);
            }
            _craftScreenRows.Clear();

            foreach (var def in craftableBuildings)
            {
                if (def == null) continue;

                var rowGO = Instantiate(craftScreenRowPrefab, craftScreenListRoot);

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

                if (cache.button != null)
                {
                    var capturedDef = def;
                    cache.button.onClick.RemoveAllListeners();
                    cache.button.onClick.AddListener(() => OnClickCraftBuilding(capturedDef));
                }

                _craftScreenRows.Add(cache);
            }
        }

        // ★ここから各行の内容更新（Qパネルは「作れる数」を表示）
        var globalInv = dm.GlobalInventory;   // 全体在庫【素材】

        for (int i = 0; i < _craftScreenRows.Count && i < craftableBuildings.Count; i++)
        {
            var cache = _craftScreenRows[i];
            var def = craftableBuildings[i];
            if (def == null || cache.root == null) continue;

            cache.def = def;

            if (cache.icon != null)
            {
                cache.icon.sprite = def.icon;
                cache.icon.enabled = (cache.icon.sprite != null);
            }

            // --- 作れる数を計算 ---
            int craftableCount = 0;

            if (def.buildCosts != null && def.buildCosts.Count > 0)
            {
                craftableCount = int.MaxValue;

                foreach (var cost in def.buildCosts)
                {
                    if (cost == null) continue;
                    if (string.IsNullOrEmpty(cost.itemName)) continue;
                    if (cost.amount <= 0) continue;

                    int have = 0;
                    globalInv.TryGetValue(cost.itemName, out have);

                    // この素材だけ見たときの最大クラフト数
                    int byThisItem = have / cost.amount;
                    craftableCount = Mathf.Min(craftableCount, byThisItem);
                }

                if (craftableCount == int.MaxValue)
                    craftableCount = 0;
            }

            bool canCraft = (craftableCount > 0);

            if (cache.label != null)
            {
                // ★Qパネルでは「作れる数」を表示
                cache.label.text = craftableCount.ToString();
            }

            if (cache.button != null)
            {
                cache.button.interactable = canCraft;
            }
        }
    }

    // インベントリ側：建物ボタン → ゴースト選択
    void OnClickSelectBuilding(BuildingDef def)
    {
        if (def == null) return;

        if (buildPlacement == null)
        {
            buildPlacement = FindFirstObjectByType<BuildPlacement>();
            if (buildPlacement == null)
            {
                Debug.LogWarning("[InventoryUI] BuildPlacement が見つからないため、ゴーストプレビューを出せません。");
                return;
            }
        }

        // ★ インベントリから選んだときも「建築モード開始」扱いにしてロック解除する
        SelectionBoxDrawer.NotifyBuildModeStartedFromOutside();

        // 選択した建物を建築対象にセット（BuildBarUI.Select と同じ流れ）
        buildPlacement.SetSelected(def);

        // ゴースト選択後はインベントリパネルを閉じておく
        HidePanel();
    }

    // クラフト画面：建物ボタン → キットをクラフト
    void OnClickCraftBuilding(BuildingDef def)
    {
        var dm = DroneBuildManager.Instance;
        if (dm == null || def == null) return;

        if (!dm.TryCraftBuilding(def, 1))
        {
            Debug.Log($"[InventoryUI] {def.displayName} をクラフトできませんでした（素材不足？）");
            return;
        }

        // 素材在庫 & キット在庫が変わるので、両方のリストを更新
        RefreshList();
        RefreshCraftScreenList();
    }

    Sprite FindIcon(string itemName)
    {
        if (string.IsNullOrEmpty(itemName)) return null;
        if (_iconLookup.TryGetValue(itemName, out var sp))
            return sp;
        return null;
    }
}
