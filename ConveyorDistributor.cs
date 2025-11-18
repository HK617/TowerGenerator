using UnityEngine;

/// <summary>
/// 【レガシー用スタブ】
///
/// 旧システムでは「分配機」専用のロジックを持っていたが、
/// 現在は「ConveyorBelt.outputs による多出口ベルト」に統合されたため、
/// このコンポーネントは動作を持たないダミーとして残している。
///
/// ・既存のプレハブ / シーンにアタッチされていてもゲーム挙動には影響しない。
/// ・順次、プレハブからこのコンポーネントを外していって構わない。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ConveyorBelt))]
public class ConveyorDistributor : MonoBehaviour
{
    [Tooltip("将来的に完全削除予定のレガシーコンポーネントです。挙動は一切ありません。")]
    public bool legacyPlaceholder = true;

#if UNITY_EDITOR
    void OnValidate()
    {
        if (legacyPlaceholder)
        {
            Debug.Log(
                $"[ConveyorDistributor] このコンポーネントはレガシー用スタブです。" +
                $" 分配ロジックは ConveyorBelt.outputs に統合されています。 ({name})",
                this
            );
        }
    }
#endif
}
