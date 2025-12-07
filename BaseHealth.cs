using UnityEngine;
using UnityEngine.Events;

public class BaseHealth : MonoBehaviour
{
    [Header("HP")]
    public float maxHP = 100f;

    [Tooltip("現在 HP（デバッグ用に Inspector でも見えるようにしておく）")]
    public float currentHP;

    [Header("Events")]
    [Tooltip("Base が破壊されたときに呼ばれるイベント（ゲームオーバー画面などをここに繋ぐ）")]
    public UnityEvent onBaseDestroyed;

    void Awake()
    {
        currentHP = maxHP;
    }

    public void TakeDamage(float damage)
    {
        if (currentHP <= 0f) return;

        float d = Mathf.Max(0f, damage);
        if (d <= 0f) return;

        currentHP -= d;
        if (currentHP <= 0f)
        {
            currentHP = 0f;
            Die();
        }
    }

    void Die()
    {
        Debug.Log("[BaseHealth] Base destroyed!");

        // ここでゲームオーバー処理などを呼ぶ
        onBaseDestroyed?.Invoke();

        // 必要なら Base を消す
        // Destroy(gameObject);
    }
}
