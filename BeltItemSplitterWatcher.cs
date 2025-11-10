using UnityEngine;

/// <summary>
/// ベルト上を流れるアイテムに付ける監視スクリプト。
/// ・足元の ConveyorBelt を調べて、ConveyorDistributor が付いているベルトなら
///   その Distributor に「分割して」と依頼する。
/// </summary>
[DisallowMultipleComponent]
public class BeltItemSplitterWatcher : MonoBehaviour
{
    // 足元のベルト検出に使う半径（ConveyorBelt 内部と同じくらい）
    const float BeltDetectRadius = 0.08f;

    bool _alreadySplitted = false;

    void Update()
    {
        if (_alreadySplitted) return;

        Vector2 pos = transform.position;
        Collider2D[] hits = Physics2D.OverlapCircleAll(pos, BeltDetectRadius);
        if (hits == null || hits.Length == 0) return;

        foreach (var h in hits)
        {
            if (!h) continue;

            var belt = h.GetComponentInParent<ConveyorBelt>();
            if (belt == null) continue;

            var dist = belt.GetComponent<ConveyorDistributor>();
            if (dist == null) continue;

            _alreadySplitted = true;
            dist.SplitFromBelt(gameObject);
            break;
        }
    }
}
