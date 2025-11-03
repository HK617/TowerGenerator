using UnityEngine;
using UnityEngine.InputSystem;

public class EscapeToMenu : MonoBehaviour
{
    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.escapeKey.wasPressedThisFrame)
        {
            if (SaveLoadManager.Instance != null)
            {
                // 必要ならここでセーブ
                SaveLoadManager.Instance.Save();
                // タイトルシーンに戻る（Gameシーンはまるごと破棄される）
                SaveLoadManager.Instance.ReturnToTitle();
            }
        }
    }
}
