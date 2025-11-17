using UnityEngine;

/// <summary>
/// ・AddItem でアイテムを受け取り、ItemOnBeltMover にベルト情報を渡す
/// ・moveDirection は「このベルト上でのアイテムの進行方向」（ワールド座標）
///   ConveyorBeltAutoConnector から上書きされます。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider2D))]
public class ConveyorBelt : MonoBehaviour
{
    [Header("Move")]
    [Tooltip("アイテムを流すワールド方向。Awake で transform.up が入ります。")]
    public Vector2 moveDirection = Vector2.up;

    [Tooltip("アイテムの移動速度")]
    public float moveSpeed = 2f;

    [Header("Spawn")]
    [Tooltip("アイテムを出す位置。未指定ならベルトの中心")]
    public Transform itemSpawnPoint;

    [Tooltip("生成したアイテムをこのオブジェクトの子にするか")]
    public bool parentItemsToBelt = false;

    [Header("Belt Area")]
    [Tooltip("コンベアーとして判定するレイヤー（このレイヤーの Collider2D の上だけ動く）")]
    public LayerMask conveyorLayerMask;

    [HideInInspector]
    public Vector2 mainInDirectionWorld = Vector2.up;   // このベルトに入ってくる向き（中心に向かう移動方向）

    [HideInInspector]
    public Vector2 mainOutDirectionWorld = Vector2.up;  // このベルトから出ていく向き

    [HideInInspector]
    public bool isCornerBelt = false;                   // 入口と出口が90度に曲がっているベルトかどうか

    Collider2D _col;

    void Awake()
    {
        _col = GetComponent<Collider2D>();

        if (moveDirection == Vector2.zero)
            moveDirection = transform.up;

        if (conveyorLayerMask.value == 0)
            conveyorLayerMask = 1 << gameObject.layer;
    }

    void Reset()
    {
        moveDirection = Vector2.up;
        moveSpeed = 2f;
        conveyorLayerMask = 1 << gameObject.layer;
    }

    /// <summary>
    /// ドリルなどからアイテムを受け取る。
    /// </summary>
    public void AddItem(GameObject itemPrefab)
    {
        if (itemPrefab == null)
        {
            Debug.LogWarning("[ConveyorBelt] AddItem: itemPrefab が null", this);
            return;
        }

        Vector3 spawnPos = itemSpawnPoint != null
            ? itemSpawnPoint.position
            : transform.position;

        var item = Instantiate(itemPrefab, spawnPos, Quaternion.identity);

        if (parentItemsToBelt)
            item.transform.SetParent(transform, true);

        // ベルト移動制御
        var mover = item.GetComponent<ItemOnBeltMover>();
        if (mover == null)
            mover = item.AddComponent<ItemOnBeltMover>();
        mover.Init(this);

        // 分岐検知（分割ベルトの上に来たら 3 方向に流す）
        var splitterWatcher = item.GetComponent<BeltItemSplitterWatcher>();
        if (splitterWatcher == null)
            splitterWatcher = item.AddComponent<BeltItemSplitterWatcher>();
    }
}

/// <summary>
/// 「常に足元のコンベアーを調べて、そのコンベアーの向きに合わせて動く」
/// アイテム側の移動制御。
/// ベルトが変わった瞬間に、そのベルトの中心に一度スナップしてから進行方向を切り替えるので、
/// 曲がり角の真ん中で方向転換しているように見えます。
/// 
/// ★ 変更点
/// ・前方のアイテムの“進行方向”は無視し、Cast にヒットした ItemOnBeltMover は全て障害物扱い
/// ・同じベルトでなくても、Item レイヤーのコライダーがあれば停止対象
/// ・カーブベルトでは、入口方向→出口方向へ Vector2.Slerp で徐々に曲げる
///   （そのカーブ方向で前方チェックも行う）
/// </summary>
public class ItemOnBeltMover : MonoBehaviour
{
    ConveyorBelt _currentBelt;
    LayerMask _beltMask;
    Rigidbody2D _rb;
    Collider2D _col;

    [Header("Anti-collision")]
    [Tooltip("前のアイテムから最低これだけ離れて停止（見た目サイズに合わせる）")]
    public float minGap = 0.18f;

    [Tooltip("キャスト時の安全マージン")]
    public float safety = 0.02f;

    [Tooltip("アイテム検出レイヤー（未設定なら自動で Item）")]
    public LayerMask itemMask = 0;

    [Header("Corner / Merge Turn")]
    [Tooltip("ベルトの進行方向が変わるとき、1秒間に何度まで回転するか（カーブ・合流共通）")]
    public float cornerTurnSpeedDegPerSec = 360; //計算式90×4×(movwSpeed/2)

    // 足元のベルト検出用
    const float BeltDetectRadius = 0.08f;

    // 現在の向き（度）。Init でベルト方向から初期化する
    float _currentAngleDeg;
    bool _hasCurrentAngle = false;

    // 前方キャストで使うワーク配列
    readonly RaycastHit2D[] _castHits = new RaycastHit2D[16];

