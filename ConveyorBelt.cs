using UnityEngine;

/// <summary>
/// 単純なコンベアーベルト:
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

        var mover = item.GetComponent<ItemOnBeltMover>();
        if (mover == null)
            mover = item.AddComponent<ItemOnBeltMover>();

        mover.Init(this);
    }
}

/// <summary>
/// 「常に足元のコンベアーを調べて、そのコンベアーの向きに合わせて動く」
/// アイテム側の移動制御。
/// ベルトが変わった瞬間に、そのベルトの中心に一度スナップしてから進行方向を切り替えるので、
/// 曲がり角の真ん中で方向転換しているように見えます。
/// </summary>
public class ItemOnBeltMover : MonoBehaviour
{
    ConveyorBelt _currentBelt;
    LayerMask _beltMask;
    Rigidbody2D _rb;

    // 足元のベルト検出用半径（ベルトのコライダーに十分かかるくらい）
    const float BeltDetectRadius = 0.08f;

    public void Init(ConveyorBelt firstBelt)
    {
        _currentBelt = firstBelt;
        _beltMask = (firstBelt != null) ? firstBelt.conveyorLayerMask : ~0;

        _rb = GetComponent<Rigidbody2D>();
        if (_rb != null)
        {
            _rb.gravityScale = 0f;
        }
    }

    void Update()
    {
        // ① 足元のベルトを検出して _currentBelt を更新
        UpdateCurrentBelt();

        if (_currentBelt == null)
        {
            // ベルトの上にいない → 停止
            if (_rb != null) _rb.linearVelocity = Vector2.zero;
            return;
        }

        // ② 現在のベルトの情報から進行方向を決める
        Vector2 dir;

        if (_currentBelt.isCornerBelt)
        {
            // コーナーベルト：中心をはさんで入口方向→出口方向に切り替える
            Vector2 center = _currentBelt.transform.position;

            Vector2 inMove = _currentBelt.mainInDirectionWorld;
            Vector2 outMove = _currentBelt.mainOutDirectionWorld;

            if (inMove.sqrMagnitude < 0.0001f)
                inMove = -outMove; // 念のため

            inMove.Normalize();
            outMove.Normalize();

            Vector2 toPos = (Vector2)transform.position - center;
            float dotIn = Vector2.Dot(toPos, inMove);

            // 中心より「入口側」にいる間は inMove、
            // 中心を越えたら outMove に切り替える
            dir = (dotIn < 0f) ? inMove : outMove;
        }
        else
        {
            // 直線ベルトなど：単純に出口方向
            dir = _currentBelt.mainOutDirectionWorld;
            if (dir.sqrMagnitude < 0.0001f)
                dir = _currentBelt.moveDirection;
        }

        if (dir.sqrMagnitude < 0.0001f)
            dir = _currentBelt.transform.up;
        dir.Normalize();

        float speed = (_currentBelt.moveSpeed > 0f) ? _currentBelt.moveSpeed : 2f;
        Vector2 vel = dir * speed;

        if (_rb != null)
        {
            _rb.linearVelocity = vel;
        }
        else
        {
            transform.position += (Vector3)(vel * Time.deltaTime);
        }
    }

    /// <summary>
    /// 足元近くのコンベアーを調べて _currentBelt を更新する。
    /// 「まだ今のベルト上にいるなら絶対に乗り換えない」ルール。
    /// 今のベルトから完全に離れたあとに、近くの別ベルトがあれば乗り換える。
    /// </summary>
    void UpdateCurrentBelt()
    {
        Vector2 pos = transform.position;

        Collider2D[] hits = Physics2D.OverlapCircleAll(pos, BeltDetectRadius, _beltMask);
        ConveyorBelt nearest = null;
        float nearestSqr = float.MaxValue;
        bool onCurrent = false;

        if (hits != null)
        {
            foreach (var h in hits)
            {
                if (!h) continue;
                var b = h.GetComponentInParent<ConveyorBelt>();
                if (b == null) continue;

                if (b == _currentBelt)
                {
                    // ★ まだ現在のベルトのコライダー上にいる → 絶対に乗り換えない
                    onCurrent = true;
                }

                // 一応、後で使うために一番近いベルトも記録
                float d2 = ((Vector2)b.transform.position - pos).sqrMagnitude;
                if (d2 < nearestSqr)
                {
                    nearestSqr = d2;
                    nearest = b;
                }
            }
        }

        if (onCurrent)
        {
            // まだ今のベルトの上 → 何もしない
            return;
        }

        // ここに来た時点で「今のベルトのコライダーから完全に出ている」

        if (nearest != null)
        {
            // 別のベルトが近くにある → そのベルトに乗り換え
            _currentBelt = nearest;
        }
        else
        {
            // 足元にベルトがない → 完全にベルトから降りた
            _currentBelt = null;
        }
    }
}
