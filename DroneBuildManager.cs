
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

    /// <summary>
    /// このタスクが「作りかけの建物」かどうか判定する。
    /// （建築タスクで、ゴーストに ConstructionState が付き、
    ///  一度でも素材が納品されていて、まだ完成していないもの）
    /// </summary>
    bool IsPartiallyBuilt(BuildTask t)
    {
        if (t == null) return false;

        // 建築タスク以外は関係ない
        if (t.kind != TaskKind.BigBuild && t.kind != TaskKind.FineBuild)
            return false;

        if (t.ghost == null) return false;

        var cons = t.ghost.GetComponent<ConstructionState>();
        if (cons == null) return false;

        // 「作りかけ」＝ 納品済み > 0 かつ 未完成
        return cons.HasStartedBuild && !cons.IsCompleted;
    }

    /// <summary>
    /// キュー内のタスクを、「作りかけの建物」→「まだ手を付けていない建物・その他」
    /// の順に並び替える。
    /// </summary>
    void ReorderQueueForBuildPriority()
    {
        if (_queue.Count <= 1)
            return;

        var list = new List<BuildTask>(_queue);
        _queue.Clear();

        // 1) 作りかけの建物タスクを先に
        foreach (var t in list)
        {
            if (IsPartiallyBuilt(t))
                _queue.Enqueue(t);
        }

        // 2) 残り（未着工建物＋解体＋採掘など）を後ろに
        foreach (var t in list)
        {
            if (!IsPartiallyBuilt(t))
                _queue.Enqueue(t);
        }
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
    public void RegisterBase(Transform baseTransform)
    {
        this.baseTransform = baseTransform;

        // ★ Base の位置をドローンのスポーン地点として使う
        if (droneSpawnPoint == null)
        {
            droneSpawnPoint = baseTransform;
        }

        // 既に存在しているドローンにも Base 情報を教える
        foreach (var d in _drones)
        {
            if (d == null) continue;

            d.baseTransform = baseTransform;

            // ★ まだ何もしていないドローンは Base の位置に集める
            if (d.State == DroneWorker.DroneState.Idle)
            {
                d.transform.position = baseTransform.position;
            }
        }

        Debug.Log($"[DroneBuildManager] Base registered at {baseTransform.position}");

        // ★ まだドローンが1体も居ない場合だけ、このタイミングで生産する
        if (_drones.Count == 0)
        {
            SpawnInitialDrones();
            NotifyUI();
        }
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
            // ★ RegisterBase() から呼ばれるので、基本的には baseTransform != null になっている想定
            Vector3 pos;
            if (baseTransform != null)
                pos = baseTransform.position;
            else if (droneSpawnPoint != null)
                pos = droneSpawnPoint.position;
            else
                pos = transform.position;

            var d = Instantiate(dronePrefab, pos, Quaternion.identity);
            d.manager = this;
            d.name = $"Drone_{i + 1}";
            d.baseTransform = baseTransform;   // Base 情報も渡しておく

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

        // ★ 一度でも素材が入った建物を先に処理するように並び替え
        ReorderQueueForBuildPriority();

        int loopGuard = _queue.Count; // 無限ループ防止

        while (_queue.Count > 0 && loopGuard-- > 0)
        {
            var task = _queue.Dequeue();
            if (task == null)
                continue;

            bool assigned = false;

            bool isBuildTask =
                task.kind == TaskKind.BigBuild ||
                task.kind == TaskKind.FineBuild;

            // ★ 建築タスクで、ベースに素材が 1 つも無い場合は後回し
            if (isBuildTask && task.def != null && task.def.buildCosts != null)
            {
                bool hasAnyResource = false;
                foreach (var cost in task.def.buildCosts)
                {
                    if (cost == null) continue;
                    if (string.IsNullOrEmpty(cost.itemName)) continue;
                    if (cost.amount <= 0) continue;

                    int have = 0;
                    if (_globalInventory.TryGetValue(cost.itemName, out have) && have > 0)
                    {
                        hasAnyResource = true;
                        break;
                    }
                }

                if (!hasAnyResource)
                {
                    // この建物に使う素材がベースに 1 個もない → まだ割り当てない
                    _queue.Enqueue(task);
                    continue;
                }
            }

            foreach (var worker in _drones)
            {
                if (worker == null)
                    continue;

                if (!worker.IsIdle)
                    continue;

                // ★ Job に合うかチェック（Builder / Miner）
                if (!worker.CanAcceptTask(task.kind))
                    continue;

                // ★ ここから先、BuildCarryCapacity で弾くチェックは削除
                //    → 「コストが大きい建物も、何回かに分けて運ぶ」ため

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
            case TaskKind.FineBuild:
                {
                    // もし BuildingDef に建築コストが設定されているなら、
                    // ConstructionState の IsComplete を満たしていない限り完成させない。
                    bool hasCost =
                        task.def != null &&
                        task.def.buildCosts != null &&
                        task.def.buildCosts.Count > 0;

                    if (hasCost)
                    {
                        var ghost = task.ghost;
                        if (ghost == null)
                        {
                            // ゴーストが無い時点でおかしいので、とりあえず再キュー
                            _queue.Enqueue(task);
                            return;
                        }

                        var cons = ghost.GetComponent<ConstructionState>();
                        if (cons == null)
                        {
                            // 納品状態が作られていない → コンポーネントを付けて初期化しておく
                            cons = ghost.AddComponent<ConstructionState>();
                        }

                        // Def の内容から entries を初期化（まだなら）
                        cons.EnsureInitialized(task.def);

                        // まだ必要数に達していないので、完成させずに再キュー
                        if (!cons.IsCompleted)
                        {
                            _queue.Enqueue(task);
                            return;
                        }
                    }

                    // ここまで来た時点で「材料条件OK」なので、初めて建物を完成させる
                    if (task.kind == TaskKind.BigBuild)
                    {
                        task.placer?.FinalizeBigPlacement(task.def, task.bigCell, task.worldPos, task.ghost);
                    }
                    else
                    {
                        task.placer?.FinalizeFinePlacement(task.def, task.fineCell, task.worldPos, task.ghost);
                    }

                    break;
                }

            case TaskKind.BigDemolish:
                task.placer?.FinalizeBigDemolish(task.bigCell, task.targetToDemolish);
                break;

            case TaskKind.FineDemolish:
                task.placer?.FinalizeFineDemolish(task.fineCell, task.targetToDemolish);
                break;

            // ★ 採掘完了時
            case TaskKind.MineResource:
                // 今は特に何もしない
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
    /// 指定された建物に対して、残り必要な材料を
    /// maxTotal 個まで Base 在庫から取り出す。
    /// 戻り値: 実際に取り出した (itemName -> 個数) の辞書。
    /// </summary>
    public Dictionary<string, int> TakeBuildMaterials(
        BuildingDef def,
        ConstructionState state,
        int maxTotal)
    {
        var result = new Dictionary<string, int>();

        if (def == null || def.buildCosts == null || maxTotal <= 0)
            return result;

        foreach (var cost in def.buildCosts)
        {
            if (cost == null) continue;
            if (string.IsNullOrEmpty(cost.itemName)) continue;
            if (cost.amount <= 0) continue;

            // そのアイテムの「まだ必要な数」を計算
            int delivered = 0;
            if (state != null)
            {
                delivered = state.GetDeliveredAmount(cost.itemName);
            }
            int remaining = cost.amount - delivered;
            if (remaining <= 0) continue;

            // Base 在庫をチェック
            int have = 0;
            _globalInventory.TryGetValue(cost.itemName, out have);
            if (have <= 0) continue;

            // このアイテムで実際に取れる数
            int canTake = Mathf.Min(remaining, have, maxTotal);
            if (canTake <= 0) continue;

            // 在庫を減らす
            _globalInventory[cost.itemName] = have - canTake;

            // 結果辞書に加算
            int currentTaken;
            result.TryGetValue(cost.itemName, out currentTaken);
            result[cost.itemName] = currentTaken + canTake;

            maxTotal -= canTake;
            if (maxTotal <= 0)
                break;
        }

        return result;
    }

    /// <summary>
    /// 今のタスクの建物 + キューに入っている他の建物について、
    /// 残り必要な材料を合計しつつ、maxTotal 個まで Base 在庫から取り出す。
    /// 戻り値: 実際に取り出した (itemName -> 個数) の辞書。
    /// </summary>
    public Dictionary<string, int> TakeBuildMaterialsForRoute(
        BuildTask currentTask,
        ConstructionState currentState,
        int maxTotal)
    {
        var result = new Dictionary<string, int>();

        if (currentTask == null || currentTask.def == null || maxTotal <= 0)
            return result;

        int remaining = maxTotal;

        // ---------- 1) まず「今の建物」ぶんを優先して積む ----------
        if (currentTask.def != null)
        {
            if (currentState != null)
            {
                currentState.EnsureInitialized(currentTask.def);
            }

            var takenCurrent = TakeBuildMaterials(currentTask.def, currentState, remaining);
            foreach (var kv in takenCurrent)
            {
                int cur;
                result.TryGetValue(kv.Key, out cur);
                result[kv.Key] = cur + kv.Value;
                remaining -= kv.Value;
            }

            if (remaining <= 0)
                return result;
        }

        // ---------- 2) まだキャパが余っていれば、キュー内の他の建物からも積む ----------
        foreach (var t in _queue)
        {
            if (remaining <= 0)
                break;

            // 建築タスク以外は無視
            if (t.kind != TaskKind.BigBuild && t.kind != TaskKind.FineBuild)
                continue;

            if (t.def == null)
                continue;

            // ゴーストから ConstructionState を取得
            var ghost = t.ghost;
            ConstructionState cons = null;
            if (ghost != null)
            {
                cons = ghost.GetComponent<ConstructionState>();
                if (cons == null)
                {
                    cons = ghost.AddComponent<ConstructionState>();
                }
            }

            if (cons != null)
            {
                cons.EnsureInitialized(t.def);
            }

            var takenOther = TakeBuildMaterials(t.def, cons, remaining);
            int sumTaken = 0;
            foreach (var kv in takenOther)
            {
                int cur;
                result.TryGetValue(kv.Key, out cur);
                result[kv.Key] = cur + kv.Value;
                sumTaken += kv.Value;
            }

            remaining -= sumTaken;
        }

        return result;
    }

    /// <summary>
    /// 現在の在庫で指定 BuildingDef を建てられるか？
    /// </summary>
    public bool HasEnoughResourcesFor(BuildingDef def)
    {
        if (def == null || def.buildCosts == null || def.buildCosts.Count == 0)
            return true;

        foreach (var cost in def.buildCosts)
        {
            if (cost == null) continue;
            if (string.IsNullOrEmpty(cost.itemName)) continue;
            if (cost.amount <= 0) continue;

            int have = 0;
            _globalInventory.TryGetValue(cost.itemName, out have);

            if (have < cost.amount)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 実際に在庫からコスト分を消費する
    /// （Base にドローンが到着したタイミングで呼ぶ想定）
    /// </summary>
    public bool TryConsumeResourcesFor(BuildingDef def)
    {
        if (def == null || def.buildCosts == null || def.buildCosts.Count == 0)
            return true;

        if (!HasEnoughResourcesFor(def))
            return false;

        foreach (var cost in def.buildCosts)
        {
            if (cost == null) continue;
            if (string.IsNullOrEmpty(cost.itemName)) continue;
            if (cost.amount <= 0) continue;

            int have = 0;
            _globalInventory.TryGetValue(cost.itemName, out have);

            int next = have - cost.amount;
            if (next > 0)
                _globalInventory[cost.itemName] = next;
            else
                _globalInventory.Remove(cost.itemName);
        }

        return true;
    }

    /// <summary>
    /// その BuildingDef 全体の「総コスト」（個数の合計）
    /// 「より少ない材料で作れる建物」を比較したいとき用。
    /// </summary>
    public int GetTotalBuildCost(BuildingDef def)
    {
        if (def == null || def.buildCosts == null)
            return 0;

        int sum = 0;
        foreach (var cost in def.buildCosts)
        {
            if (cost == null) continue;
            if (cost.amount > 0) sum += cost.amount;
        }
        return sum;
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
