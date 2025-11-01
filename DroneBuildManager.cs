using System.Collections.Generic;
using UnityEngine;

public class DroneBuildManager : MonoBehaviour
{
    public static DroneBuildManager Instance { get; private set; }

    [Header("Drone")]
    public DroneAgent dronePrefab;
    public Transform droneSpawnPoint;

    [Header("Build")]
    [Tooltip("1建築にかける時間(秒)")]
    public float buildTime = 1.5f;

    [Tooltip("ゴーストの子にぶら下げる進捗バーPrefab")]
    public GameObject progressBarPrefab;

    readonly Queue<BuildJob> _jobs = new Queue<BuildJob>();
    DroneAgent _drone;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (dronePrefab != null)
        {
            Vector3 spawn = droneSpawnPoint ? droneSpawnPoint.position : Vector3.zero;
            _drone = Instantiate(dronePrefab, spawn, Quaternion.identity);
            _drone.name = "DroneAgent";
        }
    }

    void Start()
    {
        // ← これが「常にキューを見てる」ループ
        StartCoroutine(WorkerLoop());
    }

    System.Collections.IEnumerator WorkerLoop()
    {
        while (true)
        {
            if (_drone != null && !_drone.IsBusy && _jobs.Count > 0)
            {
                StartNextJob();
            }
            yield return null;
        }
    }

    void StartNextJob()
    {
        if (_drone == null) return;
        if (_drone.IsBusy) return;
        if (_jobs.Count == 0) return;

        var job = _jobs.Dequeue();

        GameObject bar = null;
        if (progressBarPrefab != null && job.ghost != null)
        {
            // ゴーストの子に付ける
            bar = Instantiate(progressBarPrefab, job.ghost.transform);
            bar.transform.localPosition = new Vector3(0, 1.0f, 0);
        }

        _drone.StartBuildJob(job, this, buildTime, bar);
    }

    // ===== Publish API =====
    public void EnqueueFineBuild(BuildPlacement source, BuildingDef def, Vector2Int fineCell, Vector3 worldPos, GameObject ghost)
    {
        _jobs.Enqueue(new BuildJob
        {
            source = source,
            def = def,
            isFine = true,
            fineCell = fineCell,
            bigCell = default,
            worldPos = worldPos,
            ghost = ghost,
        });
    }

    public void EnqueueBigBuild(BuildPlacement source, BuildingDef def, Vector3Int bigCell, Vector3 worldPos, GameObject ghost)
    {
        _jobs.Enqueue(new BuildJob
        {
            source = source,
            def = def,
            isFine = false,
            fineCell = default,
            bigCell = bigCell,
            worldPos = worldPos,
            ghost = ghost,
        });
    }

    // ドローンが終わったときに呼ぶ
    public void NotifyDroneJobFinished(BuildJob job)
    {
        if (job.isFine)
            job.source.FinalizeFinePlacement(job.def, job.fineCell, job.worldPos, job.ghost);
        else
            job.source.FinalizeBigPlacement(job.def, job.bigCell, job.worldPos, job.ghost);

        // ここで何もせずとも WorkerLoop が次を勝手に拾う
    }

    // ====== データ構造 ======
    public struct BuildJob
    {
        public BuildPlacement source;
        public BuildingDef def;
        public bool isFine;
        public Vector2Int fineCell;
        public Vector3Int bigCell;
        public Vector3 worldPos;
        public GameObject ghost;
    }
}
