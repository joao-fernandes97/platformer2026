using UnityEngine;
using UnityEngine.Events;
using System;
using System.Collections.Generic;
using Unity.Netcode;

/// <summary>
/// Interactable button. A player enters the trigger zone and presses the
/// Interact action.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class ActivationButton : NetworkBehaviour
{
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

    public bool startActivated = false;

    [Header("Prompt")]
    public GameObject interactPrompt;

    [Header("Events")]
    public UnityEvent OnActivated;
    public UnityEvent OnDeactivated;

    // ── C# event for code-side listeners ────────────────────────────────────
    /// <summary>Fired whenever the button state changes. true = activated.</summary>
    public event Action<bool> OnStateChanged;

    
    // NETWORK STATE
    private readonly NetworkVariable<bool> _isActivated = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private int _serverPlayerCount;
  
    // LOCAL STATE
    // Counts how many colliders belonging to each handler are currently
    // inside the zone. A handler is "present" while its count > 0.
    private readonly Dictionary<PlayerInputHandler, int> _localCollidersInZone = new();

    public bool IsActivated => _isActivated.Value;

#region Lifecycle
    private void Awake()
    {
        // Ensure the collider is a trigger so it doesn't block physics.
        GetComponent<Collider2D>().isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        _isActivated.OnValueChanged += OnActivatedValueChanged;

        if(IsServer)
        {
            _isActivated.Value = startActivated;
        }

        SetPromptVisible(false);
    }

    public override void OnNetworkDespawn()
    {
        _isActivated.OnValueChanged -= OnActivatedValueChanged;
    }

    private void Start()
    {
        SetPromptVisible(false);
    }

    private void Update()
    {
        if (activationMode != ActivationMode.PressToActivate) return;
        if (_localCollidersInZone.Count == 0) return;

        // Either player's Interact press triggers the button.
        foreach (var (input, _) in _localCollidersInZone)
        {
            if (input.InteractPressed)
            {
                //TryActivate();
                InteractServerRpc();
                break;   // one activation per frame regardless of player count
            }
        }
    }
#endregion
   
    //Network Callback
    private void OnActivatedValueChanged(bool previous, bool current)
    {
        Fire(current);
    }

#region Trigger zone

    private void OnTriggerEnter2D(Collider2D other)
    {       
        var input = other.GetComponentInParent<PlayerInputHandler>();
        if (input == null) return;

                // Increment this handler's collider count.
        _localCollidersInZone.TryGetValue(input, out int count);
        _localCollidersInZone[input] = count + 1;
 
        // Only react on the first collider that enters (i.e. when the
        // player wasn't in the zone at all before this).
        if (count == 0)
            OnLocalPlayerFirstEntered(input);
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        var input = other.GetComponentInParent<PlayerInputHandler>();
        if (input == null) return;

        if (!_localCollidersInZone.ContainsKey(input)) return;

        int remaining = _localCollidersInZone[input] - 1;

        if (remaining <= 0)
        {
            // Last collider left — player has truly exited.
            _localCollidersInZone.Remove(input);
            OnLocalPlayerFullyExited(input);
        }
        else
        {
            _localCollidersInZone[input] = remaining;
        }
    }

    private void OnLocalPlayerFirstEntered(PlayerInputHandler input)
    {
        // Show the prompt locally regardless of mode.
        if (activationMode == ActivationMode.PressToActivate)
            SetPromptVisible(true);

        // Notify the server so it can update its authoritative player count.
        PlayerZoneChangedServerRpc(true);
    }
 
    // Called when the last collider of a local player exits the zone.
    private void OnLocalPlayerFullyExited(PlayerInputHandler input)
    {
        if (activationMode == ActivationMode.PressToActivate)
        {
            if (_localCollidersInZone.Count == 0)
                SetPromptVisible(false);
        }

        PlayerZoneChangedServerRpc(false);
    }
#endregion
    
#region  Server RPCS 
    /// <summary>
    /// Sent by a client whenever a local player fully enters or exits the zone.
    /// The server is the sole authority on _serverPlayerCount so only it
    /// decides when to fire AutoOnEnter / PressurePlate state changes.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void PlayerZoneChangedServerRpc(bool entered)
    {
        if (entered)
        {
            _serverPlayerCount++;
            if (_serverPlayerCount == 1)
                ServerOnFirstPlayerEntered();
        }
        else
        {
            _serverPlayerCount = Mathf.Max(0, _serverPlayerCount - 1);
            if (_serverPlayerCount == 0)
                ServerOnLastPlayerExited();
        }
    }

    /// <summary>
    /// Sent by the owning client when the player presses Interact inside the zone.
    /// Server validates toggle behaviour and updates the NetworkVariable.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void InteractServerRpc()
    {
        if (activationMode != ActivationMode.PressToActivate) return;

        switch (toggleBehaviour)
        {
            case ToggleBehaviour.Toggle:
                _isActivated.Value = !_isActivated.Value;
                break;

            case ToggleBehaviour.OneShot:
                if (!_isActivated.Value)
                    _isActivated.Value = true;
                break;

            case ToggleBehaviour.Momentary:
                // Momentary has no persisted state — pulse all clients directly.
                FireMomentaryClientRpc();
                break;
        }
    }
#endregion

#region Server only Logic

    private void ServerOnFirstPlayerEntered()
    {
        switch (activationMode)
        {
            case ActivationMode.AutoOnEnter:
                ServerTryActivate();
                break;

            case ActivationMode.PressurePlate:
                _isActivated.Value = true;
                break;
        }
    }

    private void ServerOnLastPlayerExited()
    {
        if (activationMode == ActivationMode.PressurePlate)
            _isActivated.Value = false;
    }

    private void ServerTryActivate()
    {
        // Called by AutoOnEnter on the server.
        switch (toggleBehaviour)
        {
            case ToggleBehaviour.Toggle:
                _isActivated.Value = !_isActivated.Value;
                break;

            case ToggleBehaviour.OneShot:
                if (!_isActivated.Value)
                    _isActivated.Value = true;
                break;

            case ToggleBehaviour.Momentary:
                FireMomentaryClientRpc();
                break;
        }
    }
#endregion

#region Client RPCS

    /// <summary>
    /// Momentary mode has no persistent on/off state, so the NetworkVariable
    /// alone can't drive it. This Rpc pulses all clients directly.
    /// </summary>
    [ClientRpc]
    private void FireMomentaryClientRpc()
    {
        Fire(true);
    }
#endregion

    /// <summary>
    /// Runs on every client, driven by NetworkVariable or Rpc
    /// </summary>
    /// <param name="active"></param>
    private void Fire(bool active)
    {
        OnStateChanged?.Invoke(active);

        if (active) OnActivated?.Invoke();
        else        OnDeactivated?.Invoke();
    }

#region Helpers

    private void SetPromptVisible(bool visible)
    {
        if (interactPrompt != null)
            interactPrompt.SetActive(visible);
    }

    /// <summary>
    /// Force the button into a specific state from external server-side code
    /// (e.g. a puzzle reset). Must be called on the server.
    /// </summary>
    public void ForceState(bool active)
    {
        if (!IsServer) return;
        _isActivated.Value = active;
    }

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
#endregion
}