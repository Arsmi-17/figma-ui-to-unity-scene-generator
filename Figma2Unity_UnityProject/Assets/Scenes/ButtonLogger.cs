using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class ButtonLogger : MonoBehaviour
{
    [SerializeField] private Button[] buttons;
    [SerializeField] private bool autoFindButtons = true;
    [SerializeField] private bool drawButtonGizmos = true;
    [SerializeField] private bool drawGameViewOverlay = true;
    [SerializeField] private bool logPointerRaycasts = true;
    [SerializeField] private Color gizmoColor = new Color(0f, 1f, 0.25f, 0.9f);

    private static Texture2D lineTexture;

    private void Awake()
    {
        RefreshButtons();
    }

    private void OnEnable()
    {
        RefreshButtons();
        BindButtons();
    }

    [ContextMenu("Refresh Buttons")]
    private void RefreshButtons()
    {
        if (!autoFindButtons && buttons != null && buttons.Length > 0)
            return;

        buttons = FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        System.Array.Sort(buttons, (a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));
    }

    private void BindButtons()
    {
        if (buttons == null) return;

        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (button == null) continue;

            ButtonPointerLogger pointerLogger = button.GetComponent<ButtonPointerLogger>();
            if (pointerLogger == null)
                pointerLogger = button.gameObject.AddComponent<ButtonPointerLogger>();

            pointerLogger.SetButtonName(button.name);
        }

        Debug.Log($"ButtonLogger bound {buttons.Length} buttons.");
    }

    private void Update()
    {
        if (!logPointerRaycasts)
            return;

        if (TryGetMouseDownPosition(out Vector2 position))
            LogRaycastStack(position);
    }

    private static bool TryGetMouseDownPosition(out Vector2 position)
    {
        position = default;
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            if (!mouse.leftButton.wasPressedThisFrame)
                return false;

            position = mouse.position.ReadValue();
            return true;
        }

        try
        {
            if (!Input.GetMouseButtonDown(0))
                return false;

            position = Input.mousePosition;
            return true;
        }
        catch (System.InvalidOperationException)
        {
            return false;
        }
    }

    private static void LogRaycastStack(Vector2 position)
    {
        if (EventSystem.current == null)
        {
            Debug.Log("ButtonLogger raycast: no EventSystem in scene.");
            return;
        }

        var pointerData = new PointerEventData(EventSystem.current) { position = position };
        var results = new System.Collections.Generic.List<RaycastResult>();
        EventSystem.current.RaycastAll(pointerData, results);

        if (results.Count == 0)
        {
            Debug.Log($"ButtonLogger raycast at {position}: no UI hit.");
            return;
        }

        int count = Mathf.Min(results.Count, 8);
        string message = $"ButtonLogger raycast at {position}: ";
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                message += " -> ";
            message += results[i].gameObject.name;
        }

        Debug.Log(message);
    }

    private void OnDrawGizmos()
    {
        if (!drawButtonGizmos)
            return;

        RefreshButtons();
        if (buttons == null) return;

        Gizmos.color = gizmoColor;
        foreach (Button button in buttons)
        {
            if (button == null) continue;
            RectTransform rect = button.transform as RectTransform;
            if (rect == null) continue;

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            for (int i = 0; i < 4; i++)
                Gizmos.DrawLine(corners[i], corners[(i + 1) % 4]);
        }
    }

    private void OnGUI()
    {
        if (!drawGameViewOverlay || !Application.isPlaying)
            return;

        if (buttons == null || buttons.Length == 0)
            RefreshButtons();

        if (buttons == null)
            return;

        foreach (Button button in buttons)
        {
            if (button == null || !button.gameObject.activeInHierarchy)
                continue;

            RectTransform rect = button.transform as RectTransform;
            if (rect == null)
                continue;

            DrawButtonOverlay(button, rect);
        }
    }

    private void DrawButtonOverlay(Button button, RectTransform rect)
    {
        Camera eventCamera = GetCanvasCamera(button);
        Vector3[] worldCorners = new Vector3[4];
        rect.GetWorldCorners(worldCorners);

        Vector2[] points = new Vector2[4];
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(eventCamera, worldCorners[i]);
            points[i] = new Vector2(screen.x, Screen.height - screen.y);
        }

        for (int i = 0; i < points.Length; i++)
            DrawLine(points[i], points[(i + 1) % points.Length], gizmoColor, 2f);

        GUI.color = gizmoColor;
        GUI.Label(new Rect(points[1].x + 4f, points[1].y + 2f, 260f, 24f), button.name);
        GUI.color = Color.white;
    }

    private static Camera GetCanvasCamera(Button button)
    {
        Canvas canvas = button.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            return null;

        return canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
    }

    private static void DrawLine(Vector2 start, Vector2 end, Color color, float width)
    {
        if (lineTexture == null)
        {
            lineTexture = new Texture2D(1, 1);
            lineTexture.SetPixel(0, 0, Color.white);
            lineTexture.Apply();
        }

        Matrix4x4 oldMatrix = GUI.matrix;
        Color oldColor = GUI.color;

        Vector2 delta = end - start;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(angle, start);
        GUI.DrawTexture(new Rect(start.x, start.y - width * 0.5f, delta.magnitude, width), lineTexture);

        GUI.matrix = oldMatrix;
        GUI.color = oldColor;
    }
}

public class ButtonPointerLogger : MonoBehaviour, IPointerEnterHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
{
    private Button button;
    private string buttonName;
    private bool listenerBound;

    public void SetButtonName(string value)
    {
        buttonName = value;
        EnsureBound();
    }

    private void OnEnable()
    {
        EnsureBound();
    }

    private void OnDisable()
    {
        if (button != null && listenerBound)
            button.onClick.RemoveListener(LogButtonClick);

        listenerBound = false;
    }

    private void EnsureBound()
    {
        button = GetComponent<Button>();
        if (button == null || listenerBound)
            return;

        if (string.IsNullOrWhiteSpace(buttonName))
            buttonName = gameObject.name;

        button.onClick.AddListener(LogButtonClick);
        listenerBound = true;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        Debug.Log($"Pointer enter: {buttonName} ({GetRaycastTargetName(eventData)})");
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        Debug.Log($"Pointer down: {buttonName} ({GetRaycastTargetName(eventData)})");
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        Debug.Log($"Pointer up: {buttonName} ({GetRaycastTargetName(eventData)})");
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        Debug.Log($"Pointer click: {buttonName} ({GetRaycastTargetName(eventData)})");
    }

    private void LogButtonClick()
    {
        Debug.Log($"Button clicked: {buttonName}");
    }

    private static string GetRaycastTargetName(PointerEventData eventData)
    {
        GameObject target = eventData.pointerCurrentRaycast.gameObject;
        return target != null ? $"raycast target: {target.name}" : "no raycast target";
    }
}
