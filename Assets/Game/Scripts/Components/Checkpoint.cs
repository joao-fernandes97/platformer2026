using UnityEngine;

/// <summary>
/// A trigger zone that, when any player enters, registers itself with
/// CheckpointManager as the current respawn point.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class Checkpoint : MonoBehaviour
{
    [Header("Ordering")]
    [Tooltip("Set all to 0 to disable ordering (last-touched is active).")]
    public int orderIndex = 0;

    [Header("Spawn Point")]
    public Vector2 spawnOffset = new Vector2(0f, 1f);

    [Header("Visuals")]
    public GameObject inactiveVisual;
    public GameObject visitedVisual;
    public GameObject activeVisual;

    public enum CheckpointState { Inactive, Visited, Active }

    public CheckpointState State { get; private set; } = CheckpointState.Inactive;

    /// <summary>World-space position players will respawn at.</summary>
    public Vector3 SpawnPosition => transform.position + (Vector3)spawnOffset;

#region LIFECYCLE
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
#endregion
    
#region Public API

    /// <summary>Update visual state. Called by CheckpointManager when the active checkpoint changes.</summary>
    public void SetVisualState(CheckpointState newState)
    {
        State = newState;

        if (inactiveVisual != null) inactiveVisual.SetActive(newState == CheckpointState.Inactive);
        if (visitedVisual  != null) visitedVisual .SetActive(newState == CheckpointState.Visited);
        if (activeVisual   != null) activeVisual  .SetActive(newState == CheckpointState.Active);
    }
#endregion
    
#region Helpers
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
#endregion
}