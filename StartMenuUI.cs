using UnityEngine;
using UnityEngine.UI;

public class StartMenuUI : MonoBehaviour
{
    [Header("UI")]
    public GameObject startPanel;      // タイトル全体
    public Button newGameButton;       // 「NewGame」ボタン

    [Header("Save List")]
    [Tooltip("Scroll View の中の Content を入れる")]
    public Transform savesContainer;   // ここに行を並べる

    [Tooltip("ロードボタンと削除ボタンが乗っている1行用プレハブ")]
    public GameObject saveSlotPrefab;  // 1行ぶんのプレハブ

    [Header("Base spawn (optional)")]
    public GameObject basePrefab;
    bool baseSpawned = false;

    [Header("Maintenance (optional)")]
    public Button deleteAllButton;     // 全削除ボタン（あれば）

    void Awake()
    {
        if (startPanel) startPanel.SetActive(true);

        if (newGameButton)
            newGameButton.onClick.AddListener(OnClickNewGame);

        if (deleteAllButton)
            deleteAllButton.onClick.AddListener(OnClickDeleteAll);

        RefreshSaveButtons();

        // SaveLoadManagerが起きるのが1フレーム遅いとき用
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
    // セーブリストを作り直す
    // ─────────────────────────────
    public void RefreshSaveButtons()
    {
        if (savesContainer == null) return;
        if (saveSlotPrefab == null) return;
        if (SaveLoadManager.Instance == null) return;

        // 古い行を全部消す
        for (int i = savesContainer.childCount - 1; i >= 0; i--)
            Destroy(savesContainer.GetChild(i).gameObject);

        string[] files = SaveLoadManager.Instance.ListSaveFiles();

        foreach (var f in files)
        {
            // "save_001.json" → "save_001"
            string slotName = f.EndsWith(".json") ? f[..^5] : f;

            var rowGO = Instantiate(saveSlotPrefab, savesContainer);
            rowGO.name = "SaveSlot_" + slotName;

            var loadBtnTr = rowGO.transform.Find("LoadButton");
            var deleteBtnTr = rowGO.transform.Find("DeleteButton");

            Button loadBtn = loadBtnTr ? loadBtnTr.GetComponent<Button>() : null;
            Button deleteBtn = deleteBtnTr ? deleteBtnTr.GetComponent<Button>() : null;

            // ラベルをつける
            var tmp = loadBtn ? loadBtn.GetComponentInChildren<TMPro.TMP_Text>() : null;
            var uiTxt = (!tmp && loadBtn) ? loadBtn.GetComponentInChildren<Text>() : null;

            if (tmp) tmp.text = slotName;
            else if (uiTxt) uiTxt.text = slotName;

            // ロードボタン
            if (loadBtn != null)
            {
                loadBtn.onClick.AddListener(() =>
                {
                    SaveLoadManager.Instance.StartGameAndLoad(slotName);
                });
            }

            // 削除ボタン
            if (deleteBtn != null)
            {
                deleteBtn.onClick.AddListener(() =>
                {
                    SaveLoadManager.Instance.DeleteSave(slotName);
                    RefreshSaveButtons();
                });
            }
        }
    }

    // BuildPlacementからの古い呼び出し用に残しておく
    public bool TrySpawnBaseAt(Vector3 worldCenter)
    {
        if (baseSpawned) return false;
        if (basePrefab == null) return false;

        var go = Instantiate(basePrefab);
        go.name = "Base";
        go.transform.position = worldCenter;

        baseSpawned = true;
        return true;
    }

    // タイトルに戻ったときにまたBaseを出せるようにしたければこれを呼ぶ
    public void ResetBaseSpawnFlag()
    {
        baseSpawned = false;
    }
}
