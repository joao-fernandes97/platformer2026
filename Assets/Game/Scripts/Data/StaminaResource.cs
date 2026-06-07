using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Pure stamina resource. No MonoBehaviour, owned and ticked by PlayerController.
/// </summary>
[System.Serializable]
public class StaminaResource
{
    [Header("Config")]
    public float maxStamina = 100f;
    public float drainRate = 20f;
    public float regenRate = 15f;
    public float regenDelay = 0.5f;
    //min stam needed to start sprinting
    public float sprintMinThreshold = 10f;

    //Runtime State
    public float Current        { get; private set; }
    public float Normalized     => Current / maxStamina;
    public bool  IsExhausted    { get; private set; }

    private float _regenDelayTimer;

    public void Initialize() => Current = maxStamina;

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
