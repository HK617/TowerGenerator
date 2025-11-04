using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

[DefaultExecutionOrder(-200)]
public class SaveLoadManager : MonoBehaviour
{
    public static SaveLoadManager Instance { get; private set; }

    // ─────────────────────────────
    // ゲーム内で参照するオブジェクト（Gameシーン側にある）
    // ─────────────────────────────
    [Header("Game References")]
    public BuildPlacement placement;
    public DroneBuildManager droneManager;
    public BuildingDef[] knownDefs;

    // ─────────────────────────────
    // シーン名（インスペクターで変えられるようにする）
    // ─────────────────────────────
    [Header("Scene Names")]
    public string titleSceneName = "Boot";   // タイトル用シーン
    public string gameSceneName = "Game";   // ゲーム用シーン

    const string PLAYERPREFS_KEY_LASTSLOT = "LastSaveSlot";

    // いま選ばれているスロット
    string currentSlot = "";

    // 「このあとゲームシーンでこのスロットをロードしてね」という一時保存
    string pendingLoadSlot = null;

    // 「このあとゲームシーンでNewGameしてね」というフラグ
    bool pendingNewGame = false;

    void Awake()
    {
        // シングルトン化
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // 前回使っていたスロット名を復元
        string last = PlayerPrefs.GetString(PLAYERPREFS_KEY_LASTSLOT, "");
        if (!string.IsNullOrEmpty(last))
            currentSlot = last;

        // シーンロードを監視する
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // =====================================================
    // タイトルから「このセーブで始める」→ゲームシーンを開く
    // =====================================================
    public void StartGameAndLoad(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            slot = "save";

        pendingLoadSlot = slot.Trim();
        pendingNewGame = false;

        SetSlot(pendingLoadSlot);

        // ゲームシーンをロードする。これでGameシーンのオブジェクトは全部新しくなる
        SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
    }

    // =====================================================
    // タイトルから「New Game」→ゲームシーンを開く
    // =====================================================
    public void StartNewGame()
    {
        string newSlot = GenerateNewSlotName();
        SetSlot(newSlot);

        pendingLoadSlot = newSlot;
        pendingNewGame = true;   //ロードではなく新規作成フラグを立てる

        SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
    }

    // =====================================================
    // シーンがロードされたら呼ばれる
    // =====================================================
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // ゲームシーンが開いたときだけ処理する
        if (scene.name == gameSceneName)
        {
            // ゲームシーン内の各コンポーネントが Awake / Start し終わってからロードしたいので1フレーム待つ
            StartCoroutine(LoadGameSceneAfterOneFrame());
        }
    }

    IEnumerator LoadGameSceneAfterOneFrame()
    {
        // ゲームシーンのオブジェクトが完全に出揃うまで少し待つ
        yield return null;
        yield return null; // ← 追加でもう1フレーム待つ

        // Gameシーン内の参照を拾う
        if (!placement) placement = FindFirstObjectByType<BuildPlacement>();
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();

        // ここで念のため再チェック
        if (!EnsureGameRefs())
        {
            Debug.LogWarning("[SaveLoadManager] Placement/DroneManager not ready yet.");
            yield break;
        }

        if (pendingNewGame)
        {
            Debug.Log("[SaveLoadManager] Starting NewGame: create empty world");
            CreateEmptyWorldState();
            SaveTo(currentSlot);
        }
        else if (!string.IsNullOrEmpty(pendingLoadSlot))
        {
            Debug.Log($"[SaveLoadManager] Loading slot: {pendingLoadSlot}");
            LoadFrom(pendingLoadSlot);
        }

        pendingLoadSlot = null;
        pendingNewGame = false;
    }

    // =====================================================
    // ESCなどでタイトルに戻る（Gameシーンをまるごと破棄してBootを開く）
    // =====================================================
    public void ReturnToTitle()
    {
        SceneManager.LoadScene(titleSceneName, LoadSceneMode.Single);
    }

    // =====================================================
    // 通常のセーブ・ロード
    // =====================================================
    public void Save() => SaveTo(currentSlot);
    public void Load() => LoadFrom(currentSlot);

    public void SaveTo(string slotName)
    {
        if (!EnsureGameRefs()) return;

        string path = MakePath(slotName);
        var data = new SaveData();

        // 建物
        data.baseBuilt = BuildPlacement.s_baseBuilt;

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

        // 資源オブジェクト(ResourceMarker付き)をセーブ
        var resourceMarkers = FindObjectsOfType<ResourceMarker>();
        foreach (var r in resourceMarkers)
        {
            if (r == null || r.def == null) continue;

            // BuildingDef から一意になりそうな名前を選ぶ
            string defName;
            if (r.def.prefab != null)
                defName = r.def.prefab.name;
            else if (!string.IsNullOrEmpty(r.def.displayName))
                defName = r.def.displayName;
            else
                defName = r.def.name;

            data.resources.Add(new ResourceData
            {
                defName = defName,
                position = r.transform.position
            });
        }

        // ドローンのキューと実行中
        if (droneManager != null)
        {
            data.queuedTasks = droneManager.GetQueuedTasksForSave();
            data.drones = droneManager.GetRuntimeForSave();
        }

        // JSONにして書き込む
        var json = JsonUtility.ToJson(data, true);
        File.WriteAllText(path, json);
        Debug.Log($"[SaveLoadManager] Saved to: {path}");

        SetSlot(slotName);
    }

