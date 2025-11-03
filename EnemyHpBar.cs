using UnityEngine;

public class EnemyHpBar : MonoBehaviour
{
    [Header("Target enemy")]
    public EnemyChaseBase2D enemy;

    [Header("Bar parts")]
    public Transform barFill;                  // 横に伸び縮みする部分
    public Vector3 worldOffset = new Vector3(0f, 0.6f, 0f);

    [Header("Rotation")]
    [Tooltip("true ならバーを常にまっすぐに保つ（敵が回転しても回らない）")]
    public bool freezeRotation = true;

    [Header("Color by HP")]
    public bool useColorByHp = true;
    public Color hpHighColor = Color.green;    // HP 100% 付近の色
    public Color hpMidColor = Color.yellow;   // HP 50% 付近の色
    public Color hpLowColor = Color.red;      // HP 0% 付近の色

    SpriteRenderer _fillRenderer;

    void Awake()
    {
        if (!enemy)
            enemy = GetComponentInParent<EnemyChaseBase2D>();

        if (barFill)
            _fillRenderer = barFill.GetComponent<SpriteRenderer>();
    }

    void LateUpdate()
    {
        if (!enemy)
        {
            Destroy(gameObject);
            return;
        }

        // 敵の少し上に追従
        transform.position = enemy.transform.position + worldOffset;

        // 回転を固定（2Dなので常に水平）
        if (freezeRotation)
        {
            transform.rotation = Quaternion.identity;
        }

        float r = enemy.HPRatio;  // ← ここで宣言

        // 長さを左寄せでHP割合で変える
        if (barFill)
        {
            barFill.localScale = new Vector3(r, 1f, 1f);
            barFill.localPosition = new Vector3(-(1f - r) * 0.5f, 0f, 0f);
        }

        // 色も HP割合で変える
        if (useColorByHp && _fillRenderer)
        {
            Color c;

            if (r >= 0.5f)
            {
                // 0.5〜1.0 : 黄 → 緑
                float t = Mathf.InverseLerp(0.5f, 1f, r);
                c = Color.Lerp(hpMidColor, hpHighColor, t);
            }
            else
            {
                // 0.0〜0.5 : 赤 → 黄
                float t = Mathf.InverseLerp(0f, 0.5f, r);
                c = Color.Lerp(hpLowColor, hpMidColor, t);
            }

            _fillRenderer.color = c;
        }
    }
}
