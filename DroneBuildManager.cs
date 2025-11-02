using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ① 起動時に固定数のドローンを生成して持っておく
/// ② 建築タスクが来たら、Idleなドローンに渡す（いなければキューにためる）
/// ③ ドローンが終わったらIdleに戻す
/// ④ 左UIには「存在しているドローンたち」を常に送る
/// ⑤ セーブ/ロードに対応
/// </summary>
public class DroneBuildManager : MonoBehaviour
{
    public static DroneBuildManager Instance { get; private set; }

    [Header("Fixed drone pool")]
    public int initialDroneCount = 3;
    public DroneWorker dronePrefab;
    public Transform droneSpawnPoint;

    [Header("Task settings")]
    public int maxQueuedTasks = 64;

    // タスク（建築依頼）
    public enum TaskKind { Big, Fine }

    [Serializable]
    public class BuildTask
    {
        public TaskKind kind;
        public BuildPlacement placer;
        public BuildingDef def;
        public GameObject ghost;

        public Vector3Int bigCell;
        public Vector3 worldPos;

        public Vector2Int fineCell;
    }

    // 旧コード互換
    [Serializable]
    public class BuildJob : BuildTask { }

    readonly Queue<BuildTask> _queue = new Queue<BuildTask>();
    readonly List<DroneWorker> _drones = new List<DroneWorker>();

    public event Action<List<DroneWorker>, int> OnDroneStateChanged;

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
    }

    void SpawnInitialDrones()
    {
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
            kind = TaskKind.Big,
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
            kind = TaskKind.Fine,
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
        if (_queue.Count >= maxQueuedTasks)
        {
            Debug.LogWarning("[DroneBuildManager] queue is full.");
            return;
        }
        _queue.Enqueue(t);
        TryDispatchTasks();
        NotifyUI();
    }

    void TryDispatchTasks()
    {
        if (_queue.Count == 0) return;

        foreach (var drone in _drones)
        {
            if (!drone.IsIdle) continue;
            if (_queue.Count == 0) break;

            var task = _queue.Dequeue();
            drone.SetTask(task);
        }

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
                _queue.Enqueue(task);
            }
        }
        else if (!success && task != null)
        {
            _queue.Enqueue(task);
        }

        TryDispatchTasks();
        NotifyUI();
    }

    void FinalizeTask(BuildTask task)
    {
        switch (task.kind)
        {
            case TaskKind.Big:
                task.placer?.FinalizeBigPlacement(task.def, task.bigCell, task.worldPos, task.ghost);
                break;
            case TaskKind.Fine:
                task.placer?.FinalizeFinePlacement(task.def, task.fineCell, task.worldPos, task.ghost);
                break;
        }
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
            var cur = d.CurrentTask;
            if (cur != null)
                data.task = ToTaskData(cur);
            list.Add(data);
        }
        return list;
    }

    DroneTaskData ToTaskData(BuildTask t)
    {
        return new DroneTaskData
        {
            kind = t.kind == TaskKind.Big ? "Big" : "Fine",
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
            bool fine = data.kind == "Fine";
            ghost = placement.CreateGhostForDef(def, data.worldPos, fine);
        }

        var t = new BuildTask
        {
            kind = (data.kind == "Big") ? TaskKind.Big : TaskKind.Fine,
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

        // 2) キューを戻す
        if (queued != null)
        {
            foreach (var q in queued)
            {
                var task = FromTaskData(q, placement, defResolver);
                _queue.Enqueue(task);
            }
        }

        // 3) ドローン本体を戻す
        //    セーブ時とロード時で数が違う可能性もあるので、最小数で合わせる
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
