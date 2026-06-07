using UnityEngine;
using System;
using Unity.Netcode;

/// <summary>
/// Generic health component
/// server-authoritative.
/// </summary>
public class HealthComponent : NetworkBehaviour
{

    [Header("Config")]
    public float maxHealth = 100f;

    [Header("Invincibility")]
    public float invincibilityDuration = 0.5f;

    //Fired on every client whenever health changes. (current, max)
    public event Action<float, float> OnHealthChanged;

    //Fired on every client when health reaches zero.
    public event Action OnDied;

    //Fired on every client when the entity is revived.
    public event Action OnRevived;

    // Server writes; all clients read. OnValueChanged fires locally on every
    // client (including the server/host) whenever the value changes.
    private readonly NetworkVariable<float> _currentHealth = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // IsDead is derived from _currentHealth, but we cache it so we can detect
    // the dead→alive transition for the OnRevived event.
    private bool _isDead;

    //Server-only runtime state
    private float _invincibilityTimer;

    
    // PUBLIC READ-ONLY STATE  (reads the NetworkVariable — safe on all clients)
    public float Current    => _currentHealth.Value;
    public float Normalized => _currentHealth.Value / maxHealth;
    public bool  IsDead     => _isDead;

#region Lifecycle
    
    public override void OnNetworkSpawn()
    {
        // Subscribe to the NetworkVariable on all clients so any change
        // (written by the server) automatically propagates events locally.
        _currentHealth.OnValueChanged += OnHealthValueChanged;

        if (IsServer)
        {
            // Authoritative initial value. Triggers OnValueChanged on all clients.
            _currentHealth.Value = maxHealth;
        }
    }

    public override void OnNetworkDespawn()
    {
        _currentHealth.OnValueChanged -= OnHealthValueChanged;
    }

    private void Update()
    {
        // Invincibility timer is server-only; no point ticking it on clients.
        if (!IsServer) return;
        if (_invincibilityTimer > 0f)
            _invincibilityTimer -= Time.deltaTime;
    }
#endregion

    // NETWORK VARIABLE CALLBACK  (runs on every client)
    private void OnHealthValueChanged(float previous, float current)
    {
        OnHealthChanged?.Invoke(current, maxHealth);

        bool nowDead = current <= 0f;

        if (nowDead && !_isDead)
        {
            // Health just reached zero — broadcast death locally.
            // (The ClientRpc path below handles the multi-client broadcast;
            // this callback covers the server/host's own local copy as well.)
            _isDead = true;
            OnDied?.Invoke();
        }
        else if (!nowDead && _isDead)
        {
            // Health went above zero after being dead — revive.
            _isDead = false;
            OnRevived?.Invoke();
        }
    }

#region Public API
    /// <summary>
    /// Apply damage.
    /// Call this directly only from server-authoritative code
    /// Client-side callers should use TakeDamageServerRpc instead.
    /// </summary>
    public void TakeDamage(float amount)
    {
        if (!IsServer) return;
        ApplyDamage(amount);
    }

    /// <summary>
    /// Client > Server Rpc. Any client that wants to deal damage sends this.
    /// The server validates and applies; the NetworkVariable propagates the result.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeDamageServerRpc(float amount)
    {
        ApplyDamage(amount);
    }

    /// <summary>
    /// Restore health. Call only on the server.
    /// The NetworkVariable change propagates OnHealthChanged to all clients.
    /// </summary>
    public void Heal(float amount)
    {
        if (!IsServer || _isDead) return;
        _currentHealth.Value = Mathf.Min(_currentHealth.Value + amount, maxHealth);
    }

    /// <summary>
    /// Restore full health and clear the dead flag.
    /// Called by CheckpointManager.RespawnAll() always on the server.
    /// </summary>
    public void Revive()
    {
        if (!IsServer) return;

        _invincibilityTimer  = invincibilityDuration;   // brief grace period on spawn
        _currentHealth.Value = maxHealth;               // drives OnHealthValueChanged > OnRevived on all clients

        // Fire the Rpc so clients that re-enable this
        // NetworkObject in the same frame definitely receive the event.
        ReviveClientRpc();
    }
#endregion
    
#region Client RPCS
    /// <summary>
    /// Ensures OnRevived fires on all clients even if they receive the
    /// NetworkVariable update and the Rpc in the same frame (ordering edge case).
    /// OnHealthValueChanged already fires OnRevived when the value changes,
    /// so this Rpc is a safety net
    /// </summary>
    [ClientRpc]
    private void ReviveClientRpc()
    {
        // If OnHealthValueChanged already handled this (value arrived first),
        // _isDead will already be false and this is a safe exit.
        if (!_isDead) return;

        _isDead = false;
        OnRevived?.Invoke();
    }
#endregion

    // Internal, Server Only
    private void ApplyDamage(float amount)
    {
        // Called only on server — guards are in the callers above.
        if (_isDead || _invincibilityTimer > 0f) return;

        _currentHealth.Value = Mathf.Max(_currentHealth.Value - amount, 0f);
        _invincibilityTimer  = invincibilityDuration;

        // Death is detected in OnHealthValueChanged when the NetworkVariable
        // change propagates; no explicit Die() call needed here.
    }
}