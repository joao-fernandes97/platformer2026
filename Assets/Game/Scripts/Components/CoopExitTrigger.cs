using UnityEngine;
using UnityEngine.Events;
using System.Collections.Generic;

/// <summary>
/// Exit zone for a local co-op platformer.
///
/// ═══════════════════════════════════════════════════════════
///  BEHAVIOUR
/// ═══════════════════════════════════════════════════════════
///  Place on any GameObject with a Collider2D (set to Is Trigger).
///
///  RequiredPlayersMode controls when the exit fires:
///
///   • Any        — the first player to enter triggers it immediately.
///   • All        — every living player must be inside simultaneously.
///   • Specific N — exactly N living players must be inside simultaneously.
///
///  While players are gathering but the threshold hasn't been met,
///  an optional "waiting" prompt or indicator can be shown.
///
/// ═══════════════════════════════════════════════════════════
///  SETUP
/// ═══════════════════════════════════════════════════════════
///  1. Add to a GameObject whose Collider2D covers the exit area.
///  2. Choose RequiredPlayersMode in the Inspector.
///  3. Wire OnExitTriggered to your scene-transition action
///     (e.g. an OkapiKit action, SceneManager.LoadScene wrapper, etc.)
///  4. Optionally wire OnWaiting / OnWaitingCancelled to a UI prompt
///     that tells the other player to catch up.
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION (NGO server-auth)
/// ═══════════════════════════════════════════════════════════
///  1. Inherit NetworkBehaviour.
///  2. Track players via NetworkObject IDs instead of Transform refs.
///  3. Run the threshold check only on the server; broadcast the
///     trigger via a ClientRpc so all clients play the transition.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class CoopExitTrigger : MonoBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    public enum RequiredPlayersMode
    {
        /// <summary>Any single player entering fires the exit.</summary>
        Any,
        /// <summary>All currently living players must be inside simultaneously.</summary>
        All,
        /// <summary>A fixed number of players must be inside simultaneously.</summary>
        SpecificCount,
    }

    [Header("Trigger Condition")]
    public RequiredPlayersMode requiredPlayers = RequiredPlayersMode.All;

    [Tooltip("Only used when mode is SpecificCount.")]
    [Min(1)]
    public int requiredCount = 2;

    [Tooltip("If true the exit can only fire once. Disable for puzzle-reset scenarios.")]
    public bool oneShot = true;

    [Header("Optional Visuals")]
    [Tooltip("Shown while at least one (but not enough) player(s) are inside.")]
    public GameObject waitingIndicator;

    [Header("Events")]
    [Tooltip("Fired when the required number of players are all inside the zone.")]
    public UnityEvent OnExitTriggered;

    [Tooltip("Fired when the first player enters but the threshold isn't met yet. " +
             "Use to show a 'waiting for other player' prompt.")]
    public UnityEvent OnWaiting;

    [Tooltip("Fired when the zone empties after being in a waiting state " +
             "(i.e. the player left without triggering). " +
             "Use to hide the waiting prompt.")]
    public UnityEvent OnWaitingCancelled;

    // ════════════════════════════════════════════════════════
    // RUNTIME STATE
    // ════════════════════════════════════════════════════════

    /// <summary>True after the exit has fired (when oneShot = true).</summary>
    public bool HasFired { get; private set; }

    // One entry per PlayerController; value = number of that player's
    // colliders currently inside the zone (handles composite colliders).
    private readonly Dictionary<PlayerController, int> _playersInZone = new();

    private bool _wasWaiting;

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        GetComponent<Collider2D>().isTrigger = true;
        SetWaitingIndicator(false);
    }

    // ════════════════════════════════════════════════════════
    // TRIGGER ZONE
    // ════════════════════════════════════════════════════════

    private void OnTriggerEnter2D(Collider2D other)
    {
        var player = other.GetComponentInParent<PlayerController>();
        if (player == null) return;

        _playersInZone.TryGetValue(player, out int count);
        _playersInZone[player] = count + 1;

        if (count == 0)
            OnPlayerEntered();
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        var player = other.GetComponentInParent<PlayerController>();
        if (player == null || !_playersInZone.ContainsKey(player)) return;

        int remaining = _playersInZone[player] - 1;

        if (remaining <= 0)
        {
            _playersInZone.Remove(player);
            OnPlayerExited();
        }
        else
        {
            _playersInZone[player] = remaining;
        }
    }

    // ════════════════════════════════════════════════════════
    // LOGIC
    // ════════════════════════════════════════════════════════

    private void OnPlayerEntered()
    {
        if (HasFired) return;

        if (ThresholdMet())
        {
            Debug.Log("Threshold met");
            Fire();
        }
        else
        {
            // At least one player is here but we're still waiting for more.
            if (!_wasWaiting)
            {
                _wasWaiting = true;
                SetWaitingIndicator(true);
                OnWaiting?.Invoke();
            }
        }
    }

    private void OnPlayerExited()
    {
        if (HasFired) return;

        // Re-check: maybe enough players are still inside (e.g. one of three left).
        if (ThresholdMet())
        {
            Fire();
            return;
        }

        if (_playersInZone.Count == 0 && _wasWaiting)
        {
            _wasWaiting = false;
            SetWaitingIndicator(false);
            OnWaitingCancelled?.Invoke();
        }
    }

    private bool ThresholdMet()
    {
        int inside = _playersInZone.Count;
        if (inside == 0) return false;

        switch (requiredPlayers)
        {
            case RequiredPlayersMode.Any:
                return inside >= 1;

            case RequiredPlayersMode.All:
                int living = PlayerRegistry.Instance?.GetAllLiving().Count ?? inside;
                // Guard: if registry is unavailable, fall back to matching whoever is present.
                return inside >= Mathf.Max(1, living);

            case RequiredPlayersMode.SpecificCount:
                Debug.Log($"inside = {inside}, requiredCount = {requiredCount}");
                return inside >= requiredCount;

            default:
                return false;
        }
    }

    private void Fire()
    {
        if (oneShot)
            HasFired = true;

        _wasWaiting = false;
        SetWaitingIndicator(false);

        OnExitTriggered?.Invoke();
    }

    // ════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════

    private void SetWaitingIndicator(bool visible)
    {
        if (waitingIndicator != null)
            waitingIndicator.SetActive(visible);
    }

    /// <summary>
    /// Resets the fired flag so the exit can trigger again.
    /// Useful for puzzle-reset flows even when oneShot is true.
    /// </summary>
    public void Reset()
    {
        HasFired    = false;
        _wasWaiting = false;
        _playersInZone.Clear();
        SetWaitingIndicator(false);
    }

    // ════════════════════════════════════════════════════════
    // GIZMOS
    // ════════════════════════════════════════════════════════

    private void OnDrawGizmos()
    {
        if (HasFired)
            Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        else if (_playersInZone.Count > 0)
            Gizmos.color = new Color(1f, 1f, 0f, 0.25f);
        else
            Gizmos.color = new Color(0f, 0.6f, 1f, 0.2f);

        var col = GetComponent<Collider2D>();
        if (col is BoxCollider2D box)
            Gizmos.DrawCube(transform.position + (Vector3)box.offset, box.size);
        else if (col is CircleCollider2D circle)
            Gizmos.DrawSphere(transform.position + (Vector3)circle.offset, circle.radius);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = HasFired ? Color.green : Color.cyan;

        var col = GetComponent<Collider2D>();
        if (col is BoxCollider2D box)
            Gizmos.DrawWireCube(transform.position + (Vector3)box.offset, box.size);
        else if (col is CircleCollider2D circle)
            Gizmos.DrawWireSphere(transform.position + (Vector3)circle.offset, circle.radius);

#if UNITY_EDITOR
        // Label showing current state in the scene view.
        string label = requiredPlayers switch
        {
            RequiredPlayersMode.Any           => "Exit: Any player",
            RequiredPlayersMode.All           => "Exit: All players",
            RequiredPlayersMode.SpecificCount => $"Exit: {requiredCount} players",
            _                                 => "Exit"
        };

        if (HasFired)        label += " [FIRED]";
        else if (_playersInZone.Count > 0) label += $" [{_playersInZone.Count} inside]";

        UnityEditor.Handles.Label(transform.position + Vector3.up * 0.5f, label);
#endif
    }
}