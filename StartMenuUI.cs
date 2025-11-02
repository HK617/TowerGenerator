using UnityEngine;
using UnityEngine.UI;

public class StartMenuUI : MonoBehaviour
{
    [Header("UI")]
    public GameObject startPanel;      // タイトル全体
    public GameObject gameRoot;        // ゲーム本体
    public Button newGameButton;       // 「NewGame」ボタン

    [Header("Save List")]
    [Tooltip("Scroll View の中の Content を入れる")]
    public Transform savesContainer;   // ここに行を並べる

    [Tooltip("ロードボタンと削除ボタンが乗っている1行用プレハブ")]
    public GameObject saveSlotPrefab;  // ← Buttonじゃなくて GameObject にしました

    [Header("Base spawn (optional)")]
    public GameObject basePrefab;
    bool baseSpawned = false;

    [Header("Maintenance (optional)")]
    public Button deleteAllButton;     // 全削除ボタン（あれば）

    void Awake()
    {
        if (startPanel) startPanel.SetActive(true);
        if (gameRoot) gameRoot.SetActive(false);

        if (newGameButton)
            newGameButton.onClick.AddListener(OnClickNewGame);

        if (deleteAllButton)
            deleteAllButton.onClick.AddListener(OnClickDeleteAll);

        // まず1回
        RefreshSaveButtons();

        // もしこの時点でSaveLoadManagerがまだなら次フレームでもう1回
        if (SaveLoadManager.Instance == null)
            StartCoroutine(RefreshNextFrame());
    }

    System.Collections.IEnumerator RefreshNextFrame()
    {
        yield return null;
        RefreshSaveButtons();
    }

    void OnDestroy()
    {
        if (newGameButton)
            newGameButton.onClick.RemoveListener(OnClickNewGame);
        if (deleteAllButton)
            deleteAllButton.onClick.RemoveListener(OnClickDeleteAll);
    }

    void OnClickNewGame()
    {
        if (SaveLoadManager.Instance != null)
            SaveLoadManager.Instance.StartNewGame();
    }

    void OnClickDeleteAll()
    {
        if (SaveLoadManager.Instance != null)
        {
            SaveLoadManager.Instance.DeleteAllSaves();
            RefreshSaveButtons();
        }
    }

    // ─────────────────────────────
    // ここがポイント：1行ずつ生成
    // ─────────────────────────────
    public void RefreshSaveButtons()
    {
        Debug.Log("[StartMenuUI] RefreshSaveButtons() called");

        if (savesContainer == null)
        {
            Debug.LogWarning("[StartMenuUI] savesContainer not set");
            return;
        }
        if (saveSlotPrefab == null)
        {
            Debug.LogWarning("[StartMenuUI] saveSlotPrefab not set");
            return;
        }
        if (SaveLoadManager.Instance == null)
        {
            Debug.LogWarning("[StartMenuUI] SaveLoadManager not found");
            return;
        }

        // 古い行を全部消す
        for (int i = savesContainer.childCount - 1; i >= 0; i--)
            Destroy(savesContainer.GetChild(i).gameObject);

        string[] files = SaveLoadManager.Instance.ListSaveFiles();
        Debug.Log("[StartMenuUI] found " + files.Length + " save files.");

        foreach (var f in files)
        {
            // "save_001.json" → "save_001"
            string slotName = f.EndsWith(".json") ? f[..^5] : f;

            // 行を生成（親は必ずシーン上のContent）
            var rowGO = Instantiate(saveSlotPrefab, savesContainer);
            rowGO.name = "SaveSlot_" + slotName;

            // 子を探す
            var loadBtnTr = rowGO.transform.Find("LoadButton");
            var deleteBtnTr = rowGO.transform.Find("DeleteButton");

            Button loadBtn = loadBtnTr ? loadBtnTr.GetComponent<Button>() : null;
            Button deleteBtn = deleteBtnTr ? deleteBtnTr.GetComponent<Button>() : null;

            // ラベルは LoadButton の中にある想定
            var tmp = loadBtn ? loadBtn.GetComponentInChildren<TMPro.TMP_Text>() : null;
            var uiTxt = (!tmp && loadBtn) ? loadBtn.GetComponentInChildren<Text>() : null;

            if (tmp) tmp.text = slotName;
            else if (uiTxt) uiTxt.text = slotName;

            // ロード処理
            if (loadBtn != null)
            {
                loadBtn.onClick.AddListener(() =>
                {
                    SaveLoadManager.Instance.StartGameAndLoad(slotName);
                });
            }

            // 削除処理
            if (deleteBtn != null)
            {
                deleteBtn.onClick.AddListener(() =>
                {
                    SaveLoadManager.Instance.DeleteSave(slotName);
                    RefreshSaveButtons();   // 削除後にリストを描き直す
                });
            }
        }
    }

    // BuildPlacement から呼ばれる互換API
    public bool TrySpawnBaseAt(Vector3 worldCenter)
    {
        if (baseSpawned) return false;

        if (basePrefab == null)
        {
            Debug.LogWarning("[StartMenuUI] basePrefab が未割り当てです。");
            return false;
        }

        var go = Instantiate(basePrefab);
        go.name = "Base";
        go.transform.position = worldCenter;

        baseSpawned = true;
        return true;
    }
}
