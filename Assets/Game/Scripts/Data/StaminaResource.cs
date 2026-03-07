using UnityEngine;

/// <summary>
/// Pure stamina resource. No MonoBehaviour — owned and ticked by PlayerController.
/// Keeping it as a plain class makes it trivially serialisable for network sync later:
/// NGO: just put currentStamina in a NetworkVariable<float>.
/// </summary>
[System.Serializable]
public class StaminaResource
{
    [Header("Config")]
    [Tooltip("Maximum stamina points.")]
    public float maxStamina = 100f;

    [Tooltip("Stamina drained per second while sprinting.")]
    public float drainRate = 20f;

    [Tooltip("Stamina recovered per second when not sprinting.")]
    public float regenRate = 15f;

    [Tooltip("Seconds after sprint stops before regen begins.")]
    public float regenDelay = 0.5f;

    [Tooltip("Minimum stamina required to START a sprint (prevents flicker at 0).")]
    public float sprintMinThreshold = 10f;

    // ── Runtime State ────────────────────────────────────────────────────────
    public float Current        { get; private set; }
    public float Normalised     => Current / maxStamina;
    public bool  IsExhausted    { get; private set; }

    private float _regenDelayTimer;

    // ── Init ─────────────────────────────────────────────────────────────────
    public void Initialise() => Current = maxStamina;

    // ── Tick (call every frame from PlayerController) ────────────────────────
    /// <param name="wantsSprint">True when player holds sprint AND is moving.</param>
    /// <returns>True if sprint is active this frame.</returns>
    public bool Tick(bool wantsSprint, float deltaTime)
    {
        bool sprinting = false;

        if (wantsSprint && !IsExhausted && Current > 0f)
        {
            Current -= drainRate * deltaTime;
            Current  = Mathf.Max(Current, 0f);
            _regenDelayTimer = regenDelay;
            sprinting = true;

            if (Current <= 0f)
                IsExhausted = true;
        }
        else
        {
            // Cool-down before regen kicks in
            if (_regenDelayTimer > 0f)
            {
                _regenDelayTimer -= deltaTime;
            }
            else
            {
                Current += regenRate * deltaTime;
                Current  = Mathf.Min(Current, maxStamina);
            }

            // Clear exhaustion once enough stamina has returned
            if (IsExhausted && Current >= sprintMinThreshold)
                IsExhausted = false;
        }

        return sprinting;
    }
}
