using System.Collections;
using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class DroneAgent : MonoBehaviour
{
    [Header("Move")]
    public float moveSpeed = 4f;
    public float arriveDistance = 0.05f;
    public Vector3 hoverOffset = new Vector3(0, 0.8f, 0);

    bool _busy;
    DroneBuildManager _manager;
    DroneBuildManager.BuildJob _currentJob;
    GameObject _progressBar;   // ����
    float _buildTime;

    public bool IsBusy => _busy;

    public void StartBuildJob(
        DroneBuildManager.BuildJob job,
        DroneBuildManager manager,
        float buildTime,
        GameObject progressBar)
    {
        _busy = true;
        _manager = manager;
        _currentJob = job;
        _buildTime = buildTime;
        _progressBar = progressBar;

        // �o�[������Ȃ�O�ɏo��
        if (_progressBar != null)
        {
            // ���Ԃ�S�[�X�g�̎q�ɂȂ��Ă�̂ŁA�q��SpriteRenderer�������őO�ʂ�
            foreach (var sr in _progressBar.GetComponentsInChildren<SpriteRenderer>(true))
            {
                sr.sortingOrder = 5000;  // ���Ȃ�O
            }
        }

        StopAllCoroutines();
        StartCoroutine(DoJob());
    }

    IEnumerator DoJob()
    {
        Vector3 target = _currentJob.worldPos + hoverOffset;

        // 1) �w��ʒu�ֈړ�
        while ((transform.position - target).sqrMagnitude > arriveDistance * arriveDistance)
        {
            Vector3 dir = (target - transform.position).normalized;
            transform.position += dir * moveSpeed * Time.deltaTime;
            yield return null;
        }

        // 2) ���z�^�C�}�[
        float t = 0f;
        DroneProgressBar bar = null;
        if (_progressBar != null)
            bar = _progressBar.GetComponent<DroneProgressBar>();

        while (t < _buildTime)
        {
            t += Time.deltaTime;
            float p = Mathf.Clamp01(t / _buildTime);
            if (bar != null)
                bar.SetProgress(p);

            // �o�[���J�����������悤�Ɂi2D�ł�Z��]�����������߂��j
            if (_progressBar != null && Camera.main != null)
            {
                _progressBar.transform.rotation = Quaternion.identity;
            }

            yield return null;
        }

        // 3) ������
        _manager.NotifyDroneJobFinished(_currentJob);

        _busy = false;
    }
}
