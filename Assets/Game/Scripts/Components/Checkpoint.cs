using UnityEngine;

/// <summary>
/// A trigger zone that, when any player enters, registers itself with
/// CheckpointManager as the current respawn point.
///
/// ═══════════════════════════════════════════════════════════
///  SETUP
/// ═══════════════════════════════════════════════════════════
///  1. Add to a GameObject with a Collider2D set to "Is Trigger".
///  2. Set orderIndex to enforce one-way progression (optional —
///     leave at 0 to use simple last-touched behaviour).
///  3. Assign optional visual GameObjects for each state.
///  4. Set spawnOffset to nudge the respawn point above the floor.
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION (NGO server-auth)
/// ═══════════════════════════════════════════════════════════
///  1. Inherit NetworkBehaviour.
///  2. On trigger enter, send a ServerRpc to CheckpointManager.
///  3. Replace SetVisualState calls with a NetworkVariable<State>
///     and drive visuals from its OnValueChanged callback so all
///     clients see the correct state.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class Checkpoint : MonoBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    [Header("Ordering")]
    [Tooltip("Optional. CheckpointManager will only activate this checkpoint if its " +
             "orderIndex is >= the current active checkpoint's index, preventing " +
             "players from going backwards to an earlier checkpoint. " +
             "Set all to 0 to disable ordering (last-touched wins).")]
    public int orderIndex = 0;

    [Header("Spawn Point")]
    [Tooltip("World-space offset from this transform's position where players will respawn. " +
             "Use Y to lift the spawn point above the floor collider.")]
    public Vector2 spawnOffset = new Vector2(0f, 1f);

    [Header("Visuals")]
    [Tooltip("Shown when this checkpoint has never been reached.")]
    public GameObject inactiveVisual;

    [Tooltip("Shown when this checkpoint has been reached but is not the most recent.")]
    public GameObject visitedVisual;

    [Tooltip("Shown when this is the active (most recent) respawn point.")]
    public GameObject activeVisual;

    // ════════════════════════════════════════════════════════
    // STATE
    // ════════════════════════════════════════════════════════

    public enum CheckpointState { Inactive, Visited, Active }

    public CheckpointState State { get; private set; } = CheckpointState.Inactive;

    /// <summary>World-space position players will respawn at.</summary>
    public Vector3 SpawnPosition => transform.position + (Vector3)spawnOffset;

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        GetComponent<Collider2D>().isTrigger = true;
        SetVisualState(CheckpointState.Inactive);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        // Only react to player entry.
        if (other.GetComponentInParent<PlayerController>() == null) return;

        CheckpointManager.Instance?.PlayerReachedCheckpoint(this);
    }

    // ════════════════════════════════════════════════════════
    // PUBLIC API  (called by CheckpointManager)
    // ════════════════════════════════════════════════════════

    /// <summary>Update visual state. Called by CheckpointManager when the active checkpoint changes.</summary>
    public void SetVisualState(CheckpointState newState)
    {
        State = newState;

        if (inactiveVisual != null) inactiveVisual.SetActive(newState == CheckpointState.Inactive);
        if (visitedVisual  != null) visitedVisual .SetActive(newState == CheckpointState.Visited);
        if (activeVisual   != null) activeVisual  .SetActive(newState == CheckpointState.Active);
    }

    // ════════════════════════════════════════════════════════
    // GIZMOS
    // ════════════════════════════════════════════════════════

    private void OnDrawGizmosSelected()
    {
        // Spawn point marker
        Vector3 sp = transform.position + (Vector3)spawnOffset;
        Gizmos.color = State == CheckpointState.Active ? Color.green : Color.yellow;
        Gizmos.DrawSphere(sp, 0.2f);
        Gizmos.DrawLine(transform.position, sp);
    }

    private void OnDrawGizmos()
    {
        // Always show a small indicator so checkpoints are easy to spot in the editor.
        Gizmos.color = State == CheckpointState.Active  ? new Color(0f, 1f, 0f, 0.5f)
                      : State == CheckpointState.Visited ? new Color(1f, 1f, 0f, 0.4f)
                                                         : new Color(0.5f, 0.5f, 0.5f, 0.3f);

        var col = GetComponent<Collider2D>();
        if (col is BoxCollider2D box)
            Gizmos.DrawCube(transform.position + (Vector3)box.offset, box.size);
        else
            Gizmos.DrawSphere(transform.position, 0.4f);
    }
}