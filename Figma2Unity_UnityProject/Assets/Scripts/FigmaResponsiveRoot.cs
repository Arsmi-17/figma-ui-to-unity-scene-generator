using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class FigmaResponsiveRoot : MonoBehaviour
{
    public enum FitMode
    {
        FitInside,
        Cover,
        Stretch
    }

    public Vector2 designSize = new Vector2(1920f, 1080f);
    public FitMode fitMode = FitMode.FitInside;

    private RectTransform rectTransform;
    private RectTransform parentRectTransform;
    private Vector2 lastParentSize;
    private Vector2 lastDesignSize;
    private FitMode lastFitMode;

    private void OnEnable()
    {
        CacheTransforms();
        Apply();
    }

    private void OnValidate()
    {
        CacheTransforms();
        Apply();
    }

    private void OnTransformParentChanged()
    {
        CacheTransforms();
        Apply();
    }

    private void OnRectTransformDimensionsChange()
    {
        Apply();
    }

    private void LateUpdate()
    {
        if (parentRectTransform == null)
            CacheTransforms();

        Vector2 parentSize = parentRectTransform != null ? parentRectTransform.rect.size : Vector2.zero;
        if (parentSize != lastParentSize || designSize != lastDesignSize || fitMode != lastFitMode)
            Apply();
    }

    private void CacheTransforms()
    {
        rectTransform = transform as RectTransform;
        parentRectTransform = rectTransform != null ? rectTransform.parent as RectTransform : null;
    }

    public void Apply()
    {
        if (rectTransform == null)
            rectTransform = transform as RectTransform;
        if (rectTransform == null)
            return;

        if (parentRectTransform == null)
            parentRectTransform = rectTransform.parent as RectTransform;
        if (parentRectTransform == null)
            return;

        Vector2 parentSize = parentRectTransform.rect.size;
        Vector2 safeDesignSize = new Vector2(Mathf.Max(1f, designSize.x), Mathf.Max(1f, designSize.y));

        rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.anchoredPosition = Vector2.zero;
        rectTransform.sizeDelta = safeDesignSize;

        Vector3 scale = Vector3.one;
        if (fitMode == FitMode.Stretch)
        {
            scale = new Vector3(
                Mathf.Max(0.0001f, parentSize.x / safeDesignSize.x),
                Mathf.Max(0.0001f, parentSize.y / safeDesignSize.y),
                1f);
        }
        else
        {
            float scaleX = parentSize.x / safeDesignSize.x;
            float scaleY = parentSize.y / safeDesignSize.y;
            float uniformScale = fitMode == FitMode.Cover ? Mathf.Max(scaleX, scaleY) : Mathf.Min(scaleX, scaleY);
            scale = Vector3.one * Mathf.Max(0.0001f, uniformScale);
            scale.z = 1f;
        }

        rectTransform.localScale = scale;
        lastParentSize = parentSize;
        lastDesignSize = designSize;
        lastFitMode = fitMode;
    }
}
