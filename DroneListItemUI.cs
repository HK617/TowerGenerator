using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

public class DroneListItemUI : MonoBehaviour, IPointerClickHandler
{
    public Image background;
    public TMP_Text titleText;
    public TMP_Text subText;
    public Slider progress;

    //ProgressバーのFill部分への参照
    public Image progressFill;

    // ★ このスロットに紐づく Drone と、クリック時に呼ばれるコールバック
    [System.NonSerialized] public DroneWorker boundDrone;
    [System.NonSerialized] public System.Action<DroneListItemUI> onClick;

    public void SetTitle(string t)
    {
        if (titleText) titleText.text = t;
    }

    public void SetSub(string t)
    {
        if (subText) subText.text = t;
    }

    public void SetColor(Color c)
    {
        if (background) background.color = c;
    }

    public void SetProgress(float v)
    {
        if (progress) progress.value = Mathf.Clamp01(v);
    }

    //Progressバーの色変更
    public void SetProgressColor(Color c)
    {
        // 明示的にアサインされていたらそれを使う
        if (progressFill != null)
        {
            progressFill.color = c;
        }
        else if (progress != null)
        {
            // 念のため、Slider の fillRect から Image を拾うフォールバック
            if (progress.fillRect != null)
            {
                var img = progress.fillRect.GetComponent<Image>();
                if (img != null) img.color = c;
            }
            else if (progress.targetGraphic != null)
            {
                progress.targetGraphic.color = c;
            }
        }
    }

    // ★ クリックされたときに UI システムから呼ばれる
    public void OnPointerClick(PointerEventData eventData)
    {
        onClick?.Invoke(this);
    }
}