    // ConveyorBelt.AddItem / ConveyorDistributor から最初に呼ばれる
    public void Init(ConveyorBelt firstBelt)
    {
        _currentBelt = firstBelt;
        _beltMask = (firstBelt != null) ? firstBelt.conveyorLayerMask : ~0;

        _rb = GetComponent<Rigidbody2D>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody2D>();

        // 物理は完全に自前制御
        _rb.gravityScale = 0f;
        _rb.linearDamping = 0f;
        _rb.angularDamping = 0f;
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        _col = GetComponent<Collider2D>();
        if (_col == null)
        {
            var c = gameObject.AddComponent<CircleCollider2D>();
            c.isTrigger = true; // 物理押し出しは使わない
            c.radius = Mathf.Max(minGap * 0.5f, 0.10f);
            _col = c;
        }
        else
        {
            // アイテム同士は trigger にして自前ロジックで止める
            _col.isTrigger = true;
        }

        // itemMask が 0 のときは "Item" レイヤーを自動設定
        if (itemMask.value == 0)
        {
            int idx = LayerMask.NameToLayer("Item");
            if (idx >= 0) itemMask = 1 << idx;
        }

        // 今のベルトの向きから初期角度を決める
        InitCurrentAngleFromBelt(firstBelt);

        // スポーン直後に重なっていた場合、少しだけ後ろへずらす
        ResolveInitialOverlap();
    }

    void InitCurrentAngleFromBelt(ConveyorBelt belt)
    {
        if (belt == null)
        {
            _currentAngleDeg = 0f;
            _hasCurrentAngle = true;
            return;
        }

        Vector2 dir = belt.mainOutDirectionWorld;
        if (dir.sqrMagnitude < 1e-6f)
            dir = belt.moveDirection;
        if (dir.sqrMagnitude < 1e-6f)
            dir = belt.transform.up;

        dir.Normalize();
        _currentAngleDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        _hasCurrentAngle = true;
    }

    void FixedUpdate()
    {
        if (_rb == null) return;

        // まず「今どのベルトの上か」を更新
        UpdateCurrentBelt();

        if (_currentBelt == null)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector2.zero;
#else
            _rb.velocity = Vector2.zero;
#endif
            return;
        }

        // このフレームの進行方向（カーブ・合流どちらでもなめらかに曲げる）
        Vector2 moveDir = GetMoveDirection();
        if (moveDir.sqrMagnitude < 1e-6f)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector2.zero;
#else
            _rb.velocity = Vector2.zero;
#endif
            return;
        }
        moveDir.Normalize();

        float speed = (_currentBelt.moveSpeed > 0f) ? _currentBelt.moveSpeed : 2f;
        float step = speed * Time.fixedDeltaTime;          // 予定移動距離
        float castDist = step + minGap;                    // ちょっと先までキャスト
        float allowed = step;                              // 実際に動ける距離

        // === 前方に他アイテムがあるかチェック ===
        int hitCount = CastAhead(moveDir, castDist);
        if (hitCount > 0)
        {
            float nearest = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                var h = _castHits[i];
                if (h.collider == null) continue;

                // 自分自身は無視
                if (h.rigidbody != null && h.rigidbody == _rb)
                    continue;

                var other = h.collider.GetComponentInParent<ItemOnBeltMover>();
                if (other == null || other == this)
                    continue;

                // 方向は無視：前方にアイテムがいるならすべてブロック
                float d = h.distance;
                if (d < nearest)
                    nearest = d;
            }

            if (nearest < float.MaxValue)
            {
                // 前のアイテムの safety 手前までしか動かない
                allowed = Mathf.Max(0f, nearest - safety);
                if (allowed > step) allowed = step;
            }
        }

        // ★ 合流ゲートによる制限（FIFO 弁）
        allowed = ApplyMergeGate(moveDir, allowed, castDist);

        // === 実際に移動 ===
        Vector2 delta = moveDir * allowed;
        Vector2 newPos = _rb.position + delta;

#if UNITY_6000_0_OR_NEWER
        _rb.linearVelocity = delta / Time.fixedDeltaTime;
#else
        _rb.velocity = delta / Time.fixedDeltaTime;
