using UnityEngine;

/// <summary>
/// 【レガシー用スタブ】
///
/// 旧システムでは、ベルト上のアイテムそれぞれにこのスクリプトを付けて、
/// 足元の ConveyorDistributor に「分配して」と依頼していた。
/// 新システムでは、ConveyorBelt.outputs による多出口ベルトで分配を行うため、
/// このコンポーネントは何も処理を行わないダミーとして残している。
///
/// ・既存のプレハブ / シーンにアタッチされていてもゲーム挙動には影響しない。
/// ・順次、プレハブからこのコンポーネントを外していって構わない。
/// </summary>
[DisallowMultipleComponent]
public class BeltItemSplitterWatcher : MonoBehaviour
{
    [Tooltip("将来的に完全削除予定のレガシーコンポーネントです。挙動は一切ありません。")]
    public bool legacyPlaceholder = true;
}
