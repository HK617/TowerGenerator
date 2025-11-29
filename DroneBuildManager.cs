
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ① 起動時に固定数のドローンを生成して持っておく
/// ② 建築タスクが来たら、Idleなドローンに渡す（いなければキューにためる）
/// ③ ドローンが終わったらIdleに戻す
/// ④ 左UIには「存在しているドローンたち」を常に送る
/// ⑤ セーブ/ロードに対応
/// 
/// ※ この版では「キューできる数の上限」をなくしています。
///    何個でもためられます。
/// </summary>
public class DroneBuildManager : MonoBehaviour
{
    public static DroneBuildManager Instance { get; private set; }

    [Header("Fixed drone pool")]
    public int initialDroneCount = 3;
    public DroneWorker dronePrefab;
    public Transform droneSpawnPoint;

    [Header("Base")]
    public Transform baseTransform;

    // ─────────────────────────────────────────────
    // 以前あった「maxQueuedTasks」は削除しました
    // ─────────────────────────────────────────────

    // タスク（建築依頼）
    public enum TaskKind
    {
        BigBuild,
        FineBuild,
        BigDemolish,
        FineDemolish,
        MineResource,   // ★ 追加
    }

    [Serializable]
    public class BuildTask
    {
        public TaskKind kind;
        public BuildPlacement placer;
        public BuildingDef def;       // Build のときだけ使用
        public GameObject ghost;      // Build のときだけ使用

        public Vector3Int bigCell;
        public Vector3 worldPos;
        public Vector2Int fineCell;

        public GameObject targetToDemolish;  // ★ 解体対象

        // ★ 追加：採掘対象の Resource
        public ResourceMarker resourceMarker;
    }

    // 旧コード互換
    [Serializable]
    public class BuildJob : BuildTask { }

    // ← ここにタスクをためる。上限なし
    readonly Queue<BuildTask> _queue = new Queue<BuildTask>();
    readonly List<DroneWorker> _drones = new List<DroneWorker>();

    public event Action<List<DroneWorker>, int> OnDroneStateChanged;

    // ★ この ResourceMarker に対する採掘ジョブが
    //    ・キューに入っている
    //    ・または、ドローンが現在実行中
    // のどちらかなら true を返す
    public bool IsResourceInMiningQueue(ResourceMarker marker)
    {
        if (marker == null) return false;

        // 1) キューにあるタスクをチェック
        foreach (var t in _queue)
        {
            if (t.kind == TaskKind.MineResource && t.resourceMarker == marker)
                return true;
        }

        // 2) すでにドローンが実行中のタスクもチェック
        foreach (var d in _drones)
        {
            var cur = d.CurrentTask;
            if (cur != null &&
                cur.kind == TaskKind.MineResource &&
                cur.resourceMarker == marker)
            {
                return true;
            }
        }

        return false;
    }

    // ★ 追加：指定の ResourceMarker に対する採掘ターゲット座標をすべて取得
    public bool TryGetMiningTargets(ResourceMarker marker, List<Vector3> results)
    {
        if (marker == null)
            return false;

        results.Clear();

        // 1) キューに溜まっている MineResource
        foreach (var t in _queue)
        {
            if (t.kind == TaskKind.MineResource && t.resourceMarker == marker)
            {
                results.Add(t.worldPos);
            }
        }

        // 2) すでにドローンが実行中の MineResource も追加
        foreach (var d in _drones)
        {
            var cur = d.CurrentTask;
            if (cur != null &&
                cur.kind == TaskKind.MineResource &&
                cur.resourceMarker == marker)
            {
                results.Add(cur.worldPos);
            }
        }

        return results.Count > 0;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        SpawnInitialDrones();
        NotifyUI();
    }

    void Update()
    {
        TryDispatchTasks();

        //ドローンの位置や進捗を常に UI に反映させる
        NotifyUI();
    }