    public void LoadFrom(string slotName)
    {
        if (!EnsureGameRefs()) return;

        string path = MakePath(slotName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[SaveLoadManager] No save file: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        var data = JsonUtility.FromJson<SaveData>(json);
        if (data == null)
        {
            Debug.LogWarning($"[SaveLoadManager] Load failed (json null) from: {path}");
            return;
        }

        // まず現在のワールドを空にする
        placement.ClearAllPlaced();
        BuildPlacement.s_baseBuilt = false;

        // 既存の資源オブジェクト(ResourceMarker付き)も削除
        var existingResources = FindObjectsOfType<ResourceMarker>();
        foreach (var r in existingResources)
        {
            if (!r) continue;
#if UNITY_EDITOR
        if (!Application.isPlaying)
            DestroyImmediate(r.gameObject);
        else
            Destroy(r.gameObject);
#else
            Destroy(r.gameObject);
#endif
        }

        // BuildingDefをまとめておく（Inspectorに並べてあるならそれを優先）
        BuildingDef[] allDefs = (knownDefs != null && knownDefs.Length > 0)
            ? knownDefs
            : Resources.LoadAll<BuildingDef>("");

        BuildingDef FindDef(string name)
        {
            if (string.IsNullOrEmpty(name) || allDefs == null) return null;

            // displayName → prefab name → asset name の順で探す
            var d1 = allDefs.FirstOrDefault(d => d && d.displayName == name);
            if (d1 != null) return d1;

            var d2 = allDefs.FirstOrDefault(d => d && d.prefab != null && d.prefab.name == name);
            if (d2 != null) return d2;

            var d3 = allDefs.FirstOrDefault(d => d && d.name == name);
            if (d3 != null) return d3;

            Debug.LogWarning($"[SaveLoadManager] BuildingDef not found: {name}");
            return null;
        }

        // 建物を復元
        foreach (var b in data.buildings)
        {
            var def = FindDef(b.defName);
            if (def == null) continue;

            placement.RestoreBuilding(def, b.position, b.fine, b.isBase);
        }

        // 資源を復元
        if (data.resources != null)
        {
            foreach (var r in data.resources)
            {
                var def = FindDef(r.defName);
                if (def == null || def.prefab == null) continue;

                var obj = Instantiate(def.prefab, r.position, Quaternion.identity);
                var marker = obj.GetComponent<ResourceMarker>();
                if (!marker) marker = obj.AddComponent<ResourceMarker>();
                marker.def = def;
            }
        }

        // Baseを建てたかどうか
        BuildPlacement.s_baseBuilt = data.baseBuilt;

        // ドローンも復元
        if (droneManager != null)
        {
            droneManager.RestoreFromSave(
                data.queuedTasks,
                data.drones,
                placement,
                (name) => FindDef(name)
            );
        }

        Debug.Log($"[SaveLoadManager] Loaded from: {path}");
    }

    // =====================================================
    // 保存済みファイル一覧（タイトルUI用）
    // =====================================================
    public string[] ListSaveFiles()
    {
        string dir = Application.persistentDataPath;
        if (!Directory.Exists(dir)) return new string[0];

        var files = Directory.GetFiles(dir, "*.json");
        for (int i = 0; i < files.Length; i++)
            files[i] = Path.GetFileName(files[i]);
        return files;
    }

    public void DeleteSave(string slotName)
    {
        string path = MakePath(slotName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public void DeleteAllSaves()
    {
        string dir = Application.persistentDataPath;
        if (!Directory.Exists(dir)) return;

        var files = Directory.GetFiles(dir, "*.json");
        foreach (var f in files)
            File.Delete(f);
    }

    // =====================================================
    // 内部ユーティリティ
    // =====================================================
    public void SetSlot(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            return;

        currentSlot = slotName.Trim();
        PlayerPrefs.SetString(PLAYERPREFS_KEY_LASTSLOT, currentSlot);
        PlayerPrefs.Save();
    }

    string MakePath(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            slotName = "save";
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
            Debug.LogWarning("[SaveLoadManager] game refs not found yet.");
            return false;
        }
        return true;
    }

    string GenerateNewSlotName()
    {
        // "save", "save_001", "save_002", ... のように増やす
        if (!File.Exists(MakePath("save")))
            return "save";

        int idx = 1;
        while (true)
        {
            string candidate = $"save_{idx:000}";
            if (!File.Exists(MakePath(candidate)))
                return candidate;
            idx++;
        }
    }

    // いちおう残しておく（同じシーン運用のときに使ったやつ）
    public void ResetCurrentWorld()
    {
        CreateEmptyWorldState();
    }

    // ゲーム中の建物・ドローンだけを空にする既存の処理
    void CreateEmptyWorldState()
    {
        if (!EnsureGameRefs()) return;

        placement.ClearAllPlaced();
        BuildPlacement.s_baseBuilt = false;

        if (droneManager != null)
        {
            droneManager.RestoreFromSave(
                new System.Collections.Generic.List<DroneTaskData>(),
                new System.Collections.Generic.List<DroneRuntimeData>(),
                placement,
                _ => null
            );
        }
    }
}
