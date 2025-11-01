using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class DroneListItemUI : MonoBehaviour
{
    public Image background;
    public TMP_Text titleText;
    public TMP_Text subText;
    public Slider progress;

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
}
