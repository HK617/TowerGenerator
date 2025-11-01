using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// �@ �N�����ɌŒ萔�̃h���[���𐶐����Ď����Ă���
/// �A ���z�^�X�N��������AIdle�ȃh���[���ɓn���i���Ȃ���΃L���[�ɂ��߂�j
/// �B �h���[�����I�������Idle�ɖ߂�
/// �C ��UI�ɂ́u���݂��Ă���h���[�������v����ɑ���
/// �D ��DroneAgent�p�̌݊������c��
/// </summary>
public class DroneBuildManager : MonoBehaviour
{
    public static DroneBuildManager Instance { get; private set; }

    [Header("Fixed drone pool")]
    [Tooltip("�ŏ��ɉ��̐������Ă�����")]
    public int initialDroneCount = 3;

    [Tooltip("�v�[���p�̃h���[���v���n�u�iDroneWorker���t���Ă��邱�Ɓj")]
    public DroneWorker dronePrefab;

    [Tooltip("�h���[���̏o���ʒu")]
    public Transform droneSpawnPoint;

    [Header("Task settings")]
    public int maxQueuedTasks = 64;

    // �^�X�N�i���z�˗��j
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

    // ���R�[�h�݊�
    [Serializable]
    public class BuildJob : BuildTask { }

    // �^�X�N��҂����Ă���
    readonly Queue<BuildTask> _queue = new Queue<BuildTask>();

    // ���ܑ��݂��Ă���풓�h���[������
    readonly List<DroneWorker> _drones = new List<DroneWorker>();

    /// <summary>
    /// UI���w�ǂ���C�x���g
    /// ������: ���ݑ��݂��Ă���h���[���̃��X�g�i��ɓ������j
    /// ������: ���݂̑ҋ@�^�X�N���Ȃǂ����������Ɏg�����A�����ł͋��OK
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
        // �풓�h���[������
        SpawnInitialDrones();
        NotifyUI();
    }

    void Update()
    {
        // �󂢂Ă�h���[��������΃^�X�N��n��
        TryDispatchTasks();
    }

    void SpawnInitialDrones()
    {
        if (dronePrefab == null)
        {
            Debug.LogError("[DroneBuildManager] dronePrefab ���ݒ肳��Ă��܂���B");
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
    // BuildPlacement ����Ă΂����
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

    // �󂢂Ă�h���[���Ƀ^�X�N��n��
    void TryDispatchTasks()
    {
        if (_queue.Count == 0) return;

        foreach (var drone in _drones)
        {
            if (!drone.IsIdle) continue;
            if (_queue.Count == 0) break;

            var task = _queue.Dequeue();
            drone.SetTask(task); // �h���[�����ɓn��
        }

        NotifyUI();
    }

    // �h���[������u�I������v�ƌ���ꂽ�Ƃ�
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
                // ���s�����̂ŃL���[�ɖ߂�
                _queue.Enqueue(task);
            }
        }
        else if (!success && task != null)
        {
            // ��������点��B���Ȃ炱���Ŏ̂Ă�ł�OK
            _queue.Enqueue(task);
        }

        // �I������̂ł܂��ʂ̃^�X�N��U��
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
    // �� DroneAgent �݊�
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
    // UI �ɓn��
    // =========================
    void NotifyUI()
    {
        OnDroneStateChanged?.Invoke(new List<DroneWorker>(_drones), _queue.Count);
    }
}
