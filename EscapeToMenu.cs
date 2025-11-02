using UnityEngine;
using UnityEngine.InputSystem;

public class EscapeToMenu : MonoBehaviour
{
    [Header("UI References")]
    public GameObject startMenuRoot;   // StartMenuUI を含むオブジェクト
    public GameObject gameRoot;        // ゲーム中のオブジェクトをまとめたルート

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.escapeKey.wasPressedThisFrame)
        {
            // 1) いまのスロットにセーブ
            if (SaveLoadManager.Instance != null)
            {
                SaveLoadManager.Instance.Save();

                // 2) ロード時に古いワールドが残らないように、ここで中身を初期化
                SaveLoadManager.Instance.ResetCurrentWorld();
            }

            // 3) ゲーム本体は非表示にする
            if (gameRoot) gameRoot.SetActive(false);

            // 4) スタートメニューを表示する
            if (startMenuRoot) startMenuRoot.SetActive(true);

            // 5) いまセーブしたぶんも含めてリストを描き直す
            var ui = startMenuRoot ? startMenuRoot.GetComponentInChildren<StartMenuUI>() : null;
            if (ui != null)
                ui.RefreshSaveButtons();

            Debug.Log("[EscapeToMenu] Saved, reset world, and returned to StartMenu.");
        }
    }
}
