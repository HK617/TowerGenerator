using UnityEngine;
using System.Collections.Generic;
using System.Text;

public class DroneWorker : MonoBehaviour
{
    public enum DroneState
    {
        Idle,
        MovingToTarget,
        Working,
        ReturningToBase
    }

    public enum JobType
    {
        Builder,   // 建築・解体専用
        Miner      // 採掘専用
    }

    [SerializeField]
    JobType _currentJob = JobType.Builder;

    public JobType CurrentJob => _currentJob;

    [Header("Job設定")]
    public JobType job = JobType.Builder;   // デフォルトは Builder

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

    [Header("Base")]
    public Transform baseTransform;

    [Header("Mining")]
    [Tooltip("1回の採掘にかかる秒数")]
    public float miningInterval = 3f;

    [Tooltip("この採掘タスクで運べるアイテムの最大数")]
    public int miningCarryCapacity = 20;   // ★ 追加

    // 現在の採掘タスクでいくつ持っているか（タスク終了条件に使う）
    int _currentCarryCount = 0;            // ★ 追加

    // 採掘用タイマー
    float _miningTimer = 0f;

    bool _miningVisualNotified = false;

    // ドローンが今まで掘ってきたアイテム（種類→個数）
    Dictionary<string, int> _minedItems = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> MinedItems => _minedItems;

    // 1種類のアイテムに対する採掘統計
    [System.Serializable]
    public class MinedItemStat
    {
        public string scriptName;   // アイテムのスクリプト名（クラス名）
        public string displayName;  // アイテム名（UI表示用, 日本語）
        public int totalCount;      // 採掘総数
    }

    // key: scriptName（クラス名）
    readonly Dictionary<string, MinedItemStat> _minedItemStats = new();

    // 読み取り専用で外部に渡したい場合に使える
    public IEnumerable<MinedItemStat> MinedItemStats => _minedItemStats.Values;

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

    /// <summary>
    /// 今の Job 設定で、指定されたタスク種別を受けてよいか？
    /// </summary>
    public bool CanAcceptTask(DroneBuildManager.TaskKind kind)
    {
        switch (job)
        {
            case JobType.Builder:
                // 建築・解体のみ
                return kind == DroneBuildManager.TaskKind.BigBuild
                    || kind == DroneBuildManager.TaskKind.FineBuild
                    || kind == DroneBuildManager.TaskKind.BigDemolish
                    || kind == DroneBuildManager.TaskKind.FineDemolish;

            case JobType.Miner:
                // 採掘のみ
                return kind == DroneBuildManager.TaskKind.MineResource;

            default:
                return true;
        }
    }

    /// <summary>
    /// このドローンがアイテムを1つ採掘したときに呼ぶ
    /// </summary>
    /// <param name="itemScript">アイテムのスクリプト（Component）や ScriptableObject</param>
    /// <param name="displayName">UI に出したい日本語名</param>
    public void NotifyMinedItem(Object itemScript, string displayName)
    {
        if (itemScript == null && string.IsNullOrEmpty(displayName))
            return;

        // キーにはスクリプト名を使う（同じ種類をまとめる用）
        string scriptName = (itemScript != null) ? itemScript.GetType().Name : displayName;

        if (string.IsNullOrEmpty(displayName))
        {
            displayName = scriptName;
        }

        if (!_minedItemStats.TryGetValue(scriptName, out var stat))
        {
            stat = new MinedItemStat
            {
                scriptName = scriptName,
                displayName = displayName,
                totalCount = 0
            };
            _minedItemStats[scriptName] = stat;
        }

        stat.totalCount++;
    }

