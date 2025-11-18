using UnityEngine;

/// <summary>
/// 【レガシー用スタブ】BeltMergeController
///
/// 旧システムでは、合流コンベアーで各入口の FIFO 制御を行う役割だったが、
/// 新しいスロットベースの ConveyorBelt ロジックでは、
/// ベルト同士が単純に「空いているかどうか」で押し出し制御を行うため、
/// このクラスは動作を持たないダミーとして残している。
///
/// ・既存のプレハブ / シーンにアタッチされていてもゲーム挙動には影響しない。
/// ・順次、プレハブからこのコンポーネントを外していって構わない。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ConveyorBelt))]
public class BeltMergeController : MonoBehaviour
{
    [Tooltip("将来的に完全削除予定のレガシーコンポーネントです。挙動は一切ありません。")]
    public bool legacyPlaceholder = true;

#if UNITY_EDITOR
    void OnValidate()
    {
        if (legacyPlaceholder)
        {
            Debug.Log(
                $"[BeltMergeController] このコンポーネントはレガシー用スタブです。" +
                $" 合流ロジックは ConveyorBelt のスロット制御に統合されています。 ({name})",
                this
            );
        }
    }
#endif
}
