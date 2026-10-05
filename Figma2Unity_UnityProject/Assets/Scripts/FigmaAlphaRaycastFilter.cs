using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class FigmaAlphaRaycastFilter : MonoBehaviour, ICanvasRaycastFilter
{
    [SerializeField] private Image targetImage;
    [SerializeField, Range(0f, 1f)] private float alphaThreshold = 0.1f;

    public Image TargetImage
    {
        get => targetImage;
        set => targetImage = value;
    }

    public float AlphaThreshold
    {
        get => alphaThreshold;
        set => alphaThreshold = Mathf.Clamp01(value);
    }

    public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
    {
        Image image = targetImage != null ? targetImage : GetComponent<Image>();
        if (image == null || image.sprite == null || image.sprite.texture == null)
            return true;

        RectTransform rectTransform = image.rectTransform;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, screenPoint, eventCamera, out Vector2 localPoint))
            return false;

        Rect rect = rectTransform.rect;
        if (!rect.Contains(localPoint))
            return false;

        float u = Mathf.InverseLerp(rect.xMin, rect.xMax, localPoint.x);
        float v = Mathf.InverseLerp(rect.yMin, rect.yMax, localPoint.y);

        Sprite sprite = image.sprite;
        Rect textureRect = sprite.textureRect;
        int x = Mathf.Clamp(Mathf.FloorToInt(textureRect.x + u * textureRect.width), 0, sprite.texture.width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(textureRect.y + v * textureRect.height), 0, sprite.texture.height - 1);

        try
        {
            return sprite.texture.GetPixel(x, y).a >= alphaThreshold;
        }
        catch
        {
            return true;
        }
    }
}
