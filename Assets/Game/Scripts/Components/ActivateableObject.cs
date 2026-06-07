using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// An object that can be activated or deactivated,
/// optionally with a smooth scale/fade transition.
/// </summary>
public class ActivatableObject : NetworkBehaviour
{
    public enum AppearStyle { Instant, Scale, Fade }

    [Header("State")]
    public bool startActive = false;

    [Header("Appearance")]
    public AppearStyle appearStyle  = AppearStyle.Scale;
    public float transitionDuration = 0.3f;
    public bool disableColliderWhenInactive = true;

    // Server writes; all clients read. OnValueChanged drives visuals on
    // every client (including the server/host) whenever the value changes.
    private readonly NetworkVariable<bool> _isActive = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);


    private Vector3        _originalScale;
    private SpriteRenderer _sprite;
    private Collider2D     _collider;
    private Coroutine      _tween;

    public bool IsActive => _isActive.Value;


#region Lifecycle

    private void Awake()
    {
        _originalScale = transform.localScale;
        _sprite        = GetComponent<SpriteRenderer>();
        _collider      = GetComponent<Collider2D>();
    }

    public override void OnNetworkSpawn()
    {
        // Subscribe on all clients so any server write propagates visuals locally.
        _isActive.OnValueChanged += OnActiveValueChanged;

        if (IsServer)
        {
            // Authoritative initial value — triggers OnValueChanged on all clients.
            _isActive.Value = startActive;
        }
        ApplyImmediate(_isActive.Value);
    }

    public override void OnNetworkDespawn()
    {
        _isActive.OnValueChanged -= OnActiveValueChanged;
    }
#endregion

#region  Network Callbacks

    private void OnActiveValueChanged(bool previous, bool current)
    {
        // Server: update the physics collider immediately so authority is correct.
        if (IsServer)
            UpdateCollider(current);

        // All clients: run the visual transition.
        ApplyVisual(current);
    }
#endregion
    
#region  Public API

    /// <summary>Make the object appear. Call on server only.</summary>
    public void Activate()   => SetActivated(true);

    /// <summary>Make the object disappear. Call on server only.</summary>
    public void Deactivate() => SetActivated(false);

    /// <summary>Toggle between states. Call on server only.</summary>
    public void Toggle()     => SetActivated(!_isActive.Value);

    public void SetActivated(bool active)
    {
        if (!IsServer) return;
        if (_isActive.Value == active) return;
        _isActive.Value = active;   // drives OnActiveValueChanged on all clients
    }
#endregion

#region Visuals
    private void ApplyVisual(bool active)
    {
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

    private void ApplyImmediate(bool active)
    {
        if (_sprite != null)
        {
            var c = _sprite.color;
            c.a   = active ? 1f : 0f;
            _sprite.color = c;
        }

        transform.localScale = active ? _originalScale : Vector3.zero;
        UpdateCollider(active);
    }

    private IEnumerator TweenScale(bool appearing)
    {
        // Enable collider before appearing so it's solid as it grows;
        // disable immediately on disappear.
        UpdateCollider(appearing);

        if (_sprite != null)
        {
            var c = _sprite.color;
            c.a   = appearing ? 1f : _sprite.color.a;
            _sprite.color = c;
        }

        Vector3 startScale = transform.localScale;
        Vector3 endScale   = appearing ? _originalScale : Vector3.zero;
        float   elapsed    = 0f;

        while (elapsed < transitionDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.SmoothStep(0f, 1f, elapsed / transitionDuration);
            transform.localScale = Vector3.LerpUnclamped(startScale, endScale, t);
            yield return null;
        }

        transform.localScale = endScale;
        _tween = null;
    }

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
            float t  = Mathf.SmoothStep(0f, 1f, elapsed / transitionDuration);

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
#endregion
    
#region  Helpers

    private void UpdateCollider(bool active)
    {
        if (_collider == null || !disableColliderWhenInactive) return;
        _collider.enabled = active;
    }

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
#endregion
}