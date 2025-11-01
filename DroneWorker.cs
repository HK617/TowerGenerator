using UnityEngine;

/// <summary>
/// 常駐するドローン1体ぶん。
/// 詰まり防止のために「移動に時間がかかりすぎたら失敗で返す」「作業が終わらなかったら失敗で返す」を入れてある。
/// </summary>
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

    [Tooltip("これ秒以上かかっても目的地に着かなかったら、そのタスクは失敗とみなして返す")]
    public float moveTimeout = 3f;

    [Header("Work")]
    public float workTime = 0.5f;

    [Tooltip("これ秒以上作業したのに終わらなかったら、そのタスクは失敗として返す")]
    public float workTimeout = 5f;

    [Header("Visual (optional)")]
    public Transform graphics;

    [Header("Enemy avoid")]
    public LayerMask enemyCheckMask;   // Enemyレイヤーを指定

    [HideInInspector] public DroneBuildManager manager;

    public DroneState State => _state;
    public DroneBuildManager.BuildTask CurrentTask => _task;
    public float CurrentProgress01 => _workProgress;

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

        // ① 近くに敵がいるかチェック（半径0.6fくらい）
        bool enemyVeryClose = false;
        if (enemyCheckMask != 0) // ← 後で説明します
        {
            var hit = Physics2D.OverlapCircle(pos, 0.6f, enemyCheckMask);
            enemyVeryClose = (hit != null);
        }

        // ② 基本の到達距離（敵がいれば緩める）
        float reach = enemyVeryClose ? (arriveDistance * 4f) : arriveDistance;

        // 目的地に届いたら作業フェーズへ
        if (dist <= reach)
        {
            _state = DroneState.Working;
            _workTimer = 0f;
            _workProgress = 0f;
            return;
        }

        // タイムアウトチェック（敵がふさがってるときは少し余裕を持たせる）
        float timeout = enemyVeryClose ? (moveTimeout * 2f) : moveTimeout;
        if (_moveTimer > timeout)
        {
            manager?.NotifyDroneFinished(this, _task, false);
            _task = null;
            _state = DroneState.Idle;
            return;
        }

        // まだ移動できるなら進む
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

        // 万一 workTime が極端に短くても0除算にならないように
        float wt = Mathf.Max(0.01f, workTime);
        _workProgress += Time.deltaTime / wt;

        // 仕事が終わった
        if (_workProgress >= 1f)
        {
            manager?.NotifyDroneFinished(this, _task, true);
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
            return;
        }

        // 作業タイムアウト：Finalize 中のどこかで詰まっても復帰させる
        if (_workTimer > workTimeout)
        {
            manager?.NotifyDroneFinished(this, _task, false);
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
        }
    }
}
