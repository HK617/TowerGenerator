using UnityEngine;

/// <summary>
/// 簡易コンベア:
/// ・AddItem でアイテムプレハブを受け取る
/// ・アイテムには ItemOnBeltMover を付け、
///   「コンベアーの上にいる間だけ」進行方向に動かす。
/// ・進行方向はコンベアー本体の回転（transform.right）から自動決定。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider2D))]
public class ConveyorBelt : MonoBehaviour
{
    [Header("Move")]
    [Tooltip("アイテムを流す方向（ワールド空間）。Awake時に transform.right から自動設定されます。")]
    public Vector2 moveDirection = Vector2.right;

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

    Collider2D _col;

    void Awake()
    {
        _col = GetComponent<Collider2D>();

        // ★ 回転に合わせて自動的に moveDirection を設定
        // （プレハブを回転させておけば、その向きが「進行方向」になる）
        moveDirection = (Vector2)transform.up;

        // デフォルトで自分のレイヤーをコンベアーレイヤーとみなす（空なら）
        if (conveyorLayerMask.value == 0)
        {
            conveyorLayerMask = 1 << gameObject.layer;
        }
    }

    void Reset()
    {
        moveDirection = Vector2.right;
        moveSpeed = 2f;
        conveyorLayerMask = 1 << gameObject.layer;
    }

    /// <summary>
    /// ドリルや他の機械から「アイテムを受け取る」ためのAPI
    /// </summary>
    public void AddItem(GameObject itemPrefab)
    {
        if (itemPrefab == null)
        {
            Debug.LogWarning("[ConveyorBelt] AddItem: itemPrefab が null です。", this);
            return;
        }

        Vector3 spawnPos = itemSpawnPoint != null
            ? itemSpawnPoint.position
            : transform.position;

        var item = Instantiate(itemPrefab, spawnPos, Quaternion.identity);

        if (parentItemsToBelt)
            item.transform.SetParent(transform, worldPositionStays: true);

        // 移動制御用コンポーネントを付与
        var mover = item.GetComponent<ItemOnBeltMover>();
        if (mover == null)
            mover = item.AddComponent<ItemOnBeltMover>();

        mover.Init(this);
    }
}

/// <summary>
/// 「コンベアーの上にいる間だけ」アイテムを動かす制御クラス。
/// Rigidbody2D があれば velocity、無ければ Transform で移動する。
/// </summary>
public class ItemOnBeltMover : MonoBehaviour
{
    ConveyorBelt _belt;
    Rigidbody2D _rb;
    LayerMask _conveyorMask;

    public void Init(ConveyorBelt belt)
    {
        _belt = belt;
        _conveyorMask = (_belt != null) ? _belt.conveyorLayerMask : ~0;

        _rb = GetComponent<Rigidbody2D>();
        if (_rb != null)
        {
            // 重力で落ちてほしくないなら 0 にする
            _rb.gravityScale = 0f;
        }
    }

    void Update()
    {
        if (_belt == null)
        {
            if (_rb != null) _rb.linearVelocity = Vector2.zero;
            enabled = false;
            return;
        }

        bool onBelt = IsOnBelt();
        if (!onBelt)
        {
            // コンベアーの上にいない → 停止
            if (_rb != null) _rb.linearVelocity = Vector2.zero;
            return;
        }

        // ベルトの現在の向きと速度を毎フレーム参照
        Vector2 dir = (_belt.moveDirection.sqrMagnitude > 0.0001f)
            ? _belt.moveDirection.normalized
            : (Vector2)_belt.transform.up;

        float speed = (_belt.moveSpeed > 0f)
            ? _belt.moveSpeed
            : 2f;

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
    /// 自分の足元に「コンベアーレイヤーの Collider2D」があるか判定
    /// </summary>
    bool IsOnBelt()
    {
        Vector2 pos2D = transform.position;
        var hit = Physics2D.OverlapPoint(pos2D, _conveyorMask);
        return (hit != null);
    }
}