    /// <summary>
    /// ゲーム中の Base を登録する（建設 / ロード直後に呼び出す）
    /// </summary>
    public void RegisterBase(Transform baseTr)
    {
        baseTransform = baseTr;

        // 既に存在しているドローンにも教える
        foreach (var d in _drones)
        {
            if (d != null)
            {
                d.baseTransform = baseTr;
            }
        }

        Debug.Log($"[DroneBuildManager] Base registered at {baseTransform.position}");
    }

    void SpawnInitialDrones()
    {
        // ★ ここでは Base 探しをしない。
        //    Base は BuildPlacement などから RegisterBase() で登録してもらう。

        if (dronePrefab == null)
        {
            Debug.LogError("[DroneBuildManager] dronePrefab が設定されていません。");
            return;
        }

        for (int i = 0; i < initialDroneCount; i++)
        {
            Vector3 pos = droneSpawnPoint ? droneSpawnPoint.position : transform.position;
            var d = Instantiate(dronePrefab, pos, Quaternion.identity);
            d.manager = this;
            d.name = $"Drone_{i + 1}";

            // ★ 登録済みならドローンに渡す（まだ無ければ null のままでOK）
            d.baseTransform = baseTransform;

            _drones.Add(d);
        }
    }

    // =========================
    // BuildPlacement から呼ばれるやつ
    // =========================
    public void EnqueueBigBuild(BuildPlacement placer, BuildingDef def, Vector3Int cell, Vector3 pos, GameObject ghost)
    {
        var t = new BuildTask
        {
            kind = TaskKind.BigBuild,
            placer = placer,
            def = def,
            bigCell = cell,
            worldPos = pos,
            ghost = ghost
        };

        EnqueueTask(t);
    }

    public void EnqueueFineBuild(BuildPlacement placer, BuildingDef def, Vector2Int cell, Vector3 pos, GameObject ghost)
    {
        var t = new BuildTask
        {
            kind = TaskKind.FineBuild,
            placer = placer,
            def = def,
            fineCell = cell,
            worldPos = pos,
            ghost = ghost
        };

        EnqueueTask(t);
    }

    void EnqueueTask(BuildTask t)
    {
        // ★ 上限チェックなしでそのまま入れる
        _queue.Enqueue(t);

        // すぐ渡せるドローンがいれば渡す
        TryDispatchTasks();

        // UI更新（待ち件数を見せるため）
        NotifyUI();
    }

    void TryDispatchTasks()
    {
        if (_drones.Count == 0 || _queue.Count == 0)
            return;

        int loopGuard = _queue.Count; // 無限ループ防止

        while (_queue.Count > 0 && loopGuard-- > 0)
        {
            var task = _queue.Dequeue();
            bool assigned = false;

            foreach (var worker in _drones)
            {
                if (!worker.IsIdle)
                    continue;

                // ★ ここで Job に合うかチェック
                if (!worker.CanAcceptTask(task.kind))
                    continue;

                worker.SetTask(task);
                assigned = true;
                break;
            }

            // 誰にも渡せなかったタスクはキューの末尾に戻す
            if (!assigned)
            {
                _queue.Enqueue(task);
            }
        }
    }

    public void EnqueueBigDemolish(BuildPlacement placer, Vector3Int cell, GameObject target)
    {
        var t = new BuildTask
        {
            kind = TaskKind.BigDemolish,
            placer = placer,
            bigCell = cell,
            worldPos = target.transform.position,
            targetToDemolish = target
        };
        EnqueueTask(t);
    }

    public void EnqueueFineDemolish(BuildPlacement placer, Vector2Int cell, GameObject target)
    {
        var t = new BuildTask
        {
            kind = TaskKind.FineDemolish,
            placer = placer,
            fineCell = cell,
            worldPos = target.transform.position,
            targetToDemolish = target
        };
        EnqueueTask(t);
    }

    // ★ 採掘タスクだけキューから全部消す
    public void ClearAllMiningReservations()
    {
        if (_queue.Count == 0) return;

        var tmp = new Queue<BuildTask>();

        while (_queue.Count > 0)
        {
            var t = _queue.Dequeue();

            // 採掘以外だけ残す
            if (t.kind != TaskKind.MineResource)
                tmp.Enqueue(t);
        }

        while (tmp.Count > 0)
            _queue.Enqueue(tmp.Dequeue());

        // UI とタスク配布を更新
        TryDispatchTasks();
        NotifyUI();
    }

