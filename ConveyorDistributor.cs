using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ベルト上の分岐ブロック。
/// ・同じ GameObject に ConveyorBelt が付いている前提。
/// ・周囲のベルトのうち「このブロックに向かって流れてくるベルト」をすべて入力とみなし、
///   それ以外のベルトを出力候補とする。
/// ・アイテムがこのブロックに到達したとき、出力候補の中から 1 本を順番に選んで流す。
/// ・アイテムは分岐ブロック中心を通ってから出口ベルトへ滑らかに移動する。
/// ・このブロックの ConveyorBelt は moveDirection=0 に固定し、
///   普通のベルトとしては動かさない（＝デフォルト上方向へ逃さない）。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ConveyorBelt))]
public class ConveyorDistributor : MonoBehaviour
{
    [Header("Neighbor Search")]
    [Tooltip("上下左右にどれだけ離れた位置を隣とみなすか（0.25 推奨）")]
    public float neighborOffset = 0.25f;

    [Tooltip("隣接チェック用の半径")]
    public float neighborProbeRadius = 0.18f;

    [Header("Movement Settings")]
    [Tooltip("分岐ブロック中心 → 出口ベルトへ移動する速さ")]
    public float passSpeed = 3f;

    ConveyorBelt _belt;
    int _nextOutputIndex = 0;

    void Awake()
    {
        _belt = GetComponent<ConveyorBelt>();
    }

    void LateUpdate()
    {
        if (_belt != null)
        {
            // デフォルトのmoveDirectionも出口方向(mainOutDirectionWorld)も完全に無効化
            _belt.moveDirection = Vector2.zero;
            _belt.mainOutDirectionWorld = Vector2.zero;
            _belt.mainInDirectionWorld = Vector2.zero;
        }
    }

    /// <summary>
    /// 分岐ブロックにアイテムが到達したときに呼ぶ。
    /// 「どの方向から来たか」は見ずに、
    /// 周囲のベルトの向きから「入力側ベルト」を判定し、
    /// それ以外の方向へ振り分ける。
    /// </summary>
    public void SplitFromBelt(GameObject item)
    {
        if (item == null) return;

        var mover = item.GetComponent<ItemOnBeltMover>();
        if (mover == null) return;

        // --- 1) 出力候補ベルトを集める（入力側ベルトは除外） ---
        List<ConveyorBelt> outputs = CollectOutputBelts();
        if (outputs.Count == 0)
        {
            // 出口が一つもない構造の場合は、ここで停止（どこにも流さない）
            return;
        }

        // --- 2) ラウンドロビンで出口ベルトを選ぶ ---
        if (_nextOutputIndex >= outputs.Count)
            _nextOutputIndex = 0;

        ConveyorBelt target = outputs[_nextOutputIndex];
        _nextOutputIndex++;

        if (target == null) return;

        // --- 3) アイテムを分岐ブロック中心 → 出口ベルトへ滑らかに移動させる ---
        Vector3 targetPos = target.transform.position;
        if (target.itemSpawnPoint != null)
            targetPos = target.itemSpawnPoint.position;

        mover.StopAllCoroutines();
        mover.StartCoroutine(MoveThroughDistributor(mover, target, targetPos));
    }

    /// <summary>
    /// 分岐ブロック中心 → 出口ベルトへ滑らかに移動。
    /// </summary>
    IEnumerator MoveThroughDistributor(ItemOnBeltMover mover, ConveyorBelt targetBelt, Vector3 targetPos)
    {
        if (mover == null || targetBelt == null) yield break;

        // まずは既存の速度を止めておく
        var rb = mover.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = Vector2.zero;
#else
            rb.velocity = Vector2.zero;
#endif
        }

        Vector3 startPos = mover.transform.position;
        Vector3 mid = transform.position;

        float t = 0f;
        // 中心へ
        while (t < 1f)
        {
            t += Time.deltaTime * passSpeed;
            mover.transform.position = Vector3.Lerp(startPos, mid, t);
            yield return null;
        }

        t = 0f;
        // 出口へ
        while (t < 1f)
        {
            t += Time.deltaTime * passSpeed;
            mover.transform.position = Vector3.Lerp(mid, targetPos, t);
            yield return null;
        }

        // ★ ここで新しいベルトとして初期化
        mover.Init(targetBelt);

        // ★ すぐにベルト方向の速度を与えておく
        if (rb != null)
        {
            Vector2 dir = targetBelt.mainOutDirectionWorld;
            if (dir.sqrMagnitude < 0.0001f)
                dir = targetBelt.moveDirection;
            if (dir.sqrMagnitude < 0.0001f)
                dir = targetBelt.transform.up;

            dir.Normalize();
#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = dir * targetBelt.moveSpeed;
#else
            rb.velocity = dir * targetBelt.moveSpeed;
#endif
        }
    }


    /// <summary>
    /// 周囲の上下左右にある ConveyorBelt のうち、
    /// 「この分岐ブロックの中心に向かってアイテムを流しているベルト」を入力として扱い、
    /// それ以外（中心から離れる or 横方向）のベルトを出力候補として返す。
    /// </summary>
    List<ConveyorBelt> CollectOutputBelts()
    {
        var outputs = new List<ConveyorBelt>(4);

        Vector3 center = transform.position;
        Vector2[] dirs = { Vector2.up, Vector2.right, Vector2.down, Vector2.left };

        foreach (var dir in dirs)
        {
            Vector2 checkPos = (Vector2)center + dir.normalized * neighborOffset;
            Collider2D[] hits = Physics2D.OverlapCircleAll(checkPos, neighborProbeRadius);
            if (hits == null || hits.Length == 0)
                continue;

            foreach (var h in hits)
            {
                if (!h) continue;

                var belt = h.GetComponentInParent<ConveyorBelt>();
                if (belt == null) continue;

                // ベルトの「アイテムを流す方向」（出口方向）
                Vector2 beltDir = belt.moveDirection;
                if (beltDir.sqrMagnitude < 0.0001f)
                    beltDir = belt.transform.up; // 念のための保険
                beltDir.Normalize();

                // ベルト中心 → 分岐中心 のベクトル
                Vector2 fromBeltToCenter = (Vector2)center - (Vector2)belt.transform.position;
                if (fromBeltToCenter.sqrMagnitude < 0.0001f)
                    fromBeltToCenter = -beltDir; // 近すぎる場合は逆向きを「中心方向」とみなす
                fromBeltToCenter.Normalize();

                float dot = Vector2.Dot(beltDir, fromBeltToCenter);

                // dot > 0.7 なら「中心に向かってアイテムを流している」＝入力ベルト
                bool isInput = dot > 0.7f;

                // 入力ベルトは出力から除外、それ以外は出力候補
                if (!isInput)
                {
                    if (!outputs.Contains(belt))
                        outputs.Add(belt);
                }

                // 同じ方向に複数ベルトがあっても、最初の一つだけ見れば十分
                break;
            }
        }

        return outputs;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Vector3 c = transform.position;
        Vector2[] dirs = { Vector2.up, Vector2.right, Vector2.down, Vector2.left };
        foreach (var d in dirs)
        {
            Vector3 p = c + (Vector3)(d.normalized * neighborOffset);
            Gizmos.DrawWireSphere(p, neighborProbeRadius);
        }
    }
#endif
}