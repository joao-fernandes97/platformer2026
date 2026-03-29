using UnityEngine;
using System;

/// <summary>
/// Generic health component. Attach to any damageable GameObject.
/// Kept separate from PlayerController so enemies, destructibles,
/// and players all share the same contract.
///
/// MULTIPLAYER MIGRATION (NGO server-auth):
///   1. Inherit NetworkBehaviour.
///   2. Replace _current with NetworkVariable<float> (server write, all read).
///   3. Move TakeDamage behind an IsServer guard — clients call a ServerRpc.
///   4. OnDied ServerRpc → ClientRpc to trigger death FX on all clients.
/// </summary>
public class HealthComponent : MonoBehaviour
{
    [Header("Config")]
    public float maxHealth = 100f;

    [Header("Invincibility")]
    [Tooltip("Seconds of invincibility after taking a hit (0 = none).")]
    public float invincibilityDuration = 0.5f;

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<float, float> OnHealthChanged;   // (current, max)
    public event Action               OnDied;
    public event Action               OnRevived;


    // ── Public State ──────────────────────────────────────────────────────────
    public float Current     { get; private set; }
    public float Normalized  => Current / maxHealth;
    public bool  IsDead      { get; private set; }

    // ── Internal ──────────────────────────────────────────────────────────────
    private float _invincibilityTimer;

    private void Awake() => Current = maxHealth;

    private void Update()
    {
        if (_invincibilityTimer > 0f)
            _invincibilityTimer -= Time.deltaTime;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Apply damage. Respects invincibility window.</summary>
    public void TakeDamage(float amount)
    {
        if (IsDead || _invincibilityTimer > 0f) return;

        Current = Mathf.Max(Current - amount, 0f);
        _invincibilityTimer = invincibilityDuration;

        OnHealthChanged?.Invoke(Current, maxHealth);

        if (Current <= 0f)
            Die();
    }

    public void Heal(float amount)
    {
        if (IsDead) return;
        Current = Mathf.Min(Current + amount, maxHealth);
        OnHealthChanged?.Invoke(Current, maxHealth);
    }

    /// <summary>
    /// Restore the entity to full health and clear the dead flag.
    /// Called by CheckpointManager on respawn.
    ///
    /// MULTIPLAYER MIGRATION: call only on the server; broadcast the new
    /// health value to all clients via a ClientRpc or NetworkVariable change.
    /// </summary>
    public void Revive()
    {
        IsDead              = false;
        Current             = maxHealth;
        _invincibilityTimer = invincibilityDuration;   // brief grace period on spawn
        OnHealthChanged?.Invoke(Current, maxHealth);
        OnRevived?.Invoke();
    }

    private void Die()
    {
        IsDead = true;
        OnDied?.Invoke();
    }
}