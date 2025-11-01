using System.Collections.Generic;
using UnityEngine;

public class DroneBuildManager : MonoBehaviour
{
    public static DroneBuildManager Instance { get; private set; }

    [Header("Drone")]
    public DroneAgent dronePrefab;
    public Transform droneSpawnPoint;

    [Header("Build")]
    public float buildTime = 1.5f;
    public GameObject progressBarPrefab;

    // �h���[�������W���u
    readonly Queue<BuildJob> _jobs = new();

    // ������ɂ�������鏈��
    readonly Queue<System.Action> _heavy = new();

    [Tooltip("1�t���[���Ɏ��s����d�������̐�")]
    public int heavyPerFrame = 1;

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
            Vector3 p = droneSpawnPoint ? droneSpawnPoint.position : Vector3.zero;
            _drone = Instantiate(dronePrefab, p, Quaternion.identity);
            _drone.name = "DroneAgent";
        }
    }

    void Start()
    {
        StartCoroutine(Worker());
    }

    void Update()
    {
        // �d��������������藬��
        int budget = heavyPerFrame;
        while (budget-- > 0 && _heavy.Count > 0)
        {
            var a = _heavy.Dequeue();
            a?.Invoke();
        }
    }

    System.Collections.IEnumerator Worker()
    {
        while (true)
        {
            if (_drone != null && !_drone.IsBusy && _jobs.Count > 0)
                StartNext();
            yield return null;
        }
    }

    void StartNext()
    {
        var job = _jobs.Dequeue();

        GameObject bar = null;
        if (progressBarPrefab != null && job.ghost != null)
        {
            bar = Instantiate(progressBarPrefab, job.ghost.transform);
            bar.transform.localPosition = new Vector3(0, 1f, 0);
        }

        _drone.StartBuildJob(job, this, buildTime, bar);
    }

    // ===== enqueue =====
    public void EnqueueFineBuild(BuildPlacement src, BuildingDef def, Vector2Int cell, Vector3 pos, GameObject ghost)
    {
        _jobs.Enqueue(new BuildJob
        {
            source = src,
            def = def,
            isFine = true,
            fineCell = cell,
            bigCell = default,
            worldPos = pos,
            ghost = ghost,
        });
    }

    public void EnqueueBigBuild(BuildPlacement src, BuildingDef def, Vector3Int cell, Vector3 pos, GameObject ghost)
    {
        _jobs.Enqueue(new BuildJob
        {
            source = src,
            def = def,
            isFine = false,
            fineCell = default,
            bigCell = cell,
            worldPos = pos,
            ghost = ghost,
        });
    }

    // ===== �h���[�� �� �}�l�[�W�� =====
    public void NotifyDroneJobFinished(BuildJob job)
    {
        // �����ł� "���y����������" �������
        if (job.isFine)
            job.source.FinalizeFinePlacement(job.def, job.fineCell, job.worldPos, job.ghost);
        else
            job.source.FinalizeBigPlacement(job.def, job.bigCell, job.worldPos, job.ghost);

        // �d����������Ȃ��Ƃ���͌��
        _heavy.Enqueue(() =>
        {
            // �Z�p��Base��T����Ƃ��AFlowField��Rebuild�Ƃ���������
            if (!job.isFine && job.def != null && job.def.isHexTile)
            {
                var ui = Object.FindFirstObjectByType<StartMenuUI>();
                if (ui != null)
                    ui.TrySpawnBaseAt(job.worldPos);
            }

            // FlowField���u�S���I����Ă���1�񂾂��v�Ăт����Ȃ�
            // �����ŃJ�E���^�Ǘ����Ă�����
        });
    }

    // ====== struct ======
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
