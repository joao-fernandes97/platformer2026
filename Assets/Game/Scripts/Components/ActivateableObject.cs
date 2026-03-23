using System.Collections;
using UnityEngine;

/// <summary>
/// An object (platform, wall, bridge…) that can be activated or deactivated,
/// optionally with a smooth scale/fade transition.
///
/// ═══════════════════════════════════════════════════════════
///  QUICK SETUP
/// ═══════════════════════════════════════════════════════════
///  1. Attach to the platform / object you want to appear.
///  2. On the ActivationButton, drag this object into the
///     OnActivated UnityEvent → select ActivatableObject.Activate().
///  3. Optionally bind OnDeactivated → ActivatableObject.Deactivate().
///
///  Or subscribe from code:
///      button.OnStateChanged += myActivatable.SetActivated;
///
/// ═══════════════════════════════════════════════════════════
///  APPEARANCE STYLES
/// ═══════════════════════════════════════════════════════════
///  Instant     — SetActive on/off, no animation.
///  Scale       — scales from zero → full size (or reverse).
///  Fade        — fades SpriteRenderer alpha in / out.
///                (Also fades a child named "Shadow" if present.)
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION (NGO server-auth)
/// ═══════════════════════════════════════════════════════════
///  1. Inherit NetworkBehaviour.
///  2. Replace _isActive with a NetworkVariable<bool>.
///  3. Drive Activate / Deactivate from the server only.
///  4. Visual tween (coroutine) stays client-side — triggered by the
///     NetworkVariable's OnValueChanged callback.
/// </summary>
public class ActivatableObject : MonoBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    public enum AppearStyle { Instant, Scale, Fade }

    [Header("State")]
    [Tooltip("Is the object active (visible / solid) at start?")]
    public bool startActive = false;

    [Header("Appearance")]
    public AppearStyle appearStyle  = AppearStyle.Scale;

    [Tooltip("Seconds to transition in or out.")]
    public float transitionDuration = 0.3f;

    [Tooltip("Disable the Collider2D while the object is inactive so players can't stand on an invisible platform.")]
    public bool disableColliderWhenInactive = true;

    // ════════════════════════════════════════════════════════
    // RUNTIME STATE
    // ════════════════════════════════════════════════════════

    public bool IsActive { get; private set; }

    private Vector3          _originalScale;
    private SpriteRenderer   _sprite;
    private Collider2D        _collider;
    private Coroutine         _tween;

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        _originalScale = transform.localScale;
        _sprite        = GetComponent<SpriteRenderer>();
        _collider      = GetComponent<Collider2D>();
    }

    private void Start()
    {
        // Apply initial state without a transition.
        ApplyImmediate(startActive);
    }

    // ════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════

    /// <summary>Make the object appear.</summary>
    public void Activate()   => SetActivated(true);

    /// <summary>Make the object disappear.</summary>
    public void Deactivate() => SetActivated(false);

    /// <summary>Toggle between active and inactive states.</summary>
    public void Toggle()     => SetActivated(!IsActive);

    /// <summary>
    /// Directly set the active state. Signature matches ActivationButton.OnStateChanged
    /// so it can be subscribed directly:
    ///   button.OnStateChanged += myActivatable.SetActivated;
    /// </summary>
    public void SetActivated(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;

        if (_tween != null)
            StopCoroutine(_tween);

        switch (appearStyle)
        {
            case AppearStyle.Instant:
                ApplyImmediate(active);
                break;

            case AppearStyle.Scale:
                _tween = StartCoroutine(TweenScale(active));
                break;

            case AppearStyle.Fade:
                _tween = StartCoroutine(TweenFade(active));
                break;
        }
    }

    // ════════════════════════════════════════════════════════
    // INSTANT APPLY
    // ════════════════════════════════════════════════════════

    private void ApplyImmediate(bool active)
    {
        IsActive = active;

        // Renderer
        if (_sprite != null)
        {
            var c     = _sprite.color;
            c.a       = active ? 1f : 0f;
            _sprite.color = c;
        }

        // Scale
        transform.localScale = active ? _originalScale : Vector3.zero;

        // Collider
        UpdateCollider(active);
    }

    // ════════════════════════════════════════════════════════
    // TWEEN: SCALE
    // ════════════════════════════════════════════════════════

    private IEnumerator TweenScale(bool appearing)
    {
        // Always enable the collider before appearing so the object is solid
        // as soon as it starts to solidify; disable it instantly on disappear.
        if (appearing)
        {
            UpdateCollider(true);
            if (_sprite != null)
            {
                var c = _sprite.color; c.a = 1f;
                _sprite.color = c;
            }
        }
        else
        {
            UpdateCollider(false);
        }

        Vector3 startScale = transform.localScale;
        Vector3 endScale   = appearing ? _originalScale : Vector3.zero;
        float   elapsed    = 0f;

        while (elapsed < transitionDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / transitionDuration);
            transform.localScale = Vector3.LerpUnclamped(startScale, endScale, t);
            yield return null;
        }

        transform.localScale = endScale;
        _tween = null;
    }

    // ════════════════════════════════════════════════════════
    // TWEEN: FADE
    // ════════════════════════════════════════════════════════

    private IEnumerator TweenFade(bool appearing)
    {
        if (_sprite == null)
        {
            ApplyImmediate(appearing);
            yield break;
        }

        transform.localScale = _originalScale;

        if (appearing)
            UpdateCollider(true);

        float startAlpha = _sprite.color.a;
        float endAlpha   = appearing ? 1f : 0f;
        float elapsed    = 0f;

        while (elapsed < transitionDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / transitionDuration);

            var c = _sprite.color;
            c.a = Mathf.Lerp(startAlpha, endAlpha, t);
            _sprite.color = c;

            yield return null;
        }

        var final = _sprite.color;
        final.a = endAlpha;
        _sprite.color = final;

        if (!appearing)
            UpdateCollider(false);

        _tween = null;
    }

    // ════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════

    private void UpdateCollider(bool active)
    {
        if (_collider == null || !disableColliderWhenInactive) return;
        _collider.enabled = active;
    }

    // ════════════════════════════════════════════════════════
    // GIZMOS
    // ════════════════════════════════════════════════════════

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = IsActive ? new Color(0f, 1f, 0f, 0.4f)
                                : new Color(1f, 0f, 0f, 0.4f);

        var col = GetComponent<Collider2D>();
        if (col is BoxCollider2D box)
            Gizmos.DrawCube(transform.position + (Vector3)box.offset, box.size);
        else
            Gizmos.DrawSphere(transform.position, 0.3f);
    }
}