    // ドローンから「終わった」と言われたとき
    public void NotifyDroneFinished(DroneWorker worker, BuildTask task, bool success = true)
    {
        if (success && task != null)
        {
            try
            {
                FinalizeTask(task);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DroneBuildManager] FinalizeTask failed: " + ex.Message);
                // 失敗したら再キュー（ここも上限なしで戻せる）
                _queue.Enqueue(task);
            }
        }
        else if (!success && task != null)
        {
            // 失敗したタスクも戻す
            _queue.Enqueue(task);
        }

        TryDispatchTasks();
        NotifyUI();
    }

    void FinalizeTask(BuildTask task)
    {
        switch (task.kind)
        {
            case TaskKind.BigBuild:
                task.placer?.FinalizeBigPlacement(task.def, task.bigCell, task.worldPos, task.ghost);
                break;
            case TaskKind.FineBuild:
                task.placer?.FinalizeFinePlacement(task.def, task.fineCell, task.worldPos, task.ghost);
                break;

            case TaskKind.BigDemolish:
                task.placer?.FinalizeBigDemolish(task.bigCell, task.targetToDemolish);
                break;
            case TaskKind.FineDemolish:
                task.placer?.FinalizeFineDemolish(task.fineCell, task.targetToDemolish);
                break;

            // ★ 採掘完了時
            case TaskKind.MineResource:
                break;
        }
    }

    public void EnqueueResourceMining(ResourceMarker marker, Vector3 targetPos)
    {
        if (marker == null) return;

        var t = new BuildTask
        {
            kind = TaskKind.MineResource,
            resourceMarker = marker,
            worldPos = targetPos   // ★ ブロックの位置をそのまま使う
        };

        EnqueueTask(t);
    }

    // =========================
    // 旧 DroneAgent 互換
    // =========================
    public BuildJob PopLegacyJob()
    {
        if (_queue.Count == 0) return null;
        var t = _queue.Dequeue();
        var j = new BuildJob
        {
            kind = t.kind,
            placer = t.placer,
            def = t.def,
            ghost = t.ghost,
            bigCell = t.bigCell,
            worldPos = t.worldPos,
            fineCell = t.fineCell
        };
        NotifyUI();
        return j;
    }

    public void NotifyDroneJobFinished(DroneBuildManager.BuildJob job)
    {
        NotifyDroneJobFinished(null, job, true);
    }

    public void NotifyDroneJobFinished(object agent, DroneBuildManager.BuildJob job, bool success = true)
    {
        if (success && job != null) FinalizeTask(job);
        else if (!success && job != null) _queue.Enqueue(job);

        TryDispatchTasks();
        NotifyUI();
    }

    // =========================
    // UI に渡す
    // =========================
    void NotifyUI()
    {
        OnDroneStateChanged?.Invoke(new List<DroneWorker>(_drones), _queue.Count);
    }

    // 全体の所有アイテム（Key: アイテム名, Value: 個数）
    Dictionary<string, int> _globalInventory = new Dictionary<string, int>();

    /// <summary>
    /// Base に納品されたアイテムを全体在庫に加算する
    /// </summary>
    public void RegisterDeliveredItems(string displayName, int amount)
    {
        if (amount <= 0) return;
        if (string.IsNullOrEmpty(displayName)) displayName = "資源";

        int current;
        if (!_globalInventory.TryGetValue(displayName, out current))
            current = 0;

        _globalInventory[displayName] = current + amount;

        Debug.Log($"[DroneBuildManager] {displayName} を {amount} 個納品 (合計: {_globalInventory[displayName]})");
    }

    /// <summary>
    /// 外部用：読み取り専用の全体在庫
    /// </summary>
    public IReadOnlyDictionary<string, int> GlobalInventory => _globalInventory;

    // =========================
    // ここからセーブ/ロード対応
    // =========================

    // キューにたまっているタスクをセーブ用に変換
    public List<DroneTaskData> GetQueuedTasksForSave()
    {
        var list = new List<DroneTaskData>();
        foreach (var t in _queue)
        {
            list.Add(ToTaskData(t));
        }
        return list;
    }

    // いま存在するドローンの実行中タスクをセーブ
    public List<DroneRuntimeData> GetRuntimeForSave()
    {
        var list = new List<DroneRuntimeData>();
        foreach (var d in _drones)
        {
            var data = new DroneRuntimeData();
            data.name = d.name;
            data.position = d.transform.position;
            data.state = d.State.ToString();
            data.workProgress = d.CurrentProgress01;
            data.workTimer = d.SavedWorkTimer;

            // ★ ここを追加（プロパティ名は実プロジェクトに合わせて）
            data.job = d.CurrentJob.ToString();   // ← 例：DroneWorker に CurrentJob プロパティを用意

            var cur = d.CurrentTask;
            if (cur != null)
                data.task = ToTaskData(cur);

            list.Add(data);
        }
        return list;
    }


    DroneTaskData ToTaskData(BuildTask t)
    {
        string kindStr = "";
        switch (t.kind)
        {
            case TaskKind.BigBuild: kindStr = "BigBuild"; break;
            case TaskKind.FineBuild: kindStr = "FineBuild"; break;
            case TaskKind.BigDemolish: kindStr = "BigDemolish"; break;
            case TaskKind.FineDemolish: kindStr = "FineDemolish"; break;
        }

        return new DroneTaskData
        {
            kind = kindStr,
            defName = t.def ? t.def.displayName : "",
            worldPos = t.worldPos,
            bigCell = t.bigCell,
            fineCell = t.fineCell,
            ghost = (t.ghost != null)
        };
    }

    BuildTask FromTaskData(DroneTaskData data, BuildPlacement placement, Func<string, BuildingDef> defResolver)
    {
        var def = defResolver != null ? defResolver(data.defName) : null;

        GameObject ghost = null;
        if (data.ghost && def != null && placement != null)
        {
            bool fine = (data.kind == "FineBuild" || data.kind == "FineDemolish");
            ghost = placement.CreateGhostForDef(def, data.worldPos, fine);
        }

        // kind を復元
        TaskKind kindEnum = TaskKind.BigBuild;
        switch (data.kind)
        {
            case "BigBuild": kindEnum = TaskKind.BigBuild; break;
            case "FineBuild": kindEnum = TaskKind.FineBuild; break;
            case "BigDemolish": kindEnum = TaskKind.BigDemolish; break;
            case "FineDemolish": kindEnum = TaskKind.FineDemolish; break;
        }

        var t = new BuildTask
        {
            kind = kindEnum,
            placer = placement,
            def = def,
            ghost = ghost,
            worldPos = data.worldPos,
            bigCell = data.bigCell,
            fineCell = data.fineCell
        };
        return t;
    }

    // セーブデータからキューとドローンを復元する
    public void RestoreFromSave(
        List<DroneTaskData> queued,
        List<DroneRuntimeData> runtime,
        BuildPlacement placement,
        Func<string, BuildingDef> defResolver)
    {
        // 1) いったんキューを空に
        _queue.Clear();

        // 2) キューを戻す（ここも上限なしでそのまま入れる）
        if (queued != null)
        {
            foreach (var q in queued)
            {
                var task = FromTaskData(q, placement, defResolver);
                _queue.Enqueue(task);
            }
        }

        // 3) ドローン本体を戻す
        int count = Mathf.Min(_drones.Count, runtime != null ? runtime.Count : 0);
        for (int i = 0; i < count; i++)
        {
            var d = _drones[i];
            var rd = runtime[i];

            d.RestoreFromSave(
                rd,
                placement,
                defResolver,
                this
            );
        }

        // 4) まだキューが残ってるなら配る
        TryDispatchTasks();
        NotifyUI();
    }
}
