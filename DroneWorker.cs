using UnityEngine;

public class DroneWorker : MonoBehaviour
{
    public enum DroneState
    {
        Idle,
        MovingToTarget,
        Working
    }

    [Header("Move")]
    public float speed = 6f;
    public float arriveDistance = 0.05f;
    public float moveTimeout = 3f;

    [Header("Work")]
    public float workTime = 0.5f;
    public float workTimeout = 5f;

    [Header("Visual (optional)")]
    public Transform graphics;

    [Header("Enemy avoid")]
    public LayerMask enemyCheckMask;

    [HideInInspector] public DroneBuildManager manager;

    public DroneState State => _state;
    public DroneBuildManager.BuildTask CurrentTask => _task;
    public float CurrentProgress01 => _workProgress;
    public float SavedWorkTimer => _workTimer;

    DroneState _state = DroneState.Idle;
    DroneBuildManager.BuildTask _task;
    Vector3 _target;
    float _workProgress;
    float _moveTimer;
    float _workTimer;

    public bool IsIdle => _state == DroneState.Idle;

    public void SetTask(DroneBuildManager.BuildTask task)
    {
        _task = task;
        _target = task.worldPos;
        _state = DroneState.MovingToTarget;
        _workProgress = 0f;
        _moveTimer = 0f;
        _workTimer = 0f;
    }

    void Update()
    {
        switch (_state)
        {
            case DroneState.Idle:
                return;

            case DroneState.MovingToTarget:
                TickMove();
                return;

            case DroneState.Working:
                TickWork();
                return;
        }
    }

    void TickMove()
    {
        _moveTimer += Time.deltaTime;

        Vector3 pos = transform.position;
        Vector3 to = _target - pos;
        float dist = to.magnitude;

        bool enemyVeryClose = false;
        if (enemyCheckMask != 0)
        {
            var hit = Physics2D.OverlapCircle(pos, 0.6f, enemyCheckMask);
            enemyVeryClose = (hit != null);
        }

        float reach = enemyVeryClose ? (arriveDistance * 4f) : arriveDistance;

        if (dist <= reach)
        {
            _state = DroneState.Working;
            _workTimer = 0f;
            _workProgress = 0f;
            return;
        }

        float timeout = enemyVeryClose ? (moveTimeout * 2f) : moveTimeout;
        if (_moveTimer > timeout)
        {
            manager?.NotifyDroneFinished(this, _task, false);
            _task = null;
            _state = DroneState.Idle;
            return;
        }

        if (dist > 0.001f)
        {
            Vector3 dir = to / dist;
            transform.position += dir * speed * Time.deltaTime;

            if (graphics != null && dir.sqrMagnitude > 0.001f)
            {
                float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                graphics.rotation = Quaternion.Euler(0, 0, ang - 90f);
            }
        }
    }

    void TickWork()
    {
        _workTimer += Time.deltaTime;

        float wt = Mathf.Max(0.01f, workTime);
        _workProgress += Time.deltaTime / wt;

        if (_workProgress >= 1f)
        {
            manager?.NotifyDroneFinished(this, _task, true);
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
            return;
        }

        if (_workTimer > workTimeout)
        {
            manager?.NotifyDroneFinished(this, _task, false);
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
        }
    }

    // ===== ここからロード用 =====
    public void RestoreFromSave(
        DroneRuntimeData data,
        BuildPlacement placement,
        System.Func<string, BuildingDef> defResolver,
        DroneBuildManager mgr)
    {
        // 位置を戻す
        transform.position = data.position;
        manager = mgr;

        // 状態を文字列から
        if (!System.Enum.TryParse<DroneState>(data.state, out var st))
            st = DroneState.Idle;

        // タスクが無いならIdleでOK
        if (data.task == null)
        {
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
            _workTimer = 0f;
            return;
        }

        // タスクを復元（BuildPlacement側でゴーストを作る）
        var def = defResolver != null ? defResolver(data.task.defName) : null;
        GameObject ghost = null;
        if (data.task.ghost && def != null && placement != null)
        {
            // "FineBuild" / "FineDemolish" のときだけ fine = true
            bool fine = (data.task.kind == "FineBuild" || data.task.kind == "FineDemolish");
            ghost = placement.CreateGhostForDef(def, data.task.worldPos, fine);
        }

        // kind を文字列から enum に復元
        DroneBuildManager.TaskKind kindEnum = DroneBuildManager.TaskKind.BigBuild;
        switch (data.task.kind)
        {
            case "BigBuild": kindEnum = DroneBuildManager.TaskKind.BigBuild; break;
            case "FineBuild": kindEnum = DroneBuildManager.TaskKind.FineBuild; break;
            case "BigDemolish": kindEnum = DroneBuildManager.TaskKind.BigDemolish; break;
            case "FineDemolish": kindEnum = DroneBuildManager.TaskKind.FineDemolish; break;
        }

        var task = new DroneBuildManager.BuildTask
        {
            kind = kindEnum,
            placer = placement,
            def = def,
            ghost = ghost,
            worldPos = data.task.worldPos,
            bigCell = data.task.bigCell,
            fineCell = data.task.fineCell
        };

        _task = task;
        _target = task.worldPos;
        _state = st;

        // 作業途中から再開
        _workProgress = Mathf.Clamp01(data.workProgress);
        _workTimer = data.workTimer;

        // もし「Movingで終わってた」場合、Movingとして再開
        if (_state == DroneState.MovingToTarget)
        {
            _moveTimer = 0f;
        }
    }
}
