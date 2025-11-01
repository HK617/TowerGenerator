using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ① 起動時に固定数のドローンを生成して持っておく
/// ② 建築タスクが来たら、Idleなドローンに渡す（いなければキューにためる）
/// ③ ドローンが終わったらIdleに戻す
/// ④ 左UIには「存在しているドローンたち」を常に送る
/// ⑤ 旧DroneAgent用の互換口も残す
/// </summary>
public class DroneBuildManager : MonoBehaviour
{
    public static DroneBuildManager Instance { get; private set; }

    [Header("Fixed drone pool")]
    [Tooltip("最初に何体生成しておくか")]
    public int initialDroneCount = 3;

    [Tooltip("プール用のドローンプレハブ（DroneWorkerが付いていること）")]
    public DroneWorker dronePrefab;

    [Tooltip("ドローンの出発位置")]
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

    // タスクを待たせておく
    readonly Queue<BuildTask> _queue = new Queue<BuildTask>();

    // いま存在している常駐ドローンたち
    readonly List<DroneWorker> _drones = new List<DroneWorker>();

    /// <summary>
    /// UIが購読するイベント
    /// 第一引数: 現在存在しているドローンのリスト（常に同じ数）
    /// 第二引数: 現在の待機タスク数などを見たい時に使うが、ここでは空でOK
    /// </summary>
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
        // 常駐ドローン生成
        SpawnInitialDrones();
        NotifyUI();
    }

    void Update()
    {
        // 空いてるドローンがあればタスクを渡す
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

    // 空いてるドローンにタスクを渡す
    void TryDispatchTasks()
    {
        if (_queue.Count == 0) return;

        foreach (var drone in _drones)
        {
            if (!drone.IsIdle) continue;
            if (_queue.Count == 0) break;

            var task = _queue.Dequeue();
            drone.SetTask(task); // ドローン側に渡す
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
                // 失敗したのでキューに戻す
                _queue.Enqueue(task);
            }
        }
        else if (!success && task != null)
        {
            // もう一回やらせる。嫌ならここで捨てるでもOK
            _queue.Enqueue(task);
        }

        // 終わったのでまた別のタスクを振る
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
}
