using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Full-screen colour overlay used for fade-out / fade-in transitions.
///
/// Self-contained — creates its own Canvas and Image at runtime so no
/// prefab or scene setup is required. Just attach to any GameObject
/// and assign it to CheckpointManager.screenFader in the Inspector.
/// </summary>
public class ScreenFader : MonoBehaviour
{
    public Color fadeColour = Color.black;
    public int sortOrder = 100;

    private Image     _overlay;
    private Coroutine _activeTween;

    // LIFECYCLE
    private void Awake()
    {
        BuildOverlay();
        SetAlphaImmediate(0f);   // start fully transparent
    }

#region Public API
    /// <summary>
    /// Fade the screen TO the fadeColour (transparent → opaque).
    /// Returns a Coroutine that can be yielded:
    ///   yield return _fader.FadeOut(0.4f);
    /// Any in-progress fade is cancelled before this one starts.
    /// </summary>
    public Coroutine FadeOut(float duration = 0.4f)
    {
        CancelActiveTween();
        _activeTween = StartCoroutine(TweenAlpha(1f, duration));
        return _activeTween;
    }

    /// <summary>
    /// Fade the screen FROM the fadeColour back to transparent (opaque → transparent).
    /// Returns a Coroutine that can be yielded:
    ///   yield return _fader.FadeIn(0.4f);
    /// Any in-progress fade is cancelled before this one starts.
    /// </summary>
    public Coroutine FadeIn(float duration = 0.4f)
    {
        CancelActiveTween();
        _activeTween = StartCoroutine(TweenAlpha(0f, duration));
        return _activeTween;
    }

    /// <summary>Instantly set overlay opacity (0 = clear, 1 = fully covered).</summary>
    public void SetAlphaImmediate(float alpha)
    {
        CancelActiveTween();
        ApplyAlpha(alpha);
    }
#endregion
    
    private IEnumerator TweenAlpha(float targetAlpha, float duration)
    {
        float startAlpha = _overlay.color.a;
        float elapsed    = 0f;

        // Guard against zero-duration calls causing a divide-by-zero.
        if (duration <= 0f)
        {
            ApplyAlpha(targetAlpha);
            yield break;
        }

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;   // unscaled so it works if Time.timeScale = 0
            float t  = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            ApplyAlpha(Mathf.Lerp(startAlpha, targetAlpha, t));
            yield return null;
        }

        ApplyAlpha(targetAlpha);
        _activeTween = null;
    }

    
#region Helpers
    
    private void ApplyAlpha(float alpha)
    {
        if (_overlay == null) return;
        Color c = fadeColour;
        c.a = Mathf.Clamp01(alpha);
        _overlay.color = c;
    }

    private void CancelActiveTween()
    {
        if (_activeTween != null)
        {
            StopCoroutine(_activeTween);
            _activeTween = null;
        }
    }

    /// <summary>
    /// Programmatically creates a screen-space Canvas with a full-screen
    /// Image child. Called once in Awake so no prefab is needed.
    /// </summary>
    private void BuildOverlay()
    {
        // ── Canvas ───────────────────────────────────────────
        var canvasGO = new GameObject("ScreenFader_Canvas");
        canvasGO.transform.SetParent(transform, false);

        var canvas            = canvasGO.AddComponent<Canvas>();
        canvas.renderMode     = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder   = sortOrder;

        // CanvasScaler keeps the overlay pixel-perfect regardless of resolution.
        var scaler              = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode      = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        canvasGO.AddComponent<GraphicRaycaster>();

        // ── Full-screen Image ────────────────────────────────
        var imageGO = new GameObject("Overlay");
        imageGO.transform.SetParent(canvasGO.transform, false);

        _overlay = imageGO.AddComponent<Image>();
        _overlay.raycastTarget = false;   // don't block UI input

        // Stretch to fill the canvas.
        var rect        = imageGO.GetComponent<RectTransform>();
        rect.anchorMin  = Vector2.zero;
        rect.anchorMax  = Vector2.one;
        rect.offsetMin  = Vector2.zero;
        rect.offsetMax  = Vector2.zero;
    }
#endregion
}