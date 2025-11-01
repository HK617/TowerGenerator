using UnityEngine;
using UnityEngine.InputSystem;

public class PreviewRaidInput : MonoBehaviour
{
    public EnemySpawnerRaid spawner;

    InputAction previewAction;

    void Awake()
    {
        previewAction = new InputAction("PreviewRaid", InputActionType.Button, "<Keyboard>/p");
    }

    void OnEnable()
    {
        previewAction.Enable();
        previewAction.performed += OnPerformed;
    }

    void OnDisable()
    {
        previewAction.performed -= OnPerformed;
        previewAction.Disable();
    }

    void OnPerformed(InputAction.CallbackContext ctx)
    {
        if (spawner != null)
            spawner.TogglePreview();
    }
}
