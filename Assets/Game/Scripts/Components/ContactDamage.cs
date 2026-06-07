using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Deals damage to any GameObject with a "HealthComponent" that
/// touches this collider.
/// </summary>
public class ContactDamage : NetworkBehaviour
{
    [Header("Damage")]
    public float damage = 10f;
    public float damageCooldown = 0.5f;
    public bool isTrigger = false;

    // One cooldown timer per target so two players touching the same hazard
    // don't share a cooldown.
    private readonly Dictionary<HealthComponent, float>
        _cooldownTimers = new();


    private void Update()
    {
        if (_cooldownTimers.Count == 0) return;

        var toRemove = new List<HealthComponent>();

        foreach (var (health, remaining) in _cooldownTimers.ToList())
        {
            float newVal = remaining - Time.deltaTime;
            if (newVal <= 0f)
                toRemove.Add(health);
            else
                _cooldownTimers[health] = newVal;
        }

        foreach (var key in toRemove)
            _cooldownTimers.Remove(key);
    }

    private void OnCollisionEnter2D(Collision2D col)
    {
        if (!IsServer) return;
        if (!isTrigger) TryDamage(col.gameObject);
    }

    private void OnCollisionStay2D(Collision2D col)
    {
        if (!IsServer) return;
        if (!isTrigger) TryDamage(col.gameObject);
    }

    private void OnTriggerEnter2D(Collider2D col)
    {
        if (!IsServer) return;
        if (isTrigger) TryDamage(col.gameObject);
    }

    private void OnTriggerStay2D(Collider2D col)
    {
        if (!IsServer) return;
        if (isTrigger) TryDamage(col.gameObject);
    }

    private void TryDamage(GameObject target)
    {
        if (!target.TryGetComponent<HealthComponent>(out var health)) return;
        if (health.IsDead) return;

        if (_cooldownTimers.TryGetValue(health, out float remaining) && remaining > 0f)
            return;

        health.TakeDamageServerRpc(damage);
        _cooldownTimers[health] = damageCooldown;
    }
}