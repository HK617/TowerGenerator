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

    // ★ クリックされたときに UI システムから呼ばれる
    public void OnPointerClick(PointerEventData eventData)
    {
        onClick?.Invoke(this);
    }
}