#endif
        _rb.MovePosition(newPos);
    }

    /// <summary>
    /// 前方に合流マスがあり、かつ自分の入口の番がまだ来ていないなら、
    /// その手前で停止するように allowed 距離を制限する。
    /// </summary>
    float ApplyMergeGate(Vector2 moveDir, float currentAllowed, float castDist)
    {
        if (_rb == null || _beltMask == 0) return currentAllowed;

        // 前方方向にベルトをレイキャスト
        RaycastHit2D hit = Physics2D.Raycast(_rb.position, moveDir, castDist, _beltMask);
        if (hit.collider == null) return currentAllowed;

        var belt = hit.collider.GetComponentInParent<ConveyorBelt>();
        if (belt == null) return currentAllowed;

        var gate = belt.GetComponent<BeltMergeController>();
        if (gate == null || !gate.enabled || !gate.HasMultipleInputs)
            return currentAllowed;

        // このアイテムから合流マス中心への方向
        Vector2 dirToCenter = (Vector2)belt.transform.position - _rb.position;

        // そもそもこの方向を入口として扱わないなら、弁の対象外
        if (!gate.IsRelevantEntrance(dirToCenter))
            return currentAllowed;

        // 「自分は弁の前で待機中です」と登録
        gate.RegisterWaiting(dirToCenter);

        // 自分の順番（FIFO）ならそのまま進める
        if (gate.IsEntranceOpen(dirToCenter))
            return currentAllowed;

        // まだ順番が来ていない → 合流マスの少し手前で停止
        float stopDist = hit.distance - safety;
        if (stopDist < 0f) stopDist = 0f;

        if (stopDist < currentAllowed)
            return stopDist;

        return currentAllowed;
    }

    // ベルトの向きが変わるとき、常になめらかに回転させる
    // → カーブでも合流でも同じ動きになる
    public Vector2 GetMoveDirection()
    {
        if (_currentBelt == null) return Vector2.zero;

        // 今のベルトが「最終的に向かってほしい方向」
        Vector2 targetDir = _currentBelt.mainOutDirectionWorld;
        if (targetDir.sqrMagnitude < 1e-6f)
            targetDir = _currentBelt.moveDirection;
        if (targetDir.sqrMagnitude < 1e-6f)
            targetDir = _currentBelt.transform.up;

        targetDir.Normalize();
        float targetAngle = Mathf.Atan2(targetDir.y, targetDir.x) * Mathf.Rad2Deg;

        // 初回だけ、現在角度が未設定ならターゲット角度に合わせる
        if (!_hasCurrentAngle)
        {
            _currentAngleDeg = targetAngle;
            _hasCurrentAngle = true;
        }

        // ●ポイント：
        //   ・「コーナーベルトかどうか」に関係なく
        //     常に cornerTurnSpeedDegPerSec の速度で targetAngle へ回転
        //   → ベルト乗り換え（合流）のときも、カーブと同じ弧を描く
        float maxDelta = cornerTurnSpeedDegPerSec * Time.fixedDeltaTime;
        _currentAngleDeg = Mathf.MoveTowardsAngle(_currentAngleDeg, targetAngle, maxDelta);

        // 角度から方向ベクトルに戻す
        float rad = _currentAngleDeg * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        return dir.normalized;
    }

    // 自分のコライダーを進行方向に Cast して、前方のアイテムを拾う
    int CastAhead(Vector2 dir, float distance)
    {
        if (_col != null)
        {
            var filter = new ContactFilter2D
            {
                useLayerMask = true,
                layerMask = itemMask,
                useTriggers = true
            };
            return _col.Cast(dir.normalized, filter, _castHits, distance);
        }

        // コライダーが無い場合の保険
        return Physics2D.RaycastNonAlloc(_rb.position, dir.normalized, _castHits, distance, itemMask);
    }

    // 「今足元にあるベルト」を決める
    void UpdateCurrentBelt()
    {
        Vector2 pos = transform.position;
        Collider2D[] hits = Physics2D.OverlapCircleAll(pos, BeltDetectRadius, _beltMask);

        bool onCurrent = false;
        ConveyorBelt nearest = null;
        float nearestSqr = float.MaxValue;

        if (hits != null)
        {
            foreach (var h in hits)
            {
                if (!h) continue;
                var b = h.GetComponentInParent<ConveyorBelt>();
                if (!b) continue;

                if (b == _currentBelt)
                    onCurrent = true;

                float d2 = ((Vector2)b.transform.position - pos).sqrMagnitude;
                if (d2 < nearestSqr)
                {
                    nearestSqr = d2;
                    nearest = b;   // 1番近いベルト（なければ null のまま）
                }
            }
        }

        // ★足元のベルトが見つからなかったら nearest は null。
        //   そのまま _currentBelt に代入して「ベルト無し」を反映する。
        if (!onCurrent)
        {
            _currentBelt = nearest;
            // 角度は維持したままなので、新しいベルトに乗り換えた瞬間も
            // カーブと同じロジックで徐々に向きが変わる。
        }
    }

    // スポーン／合流直後で既に重なっている場合、進行方向の逆へ少しずつ退避
    void ResolveInitialOverlap()
    {
        if (_col == null) return;

        var filter = new ContactFilter2D
        {
            useLayerMask = true,
            layerMask = itemMask,
            useTriggers = true
        };

        Collider2D[] cols = new Collider2D[16];
        int count = _col.Overlap(filter, cols);
        if (count <= 0) return;

        Vector2 dir = GetMoveDirection();
        if (dir.sqrMagnitude < 1e-6f)
            dir = Random.insideUnitCircle.normalized;

        float step = Mathf.Max(minGap * 0.5f, 0.10f);

        const int maxIters = 8;
        for (int i = 0; i < maxIters; i++)
        {
            int c = _col.Overlap(filter, cols);
            if (c <= 0) break;

            // 少しだけ後ろに下がる
            _rb.position -= dir * (step * 0.5f);
        }
    }
}

