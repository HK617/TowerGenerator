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
    GameObject _progressBar;   // 実体
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

        // バーがあるなら前に出す
        if (_progressBar != null)
        {
            // たぶんゴーストの子になってるので、子のSpriteRendererを強制で前面へ
            foreach (var sr in _progressBar.GetComponentsInChildren<SpriteRenderer>(true))
            {
                sr.sortingOrder = 5000;  // かなり前
            }
        }

        StopAllCoroutines();
        StartCoroutine(DoJob());
    }

    IEnumerator DoJob()
    {
        Vector3 target = _currentJob.worldPos + hoverOffset;

        // 1) 指定位置へ移動
        while ((transform.position - target).sqrMagnitude > arriveDistance * arriveDistance)
        {
            Vector3 dir = (target - transform.position).normalized;
            transform.position += dir * moveSpeed * Time.deltaTime;
            yield return null;
        }

        // 2) 建築タイマー
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

            // バーがカメラを向くように（2DでもZ回転しちゃったら戻す）
            if (_progressBar != null && Camera.main != null)
            {
                _progressBar.transform.rotation = Quaternion.identity;
            }

            yield return null;
        }

        // 3) 完了報告
        _manager.NotifyDroneJobFinished(_currentJob);

        _busy = false;
    }
}
