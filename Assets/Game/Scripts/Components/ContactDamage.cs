using UnityEngine;

/// <summary>
/// Deals damage to any GameObject with a <see cref="HealthComponent"/> that
/// touches this collider.
///
/// Drop onto spikes, lava, saw blades, or any other hazard — no movement
/// or AI logic included.
///
/// Works with both Trigger and non-Trigger colliders:
///   • Trigger  — use when the hazard has no physical presence
///                (e.g. a damage zone that players pass through).
///   • Collider — use when the hazard should also block movement
///                (e.g. spikes that stop the player and deal damage).
///
/// MULTIPLAYER NOTE (NGO server-auth):
///   Wrap OnCollisionStay2D / OnTriggerStay2D with: if (!IsServer) return;
///   Damage is already applied through HealthComponent which handles its own
///   invincibility window, so no extra guards are needed here.
/// </summary>
public class ContactDamage : MonoBehaviour
{
    [Header("Damage")]
    [Tooltip("Damage applied per tick.")]
    public float damage = 10f;

    [Tooltip("Minimum seconds between damage ticks on the same target.")]
    public float damageCooldown = 0.5f;

    [Tooltip("If true, uses OnTrigger callbacks instead of OnCollision callbacks. " +
             "Match this to whether the attached Collider2D has 'Is Trigger' ticked.")]
    public bool isTrigger = false;

    // ── Internal ──────────────────────────────────────────────────────────────

    // One cooldown timer per target so two players touching the same hazard
    // don't share a cooldown.
    private readonly System.Collections.Generic.Dictionary<HealthComponent, float>
        _cooldownTimers = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Update()
    {
        // Drain all active timers. Removing finished entries keeps the dict lean.
        var toRemove = new System.Collections.Generic.List<HealthComponent>();

        foreach (var key in _cooldownTimers.Keys)
        {
            _cooldownTimers[key] -= Time.deltaTime;
            if (_cooldownTimers[key] <= 0f)
                toRemove.Add(key);
        }

        foreach (var key in toRemove)
            _cooldownTimers.Remove(key);
    }

    // ── Collision (non-trigger) ───────────────────────────────────────────────

    private void OnCollisionEnter2D(Collision2D col)
    {
        if (!isTrigger) TryDamage(col.gameObject);
    }

    private void OnCollisionStay2D(Collision2D col)
    {
        if (!isTrigger) TryDamage(col.gameObject);
    }

    // ── Trigger ───────────────────────────────────────────────────────────────

    private void OnTriggerEnter2D(Collider2D col)
    {
        if (isTrigger) TryDamage(col.gameObject);
    }

    private void OnTriggerStay2D(Collider2D col)
    {
        if (isTrigger) TryDamage(col.gameObject);
    }

    // ── Core ──────────────────────────────────────────────────────────────────

    private void TryDamage(GameObject target)
    {
        if (!target.TryGetComponent<HealthComponent>(out var health)) return;
        if (health.IsDead) return;

        if (_cooldownTimers.TryGetValue(health, out float remaining) && remaining > 0f)
            return;

        health.TakeDamage(damage);
        _cooldownTimers[health] = damageCooldown;
    }
}