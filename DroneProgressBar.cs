using UnityEngine;

public class DroneProgressBar : MonoBehaviour
{
    [Range(0f, 1f)]
    public float progress = 0f;

    [Tooltip("伸ばす部分のTransform")]
    public Transform fill;

    [Tooltip("横幅の最大値(ローカル)")]
    public float maxWidth = 1.2f;

    void Awake()
    {
        if (fill == null && transform.childCount > 0)
            fill = transform.GetChild(0);
    }

    public void SetProgress(float p)
    {
        progress = Mathf.Clamp01(p);
        if (fill != null)
        {
            fill.localScale = new Vector3(progress * maxWidth, 1f, 1f);
        }
    }

    void LateUpdate()
    {
        // ゴーストの子になっていると一緒に回転して見えなくなるので
        // 2Dとして常に回転0にする
        transform.rotation = Quaternion.identity;
    }
}
