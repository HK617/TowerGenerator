using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 合流コンベアー用の「FIFO 弁」
/// 
/// ・周囲の ConveyorBelt を調べて「自分に向かってアイテムを流してくる枝方向」を入口として登録
/// ・本線（mainInDirectionWorld 方向）からの入力は弁の対象にしない（常に開放）
/// ・各入口ごとに「待機中か」「いつから待っているか」を記録して FIFO 順に通す
/// ・ItemOnBeltMover から RegisterWaiting → IsEntranceOpen を呼んでもらい、
///   false の場合はその手前で停止させる
/// ・3 つ以上合流が連なっても、各合流ブロックが局所的に FIFO で流れるので止まりにくい
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ConveyorBelt))]
public class BeltMergeController : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("隣接ベルトを探す半径（Fine グリッド 1 マスが 0.25 なら 0.3〜0.4 推奨）")]
    public float neighborRadius = 0.35f;

    [Tooltip("「隣のベルトの out が自分に向いている」とみなす角度のしきい値（内積）")]
    [Range(0.0f, 1.0f)]
    public float inputDotThreshold = 0.75f;

    [Header("Gate (FIFO)")]
    [Tooltip("※C案ではほぼ未使用。0 のままでOK（将来用に残してあります）")]
    public float switchInterval = 0f;

    [Tooltip("合流マス上にアイテムがいるかを見る半径")]
    public float occupyRadius = 0.15f;

    [Tooltip("ベルト検出用レイヤーマスク（未設定なら ConveyorBelt のマスクを使用）")]
    public LayerMask beltMask = 0;

    [Tooltip("アイテム検出用マスク（未設定なら \"Item\" レイヤー）")]
    public LayerMask itemMask = 0;

    [Header("Debug")]
    public bool debugDraw = false;

    class EntranceInfo
    {
        public Vector2 dir;          // 自分の中心へ向かうベクトル
        public bool hasWaiting;      // 待機中のアイテムがいるか？
        public float waitingSince;   // 最初に待ち始めた Time.time
    }

    ConveyorBelt _belt;
    readonly List<EntranceInfo> _entrances = new List<EntranceInfo>();
    int _activeIndex = -1;           // 今「通行中」の入口

    // キャッシュ
    readonly Collider2D[] _itemBuf = new Collider2D[8];

    public bool HasMultipleInputs => _entrances.Count >= 2;

    void Awake()
    {
        _belt = GetComponent<ConveyorBelt>();

        if (beltMask == 0)
            beltMask = _belt.conveyorLayerMask;

        if (itemMask == 0)
        {
            int idx = LayerMask.NameToLayer("Item");
            if (idx >= 0) itemMask = 1 << idx;
        }

        RebuildEntrances();
    }

    void Update()
    {
        // 隣接構造は建築/破壊で変わりうるので、ときどき入口を再構築する
        if (Time.frameCount % 30 == 0)
            RebuildEntrances();

        UpdateQueue();
    }

    // ─────────────────────────────
    // 入口検出
    // ─────────────────────────────

    void RebuildEntrances()
    {
        var newList = new List<EntranceInfo>();

        Vector2 center = transform.position;
        var hits = Physics2D.OverlapCircleAll(center, neighborRadius, beltMask);
        if (hits == null) hits = new Collider2D[0];

        // このベルトが定義している「本線の入口方向」
        Vector2 mainIn = _belt.mainInDirectionWorld;
        if (mainIn.sqrMagnitude > 1e-6f) mainIn.Normalize();
        else mainIn = Vector2.zero;

        foreach (var h in hits)
        {
            if (!h) continue;
            if (h.transform == transform) continue;

            var other = h.GetComponentInParent<ConveyorBelt>();
            if (other == null) continue;

            // 隣のベルト中心 → 自分中心
            Vector2 dirFromOther = (Vector2)transform.position - (Vector2)other.transform.position;
            if (dirFromOther.sqrMagnitude < 1e-6f) continue;
            dirFromOther.Normalize();

            // 隣のベルトの out 方向
            Vector2 outDir = other.mainOutDirectionWorld;
            if (outDir.sqrMagnitude < 1e-6f)
                outDir = other.moveDirection;
            if (outDir.sqrMagnitude < 1e-6f)
                outDir = other.transform.up;
            outDir.Normalize();

            // outDir が自分に向いているなら「入力候補」
            float dotOut = Vector2.Dot(outDir, dirFromOther);
            if (dotOut < inputDotThreshold)
                continue;

            // dirFromOther が mainIn とほぼ同じなら、本線からの入力 → 弁対象から除外
            if (mainIn != Vector2.zero)
            {
                float dotMainIn = Vector2.Dot(dirFromOther, mainIn);
                if (dotMainIn > 0.9f)
                    continue;
            }

            // すでに近い向きの入口があればそれを再利用
            EntranceInfo existing = null;
            foreach (var e in _entrances)
            {
                float d = Vector2.Dot(e.dir, dirFromOther);
                if (d > 0.98f)
                {
                    existing = e;
                    break;
                }
            }

            if (existing != null)
            {
                newList.Add(existing);
            }
            else
            {
                var e = new EntranceInfo
                {
                    dir = dirFromOther,
                    hasWaiting = false,
                    waitingSince = 0f
                };
                newList.Add(e);
            }
        }

        _entrances.Clear();
        _entrances.AddRange(newList);

        // アクティブ入口のインデックスを再マップ
        if (_activeIndex >= _entrances.Count) _activeIndex = -1;
    }

    // ─────────────────────────────
    // 待ち行列更新
    // ─────────────────────────────

    void UpdateQueue()
    {
        if (!HasMultipleInputs)
        {
            _activeIndex = -1;
            return;
        }

        // いまアクティブな入口がある場合
        if (_activeIndex >= 0 && _activeIndex < _entrances.Count)
        {
            // アイテムが合流マス上に存在する間は、その入口を維持
            if (IsOccupiedByItem())
                return;

            // 合流マスが空で、その入口に待機中もいないなら、次の入口へ
            if (!_entrances[_activeIndex].hasWaiting)
            {
                int next = FindEarliestWaiting();
                _activeIndex = next;
            }

            return;
        }

        // アクティブ無し → 待機中があるなら一番古い入口をアクティブに
        _activeIndex = FindEarliestWaiting();
    }

    int FindEarliestWaiting()
    {
        int best = -1;
        float bestTime = float.MaxValue;

        for (int i = 0; i < _entrances.Count; i++)
        {
            var e = _entrances[i];
            if (!e.hasWaiting) continue;
            if (e.waitingSince < bestTime)
            {
                bestTime = e.waitingSince;
                best = i;
            }
        }

        return best;
    }

    bool IsOccupiedByItem()
    {
        if (itemMask == 0) return false;
        int c = Physics2D.OverlapCircleNonAlloc(transform.position, occupyRadius, _itemBuf, itemMask);
        return c > 0;
    }

    int FindEntranceIndexByDirection(Vector2 dirToCenter)
    {
        if (_entrances.Count == 0) return -1;
        if (dirToCenter.sqrMagnitude < 1e-6f) return -1;

        dirToCenter.Normalize();

        int best = -1;
        float bestDot = 0.0f;

        for (int i = 0; i < _entrances.Count; i++)
        {
            float dot = Vector2.Dot(_entrances[i].dir, dirToCenter);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = i;
            }
        }

        if (best != -1 && bestDot >= inputDotThreshold)
            return best;

        return -1;
    }

    // ─────────────────────────────
    // Item 側から呼ばれる API
    // ─────────────────────────────

    /// <summary>
    /// この方向から入ろうとしているアイテムが、
    /// 「そもそも弁の対象となる入口か？」を返す。
    /// </summary>
    public bool IsRelevantEntrance(Vector2 dirToCenter)
    {
        return FindEntranceIndexByDirection(dirToCenter) != -1;
    }

    /// <summary>
    /// dirToCenter 方向から弁前に到達したアイテムが「待機を開始した」ことを通知。
    /// </summary>
    public void RegisterWaiting(Vector2 dirToCenter)
    {
        int idx = FindEntranceIndexByDirection(dirToCenter);
        if (idx < 0 || idx >= _entrances.Count) return;

        var e = _entrances[idx];
        if (!e.hasWaiting)
        {
            e.hasWaiting = true;
            e.waitingSince = Time.time;
        }
    }

    /// <summary>
    /// dirToCenter 方向のアイテムが「このフレーム進んでよいか？」を返す。
    /// FIFO の順番に従って true/false が決まる。
    /// </summary>
    public bool IsEntranceOpen(Vector2 dirToCenter)
    {
        if (!HasMultipleInputs) return true;

        int idx = FindEntranceIndexByDirection(dirToCenter);
        if (idx < 0) return true; // 対象外

        UpdateQueue(); // 可能ならアクティブ入口を更新

        // まだ誰もアクティブでなく、待機中も居ない → 今の入口をそのまま通す
        if (_activeIndex == -1)
            _activeIndex = idx;

        bool open = (idx == _activeIndex);

        if (open)
        {
            // このアイテムが実際に動き始めるタイミングなので、
            // 「待機フラグ」は一旦クリア（後続が来たらまた RegisterWaiting される）
            var e = _entrances[idx];
            e.hasWaiting = false;
            e.waitingSince = 0f;
        }

        return open;
    }

    // ─────────────────────────────
    // Debug 表示
    // ─────────────────────────────

    void OnDrawGizmosSelected()
    {
        if (!debugDraw) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, neighborRadius);

        for (int i = 0; i < _entrances.Count; i++)
        {
            var e = _entrances[i];
            Vector3 from = transform.position;
            Vector3 to = from + (Vector3)e.dir * 0.7f;

            if (i == _activeIndex) Gizmos.color = Color.green;
            else if (e.hasWaiting) Gizmos.color = Color.red;
            else Gizmos.color = Color.gray;

            Gizmos.DrawLine(from, to);
            Gizmos.DrawSphere(to, 0.05f);
        }

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, occupyRadius);
    }
}
