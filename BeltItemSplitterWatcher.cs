using UnityEngine;

/// <summary>
/// ベルト上を流れるアイテムに付ける監視スクリプト。
/// ・足元の ConveyorBelt を調べて、ConveyorDistributor が付いているベルトなら
///   その Distributor に「分割して」と依頼する。
/// ・同じ分配ブロックの上にいる間は 1 回だけ、
///   別の分配ブロックに乗ったら再び分配される。
/// </summary>
[DisallowMultipleComponent]
public class BeltItemSplitterWatcher : MonoBehaviour
{
    // 足元のベルト検出に使う半径（ConveyorBelt 内部と同じくらい）
    const float BeltDetectRadius = 0.08f;

    // 直前に分配を依頼した Distributor
    ConveyorDistributor _lastDistributor;

    void Update()
    {
        Vector2 pos = transform.position;
        Collider2D[] hits = Physics2D.OverlapCircleAll(pos, BeltDetectRadius);
        if (hits == null || hits.Length == 0)
        {
            // どの分配ベルトの上にもいない
            _lastDistributor = null;
            return;
        }

        ConveyorDistributor foundDistributor = null;

        foreach (var h in hits)
        {
            if (!h) continue;

            var belt = h.GetComponentInParent<ConveyorBelt>();
            if (belt == null) continue;

            var dist = belt.GetComponent<ConveyorDistributor>();
            if (dist == null) continue;

            foundDistributor = dist;
            break;
        }

        // 分配ベルトの上にいない
        if (foundDistributor == null)
        {
            _lastDistributor = null;
            return;
        }

        // すでに同じ Distributor で分配済みなら何もしない
        if (foundDistributor == _lastDistributor)
            return;

        // 新しい Distributor に乗った → 分配を依頼
        _lastDistributor = foundDistributor;
        foundDistributor.SplitFromBelt(gameObject);
    }
}