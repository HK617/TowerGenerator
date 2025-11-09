using UnityEngine;

/// <summary>
/// ドリル用の挙動:
/// ・足元に ResourceBlock / ResourceMarker があるか調べる
/// ・一定間隔ごとに資源アイテムを生産
/// ・近くの ConveyorBelt を探して、その上にアイテムを載せる
/// </summary>
[DisallowMultipleComponent]
public class DrillBehaviour : MonoBehaviour
{
    [Header("Production")]
    [Tooltip("何秒ごとに1個アイテムを生産するか")]
    public float produceInterval = 3f;

    [Tooltip("生産するアイテムのプレハブ（鉱石など）")]
    public GameObject productPrefab;

    [Header("Search Settings")]
    [Tooltip("足元のResourceBlockを探す半径")]
    public float resourceSearchRadius = 0.2f;

    [Tooltip("周囲のベルトを探す半径")]
    public float beltSearchRadius = 1.0f;

    [Tooltip("ベルト検出用のLayerMask（未指定なら全レイヤー）")]
    public LayerMask beltLayerMask = ~0;

    [Tooltip("足元資源検出用のLayerMask（未指定なら全レイヤー）")]
    public LayerMask resourceLayerMask = ~0;

    float _timer;
    ResourceMarker _linkedResource;
    ConveyorBelt _linkedBelt;

    void Start()
    {
        // 起動時に一度だけ足元の資源を探してみる
        FindResourceBelow();
        // ついでに近くのベルトも探しておく
        FindNearestBelt();
    }

    void Update()
    {
        if (productPrefab == null) return;

        // 足元に資源が無ければ何もしない
        if (_linkedResource == null)
        {
            // 途中から資源が出てくる可能性も一応考えて、毎フレーム軽く探す
            FindResourceBelow();
            if (_linkedResource == null) return;
        }

        _timer += Time.deltaTime;
        if (_timer >= produceInterval)
        {
            _timer = 0f;
            ProduceOnce();
        }
    }

    void ProduceOnce()
    {
        // ベルトが未キャッシュなら探す
        if (_linkedBelt == null)
            FindNearestBelt();

        if (_linkedBelt == null)
        {
            // ベルトが無いならまだ流せない → 何もしない
            return;
        }

        _linkedBelt.AddItem(productPrefab);
    }

    // 足元の ResourceBlock / ResourceMarker を検出
    void FindResourceBelow()
    {
        Vector2 pos = transform.position;
        var hits = Physics2D.OverlapCircleAll(pos, resourceSearchRadius, resourceLayerMask);

        ResourceMarker found = null;

        foreach (var h in hits)
        {
            // 子オブジェクトに ResourceBlock タグが付いているはずなので、そこから親をたどる
            if (h.CompareTag("ResourceBlock"))
            {
                found = h.GetComponentInParent<ResourceMarker>();
                if (found != null) break;
            }
            else
            {
                // 直接 ResourceMarker を持っている場合も一応見る
                var m = h.GetComponent<ResourceMarker>();
                if (m != null)
                {
                    found = m;
                    break;
                }
            }
        }

        _linkedResource = found;
    }

    // 周囲の ConveyorBelt を探し、一番近いものをリンク
    void FindNearestBelt()
    {
        Vector2 pos = transform.position;
        var hits = Physics2D.OverlapCircleAll(pos, beltSearchRadius, beltLayerMask);

        float bestDist2 = float.MaxValue;
        ConveyorBelt best = null;

        foreach (var h in hits)
        {
            // タグで絞ってもいいし、コンポーネントで絞ってもOK
            var belt = h.GetComponentInParent<ConveyorBelt>();
            if (belt == null) continue;

            float d2 = (belt.transform.position - transform.position).sqrMagnitude;
            if (d2 < bestDist2)
            {
                bestDist2 = d2;
                best = belt;
            }
        }

        _linkedBelt = best;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        // デバッグ用に検索範囲を表示
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, resourceSearchRadius);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, beltSearchRadius);
    }
#endif
}
