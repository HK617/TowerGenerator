using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SaveLoadManager : MonoBehaviour
{
    public static SaveLoadManager Instance { get; private set; }

    public BuildPlacement placement;
    public DroneBuildManager droneManager;

    public BuildingDef[] knownDefs;

    public string defaultSlot = "save";
    [SerializeField] string currentSlot = "save";

    const string PLAYERPREFS_KEY_LASTSLOT = "LastSaveSlot";

    string pendingLoadSlot = null;

    // ← 1シーン構成なのでここは空でOK（Inspectorでも空にしておく）
    public string gameSceneName = "";

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        string last = PlayerPrefs.GetString(PLAYERPREFS_KEY_LASTSLOT, "");
        currentSlot = string.IsNullOrEmpty(last) ? defaultSlot : last;
    }

    // =====================================================
    // タイトルから呼ぶ
    // =====================================================
    public void StartGameAndLoad(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            slot = defaultSlot;

        pendingLoadSlot = slot.Trim();
        SetSlot(pendingLoadSlot);

        // タイトルを閉じて GameRoot を開く
        var startUI = FindFirstObjectByType<StartMenuUI>();
        if (startUI != null)
        {
            if (startUI.startPanel) startUI.startPanel.SetActive(false);
            if (startUI.gameRoot) startUI.gameRoot.SetActive(true);
        }

        // ★ここがポイント★
        // GameRootを有効化した「あとのフレーム」でロードする
        StartCoroutine(LoadAfterOneFrame(pendingLoadSlot));
    }

    IEnumerator LoadAfterOneFrame(string slot)
    {
        // 1フレームまつ → GameRoot配下の Start() が全部終わる
        yield return null;

        // 念のためもう1フレーム待ちたいならもう1回 yield return null; してもいい
        // yield return null;

        // Game内の参照を取り直す
        if (!placement) placement = FindFirstObjectByType<BuildPlacement>();
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();

        // ここで本ロード
        LoadFrom(slot);
        pendingLoadSlot = null;
    }

    // =========================================================
    //  公開API：スロットの切替
    // =========================================================
    public void SetSlot(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            return;

        currentSlot = slotName.Trim();
        PlayerPrefs.SetString(PLAYERPREFS_KEY_LASTSLOT, currentSlot);
        PlayerPrefs.Save();
        Debug.Log("[SaveLoadManager] Slot changed to: " + currentSlot + " (" + MakePath(currentSlot) + ")");
    }

    public string GetCurrentSlot() => currentSlot;

    // =========================================================
    //  通常の Save / Load （ゲーム中）
    // =========================================================
    public void Save()
    {
        SaveTo(currentSlot);
    }

    public void Load()
    {
        LoadFrom(currentSlot);
    }

    public void SaveTo(string slotName)
    {
        // タイトルでこれを呼んでも refs が無いので弾く
        if (!EnsureGameRefs()) return;

        string path = MakePath(slotName);

        var data = new SaveData();

        // 全体
        data.baseBuilt = BuildPlacement.s_baseBuilt;

        // 建物
        var placed = placement.CollectForSave();
        foreach (var b in placed)
        {
            data.buildings.Add(new PlacedBuildingData
            {
                defName = b.defName,
                position = b.position,
                fine = b.isFine,
                isBase = b.isBase
            });
        }

        // ドローン
        data.queuedTasks = droneManager.GetQueuedTasksForSave();
        data.drones = droneManager.GetRuntimeForSave();

        var json = JsonUtility.ToJson(data, true);
        File.WriteAllText(path, json);
        Debug.Log("[SaveLoadManager] Saved to: " + path);

        SetSlot(slotName);
    }

    public void LoadFrom(string slotName)
    {
        if (!EnsureGameRefs()) return;

        string path = MakePath(slotName);
        if (!File.Exists(path))
        {
            Debug.LogWarning("[SaveLoadManager] no save file: " + path);
            return;
        }

        string json = File.ReadAllText(path);
        var data = JsonUtility.FromJson<SaveData>(json);
        if (data == null)
        {
            Debug.LogWarning("[SaveLoadManager] load failed (json null) from: " + path);
            return;
        }

        // 全消し
        placement.ClearAllPlaced();

        // Def 一覧
        BuildingDef[] allDefs = (knownDefs != null && knownDefs.Length > 0)
            ? knownDefs
            : Resources.LoadAll<BuildingDef>("");

        BuildingDef FindDef(string name)
        {
            if (string.IsNullOrEmpty(name) || allDefs == null) return null;

            var d1 = allDefs.FirstOrDefault(d => d && d.displayName == name);
            if (d1 != null) return d1;

            var d2 = allDefs.FirstOrDefault(d => d && d.prefab != null && d.prefab.name == name);
            if (d2 != null) return d2;

            var d3 = allDefs.FirstOrDefault(d => d && d.name == name);
            if (d3 != null) return d3;

            Debug.LogWarning("[SaveLoadManager] BuildingDef not found for name: " + name);
            return null;
        }

        foreach (var b in data.buildings)
        {
            var def = FindDef(b.defName);
            if (def == null) continue;

            placement.RestoreBuilding(def, b.position, b.fine, b.isBase);
        }

        BuildPlacement.s_baseBuilt = data.baseBuilt;

        droneManager.RestoreFromSave(
            data.queuedTasks,
            data.drones,
            placement,
            FindDef
        );

        Debug.Log("[SaveLoadManager] Loaded from: " + path);
        SetSlot(slotName);
    }

    // =========================================================
    //  保存先一覧（タイトルで使う）
    // =========================================================
    public string[] ListSaveFiles()
    {
        string dir = Application.persistentDataPath;
        if (!Directory.Exists(dir)) return new string[0];

        var files = Directory.GetFiles(dir, "*.json");
        for (int i = 0; i < files.Length; i++)
            files[i] = Path.GetFileName(files[i]);
        return files;
    }

    // =========================================================
    //  内部ヘルパー
    // =========================================================
    string MakePath(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            slotName = "save";

        slotName = slotName.Trim();

        if (!slotName.EndsWith(".json"))
            slotName += ".json";

        return Path.Combine(Application.persistentDataPath, slotName);
    }

    bool EnsureGameRefs()
    {
        if (!placement) placement = FindFirstObjectByType<BuildPlacement>();
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();

        if (!placement || !droneManager)
        {
            Debug.LogWarning("[SaveLoadManager] game refs not found yet. Are you in title scene?");
            return false;
        }
        return true;
    }
}
