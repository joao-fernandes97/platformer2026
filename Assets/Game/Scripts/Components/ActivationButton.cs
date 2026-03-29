using UnityEngine;
using UnityEngine.Events;
using System;
using System.Collections.Generic;

/// <summary>
/// Interactable button. A player enters the trigger zone and presses the
/// Interact action (bound per-player in PlayerInputActions — works for both
/// the WASD and Arrows control schemes automatically).
///
/// ═══════════════════════════════════════════════════════════
///  WIRING UP
/// ═══════════════════════════════════════════════════════════
///  Option A — Inspector (UnityEvent):
///    Drag the target ActivatableObject into OnActivated / OnDeactivated
///    in the Inspector and bind Activate() / Deactivate().
///
///  Option B — Code (C# event):
///    button.OnStateChanged += (active) => myObject.SetActivated(active);
///
///  Both work simultaneously — mix freely.
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION (NGO server-auth)
/// ═══════════════════════════════════════════════════════════
///  1. Inherit NetworkBehaviour.
///  2. On interact, send a ServerRpc from the local client.
///  3. Server validates and calls SetState(); broadcasts via ClientRpc.
///  4. _inputsInZone tracking stays local — each client knows which
///     players are inside its trigger (NetworkObject colliders replicate).
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class ActivationButton : MonoBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    public enum ActivationMode
    {
        /// <summary>A player must press Interact while inside the zone.</summary>
        PressToActivate,
        /// <summary>Activates automatically when any player enters the zone.</summary>
        AutoOnEnter,
        /// <summary>Active while at least one player is inside; deactivates on exit.</summary>
        PressurePlate,
    }

    public enum ToggleBehaviour
    {
        /// <summary>Each activation flips between on and off.</summary>
        Toggle,
        /// <summary>Only fires OnActivated once; cannot be turned off.</summary>
        OneShot,
        /// <summary>Each interact fires OnActivated regardless of current state.</summary>
        Momentary,
    }

    [Header("Behaviour")]
    public ActivationMode  activationMode  = ActivationMode.PressToActivate;
    public ToggleBehaviour toggleBehaviour = ToggleBehaviour.Toggle;

    [Tooltip("Start in the activated state?")]
    public bool startActivated = false;

    [Header("Prompt")]
    [Tooltip("Optional world-space UI shown when a player is in range. Leave null to skip.")]
    public GameObject interactPrompt;

    [Header("Events — Inspector")]
    public UnityEvent OnActivated;
    public UnityEvent OnDeactivated;

    // ── C# event for code-side listeners ────────────────────────────────────
    /// <summary>Fired whenever the button state changes. true = activated.</summary>
    public event Action<bool> OnStateChanged;

    // ════════════════════════════════════════════════════════
    // RUNTIME STATE
    // ════════════════════════════════════════════════════════

    public bool IsActivated { get; private set; }

    // Counts how many colliders belonging to each handler are currently
    // inside the zone. A handler is "present" while its count > 0.
    private readonly Dictionary<PlayerInputHandler, int> _collidersInZone = new();

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        // Ensure the collider is a trigger so it doesn't block physics.
        GetComponent<Collider2D>().isTrigger = true;

        IsActivated = startActivated;
    }

    private void Start()
    {
        SetPromptVisible(false);
    }

    private void Update()
    {
        if (activationMode != ActivationMode.PressToActivate) return;
        if (_collidersInZone.Count == 0) return;

        // Either player's Interact press triggers the button.
        foreach (var (input, _) in _collidersInZone)
        {
            if (input.InteractPressed)
            {
                TryActivate();
                break;   // one activation per frame regardless of player count
            }
        }
    }

    // ════════════════════════════════════════════════════════
    // TRIGGER ZONE
    // ════════════════════════════════════════════════════════

    private void OnTriggerEnter2D(Collider2D other)
    {       
        var input = other.GetComponentInParent<PlayerInputHandler>();
        if (input == null) return;

                // Increment this handler's collider count.
        _collidersInZone.TryGetValue(input, out int count);
        _collidersInZone[input] = count + 1;
 
        // Only react on the first collider that enters (i.e. when the
        // player wasn't in the zone at all before this).
        if (count == 0)
            OnPlayerEnteredZone(input);
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        var input = other.GetComponentInParent<PlayerInputHandler>();
        if (input == null) return;

        if (!_collidersInZone.ContainsKey(input)) return;

        int remaining = _collidersInZone[input] - 1;

                if (remaining <= 0)
        {
            // Last collider left — player has truly exited.
            _collidersInZone.Remove(input);
            OnPlayerExitedZone();
        }
        else
        {
            _collidersInZone[input] = remaining;
        }
    }

        private void OnPlayerEnteredZone(PlayerInputHandler input)
    {
        switch (activationMode)
        {
            case ActivationMode.AutoOnEnter:
                TryActivate();
                break;
 
            case ActivationMode.PressurePlate:
                if (_collidersInZone.Count == 1)   // first player
                    SetState(true);
                break;
 
            case ActivationMode.PressToActivate:
                SetPromptVisible(true);
                break;
        }
    }
 
    private void OnPlayerExitedZone()
    {
        switch (activationMode)
        {
            case ActivationMode.PressurePlate:
                if (_collidersInZone.Count == 0)
                    SetState(false);
                break;
 
            case ActivationMode.PressToActivate:
                if (_collidersInZone.Count == 0)
                    SetPromptVisible(false);
                break;
        }
    }

    // ════════════════════════════════════════════════════════
    // ACTIVATION LOGIC
    // ════════════════════════════════════════════════════════

    private void TryActivate()
    {
        switch (toggleBehaviour)
        {
            case ToggleBehaviour.Toggle:
                SetState(!IsActivated);
                break;

            case ToggleBehaviour.OneShot:
                if (!IsActivated)
                    SetState(true);
                break;

            case ToggleBehaviour.Momentary:
                // Always fires, regardless of current state.
                Fire(true);
                break;
        }
    }

    private void SetState(bool active)
    {
        if (IsActivated == active) return;
        IsActivated = active;
        Fire(active);
    }

    private void Fire(bool active)
    {
        OnStateChanged?.Invoke(active);

        if (active) OnActivated?.Invoke();
        else        OnDeactivated?.Invoke();
    }

    // ════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════

    private void SetPromptVisible(bool visible)
    {
        if (interactPrompt != null)
            interactPrompt.SetActive(visible);
    }

    /// <summary>
    /// Force the button into a specific state from external code
    /// (e.g. a puzzle reset).
    /// </summary>
    public void ForceState(bool active) => SetState(active);

    // ════════════════════════════════════════════════════════
    // GIZMOS
    // ════════════════════════════════════════════════════════

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = IsActivated ? Color.green : Color.yellow;

        var col = GetComponent<Collider2D>();
        if (col is BoxCollider2D box)
        {
            Gizmos.DrawWireCube(
                transform.position + (Vector3)box.offset,
                box.size);
        }
        else if (col is CircleCollider2D circle)
        {
            Gizmos.DrawWireSphere(
                transform.position + (Vector3)circle.offset,
                circle.radius);
        }
    }
}