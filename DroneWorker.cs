using UnityEngine;

/// <summary>
/// �풓����h���[��1�̂Ԃ�B
/// �l�܂�h�~�̂��߂Ɂu�ړ��Ɏ��Ԃ������肷�����玸�s�ŕԂ��v�u��Ƃ��I���Ȃ������玸�s�ŕԂ��v�����Ă���B
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

    [Tooltip("����b�ȏォ�����Ă��ړI�n�ɒ����Ȃ�������A���̃^�X�N�͎��s�Ƃ݂Ȃ��ĕԂ�")]
    public float moveTimeout = 3f;

    [Header("Work")]
    public float workTime = 0.5f;

    [Tooltip("����b�ȏ��Ƃ����̂ɏI���Ȃ�������A���̃^�X�N�͎��s�Ƃ��ĕԂ�")]
    public float workTimeout = 5f;

    [Header("Visual (optional)")]
    public Transform graphics;

    [Header("Enemy avoid")]
    public LayerMask enemyCheckMask;   // Enemy���C���[���w��

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

        // �@ �߂��ɓG�����邩�`�F�b�N�i���a0.6f���炢�j
        bool enemyVeryClose = false;
        if (enemyCheckMask != 0) // �� ��Ő������܂�
        {
            var hit = Physics2D.OverlapCircle(pos, 0.6f, enemyCheckMask);
            enemyVeryClose = (hit != null);
        }

        // �A ��{�̓��B�����i�G������Ίɂ߂�j
        float reach = enemyVeryClose ? (arriveDistance * 4f) : arriveDistance;

        // �ړI�n�ɓ͂������ƃt�F�[�Y��
        if (dist <= reach)
        {
            _state = DroneState.Working;
            _workTimer = 0f;
            _workProgress = 0f;
            return;
        }

        // �^�C���A�E�g�`�F�b�N�i�G���ӂ������Ă�Ƃ��͏����]�T����������j
        float timeout = enemyVeryClose ? (moveTimeout * 2f) : moveTimeout;
        if (_moveTimer > timeout)
        {
            manager?.NotifyDroneFinished(this, _task, false);
            _task = null;
            _state = DroneState.Idle;
            return;
        }

        // �܂��ړ��ł���Ȃ�i��
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

        // ���� workTime ���ɒ[�ɒZ���Ă�0���Z�ɂȂ�Ȃ��悤��
        float wt = Mathf.Max(0.01f, workTime);
        _workProgress += Time.deltaTime / wt;

        // �d�����I�����
        if (_workProgress >= 1f)
        {
            manager?.NotifyDroneFinished(this, _task, true);
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
            return;
        }

        // ��ƃ^�C���A�E�g�FFinalize ���̂ǂ����ŋl�܂��Ă����A������
        if (_workTimer > workTimeout)
        {
            manager?.NotifyDroneFinished(this, _task, false);
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
        }
    }
}
