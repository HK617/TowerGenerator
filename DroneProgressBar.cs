using UnityEngine;

public class DroneProgressBar : MonoBehaviour
{
    [Range(0f, 1f)]
    public float progress = 0f;

    [Tooltip("�L�΂�������Transform")]
    public Transform fill;

    [Tooltip("�����̍ő�l(���[�J��)")]
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
        // �S�[�X�g�̎q�ɂȂ��Ă���ƈꏏ�ɉ�]���Č����Ȃ��Ȃ�̂�
        // 2D�Ƃ��ď�ɉ�]0�ɂ���
        transform.rotation = Quaternion.identity;
    }
}