    /// <summary>
    /// 詳細メニュー用：日本語名と合計数だけをまとめた文字列を返す
    /// </summary>
    public string GetMinedItemSummary()
    {
        if (_minedItemStats.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("採掘ログ:");

        foreach (var stat in _minedItemStats.Values)
        {
            // 例: "鉄鉱石 x 12"
            sb.AppendLine($"{stat.displayName} x {stat.totalCount}");
        }

        return sb.ToString();
    }

    public void SetTask(DroneBuildManager.BuildTask task)
    {
        _task = task;
        _target = task.worldPos;
        _state = DroneState.MovingToTarget;
        _workProgress = 0f;
        _moveTimer = 0f;
        _workTimer = 0f;

        // ★ 採掘関連カウンタもリセット
        _miningTimer = 0f;
        _currentCarryCount = 0;
        _miningVisualNotified = false;   // ★ MiningIcon をまだ消していない状態に戻す
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

            case DroneState.ReturningToBase:  // ★ 追加
                TickReturnToBase();
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
        // ★ タスクが無いなら安全に Idle に戻す
        if (_task == null)
        {
            _state = DroneState.Idle;
            _workProgress = 0f;
            return;
        }

        // ★ 採掘タスクの場合は、専用の処理に切り替える
        if (_task.kind == DroneBuildManager.TaskKind.MineResource)
        {
            TickMiningWork();
            return;
        }

        // ここから下は従来どおり「一回きりの建築 / 解体」タスク
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

    void TickReturnToBase()
    {
        if (baseTransform == null)
        {
            _state = DroneState.Idle;
            _currentCarryCount = 0;
            return;
        }

        Vector3 pos = transform.position;
        Vector3 to = baseTransform.position - pos;
        float dist = to.magnitude;

        if (dist <= arriveDistance)
        {
            // ====== ここで納品処理をする ======
            if (manager != null && _task != null && _task.kind == DroneBuildManager.TaskKind.MineResource)
            {
                // 今回掘っていた資源名を決める
                string displayName = "資源";
                var marker = _task.resourceMarker;
                if (marker != null && marker.def != null && !string.IsNullOrEmpty(marker.def.displayName))
                {
                    displayName = marker.def.displayName;
                }

                // このドローンが「今回のタスクで持っている分」を全体在庫に加算
                manager.RegisterDeliveredItems(displayName, _currentCarryCount);
            }

            // ドローン側の「持ち物」をリセット
            _currentCarryCount = 0;
            _minedItems.Clear();
            _minedItemStats.Clear();

            // タスク自体は完了扱いにする
            manager?.NotifyDroneFinished(this, _task, true);
            _task = null;

            _state = DroneState.Idle;
            return;
        }

        // 移動
        if (dist > 0.001f)
        {
            Vector3 dir = to / dist;
            transform.position += dir * speed * Time.deltaTime;
        }
    }

    // MineResource タスク専用の「掘り続ける」処理
    // MineResource タスク専用の「掘り続ける」処理
    void TickMiningWork()
    {
        // ターゲット Resource
        var marker = _task.resourceMarker;
        if (marker == null)
        {
            // 採掘対象が消えたらタスク終了して Idle に戻る
            manager?.NotifyDroneFinished(this, _task, false);
            _task = null;
            _state = DroneState.Idle;
            _workProgress = 0f;
            _miningTimer = 0f;
            _currentCarryCount = 0;
            _miningVisualNotified = false;
            return;
        }

        // === すでに保有上限に達しているなら、これ以上掘らず Base へ向かう ===
        if (miningCarryCapacity > 0 && _currentCarryCount >= miningCarryCapacity)
        {
            // ★ このタイミングで MiningIcon を消す（1回だけ）
            if (!_miningVisualNotified)
            {
                marker.OnMiningCompletedAt(_task.worldPos);
                _miningVisualNotified = true;
            }

            // ★ Base に帰るモードへ移行
            if (baseTransform != null)
            {
                _state = DroneState.ReturningToBase;
                _target = baseTransform.position;   // Base へ移動開始
            }
            else
            {
                // Base が無い場合は今まで通りタスク終了
                manager?.NotifyDroneFinished(this, _task, true);
                _task = null;
                _state = DroneState.Idle;
                _workProgress = 0f;
                _miningTimer = 0f;
                _currentCarryCount = 0;
                _miningVisualNotified = false;
            }

            return;
        }

        // === ここから通常の「一定時間ごとに掘る」処理 ===
        float interval = Mathf.Max(0.1f, miningInterval);
        _miningTimer += Time.deltaTime;

        if (_miningTimer >= interval)
        {
            _miningTimer = 0f;

            // Resource の種類名を決める
            string displayName = "資源";
            if (marker.def != null && !string.IsNullOrEmpty(marker.def.displayName))
            {
                displayName = marker.def.displayName;
            }

            // 内部カウント（日本語名→個数）
            int cur;
            if (!_minedItems.TryGetValue(displayName, out cur))
                cur = 0;
            _minedItems[displayName] = cur + 1;

            // 採掘統計（スクリプト + 日本語名）
            NotifyMinedItem(marker.def, displayName);

            // このタスクで持っている数を増やす
            _currentCarryCount++;

            Debug.Log($"[DroneWorker] {name} mined {displayName}. TaskCarry = {_currentCarryCount}");
        }

        // プログレスバー用（0〜1）：保有数 / 上限
        if (miningCarryCapacity > 0)
            _workProgress = Mathf.Clamp01((float)_currentCarryCount / miningCarryCapacity);
        else
            _workProgress = 0f;
    }

    public string GetMiningSummaryString()
    {
        if (_minedItems == null || _minedItems.Count == 0)
            return "";

        var sb = new StringBuilder();
        foreach (var kv in _minedItems)
        {
            // 例: "鉄鉱石: 12"
            sb.AppendLine($"{kv.Key}: {kv.Value}");
        }
        return sb.ToString();
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
        // ★ Job 復元
        if (!string.IsNullOrEmpty(data.job))
        {
            if (System.Enum.TryParse<JobType>(data.job, out var j))
                _currentJob = j;
            else
                _currentJob = JobType.Builder;
        }
        else
        {
            _currentJob = JobType.Builder;
        }

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
    public void SetJob(JobType newJob)
    {
        if (_currentJob == newJob)
            return;

        // Miner → Builder 変更時は採掘中断
        if (newJob == JobType.Builder)
        {
            _task = null;
            _state = DroneState.Idle;
            _miningTimer = 0f;
            _currentCarryCount = 0;
        }

        _currentJob = newJob;
    }
}
