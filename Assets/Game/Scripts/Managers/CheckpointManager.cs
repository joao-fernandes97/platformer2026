using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Singleton that owns the active checkpoint and drives respawning for all players.
///
/// ═══════════════════════════════════════════════════════════
///  HOW IT WORKS
/// ═══════════════════════════════════════════════════════════
///  • Checkpoints call PlayerReachedCheckpoint() on enter.
///  • The manager activates the checkpoint (respecting orderIndex) and
///    updates every other checkpoint's visual state.
///  • It subscribes to every PlayerController's HealthComponent.OnDied.
///    When ALL living players have died it waits respawnDelay seconds,
///    then respawns everyone at the active checkpoint.
///  • TriggerRespawn() is also public for UI buttons, a reset key,
///    or a future ServerRpc.
///
/// ═══════════════════════════════════════════════════════════
///  SETUP
/// ═══════════════════════════════════════════════════════════
///  1. Add to any persistent GameObject in your scene (e.g. GameManager).
///  2. Optionally assign a defaultSpawnPoint — if no checkpoint has been
///     activated yet, players respawn here instead.
///  3. Tune respawnDelay and the optional events in the Inspector.
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION (NGO server-auth)
/// ═══════════════════════════════════════════════════════════
///  1. Inherit NetworkBehaviour.
///  2. Wrap PlayerReachedCheckpoint and TriggerRespawn with IsServer guards.
///     Clients send ServerRpc; server validates and executes.
///  3. RespawnAll → ClientRpc: each client repositions its local player
///     (NetworkTransform will sync the server-authoritative position).
///  4. _activeCheckpoint index can be a NetworkVariable<int> so late-joining
///     clients immediately know the correct spawn point.
/// </summary>
public class CheckpointManager : MonoBehaviour
{
    public static CheckpointManager Instance { get; private set; }

    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    [Header("Spawn")]
    [Tooltip("Fallback spawn position used before any checkpoint is activated. " +
             "If left null, the manager uses Vector3.zero as the fallback.")]
    public Transform defaultSpawnPoint;

    [Tooltip("Seconds between all players dying and respawning. " +
             "Use this window for a death screen or fade-out effect.")]
    public float respawnDelay = 1f;

    [Header("Screen Transition")]
    [Tooltip("Optional ScreenFader used to black out the screen during respawn. " +
             "Leave null to skip the fade effect.")]
    public ScreenFader screenFader;

    [Tooltip("Seconds to fade the screen to black before repositioning players.")]
    public float fadeOutDuration = 0.35f;

    [Tooltip("Seconds to hold on black while players are being placed.")]
    public float holdDuration = 0.1f;

    [Tooltip("Seconds to fade back in after players have been placed.")]
    public float fadeInDuration = 0.45f;

    [Header("Reset Input")]
    [Tooltip("When true, all players can be manually reset to the active checkpoint " +
             "by pressing the reset key below. Useful during development or " +
             "as a cooperative 'give up' option.")]
    public bool allowManualReset = true;

    [Tooltip("Keyboard key that triggers a manual respawn for all players.")]
    public KeyCode manualResetKey = KeyCode.R;

    [Header("Events — Inspector")]
    [Tooltip("Fired when a new checkpoint becomes active. Parameter = the Checkpoint that was activated.")]
    public UnityEvent<Checkpoint> OnCheckpointActivated;

    [Tooltip("Fired when the respawn sequence begins (before the delay).")]
    public UnityEvent OnRespawnStarted;

    [Tooltip("Fired when all players have been repositioned and re-enabled.")]
    public UnityEvent OnRespawnComplete;

    // ════════════════════════════════════════════════════════
    // RUNTIME STATE
    // ════════════════════════════════════════════════════════

    private Checkpoint         _activeCheckpoint;
    private bool               _respawnPending;

    // We track subscriptions manually so we can unsubscribe cleanly.
    private readonly Dictionary<HealthComponent, PlayerController>
        _trackedPlayers = new();

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        // Subscribe to every PlayerController already in the scene.
        // Late-spawned players are handled by TrackPlayer(), which should
        // be called from wherever players are instantiated at runtime.
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            TrackPlayer(pc);
    }

    private void Update()
    {
        if (allowManualReset && Input.GetKeyDown(manualResetKey))
            TriggerRespawn();
    }

    private void OnDestroy()
    {
        // Clean up all OnDied subscriptions to avoid memory leaks.
        foreach (var kvp in _trackedPlayers)
            if (kvp.Key != null)
                kvp.Key.OnDied -= OnPlayerDied;

        _trackedPlayers.Clear();
    }

    // ════════════════════════════════════════════════════════
    // CHECKPOINT ACTIVATION
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// Called by a Checkpoint when a player enters its trigger.
    /// Activates the checkpoint if it is further along than the current one.
    /// </summary>
    public void PlayerReachedCheckpoint(Checkpoint checkpoint)
    {
        // Respect one-way ordering: never step backward.
        if (_activeCheckpoint != null &&
            checkpoint.orderIndex < _activeCheckpoint.orderIndex)
            return;

        // Already the active one — nothing to do.
        if (checkpoint == _activeCheckpoint) return;

        // Downgrade the previous active checkpoint to Visited.
        if (_activeCheckpoint != null)
            _activeCheckpoint.SetVisualState(Checkpoint.CheckpointState.Visited);

        _activeCheckpoint = checkpoint;
        _activeCheckpoint.SetVisualState(Checkpoint.CheckpointState.Active);

        Debug.Log($"[CheckpointManager] Active checkpoint → {checkpoint.name} (index {checkpoint.orderIndex})");
        OnCheckpointActivated?.Invoke(checkpoint);
    }

    // ════════════════════════════════════════════════════════
    // DEATH TRACKING
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// Subscribe to a player's death events.
    /// Call this whenever a new PlayerController is spawned at runtime.
    /// (In a scene with pre-placed players, Start() handles it automatically.)
    /// </summary>
    public void TrackPlayer(PlayerController pc)
    {
        var health = pc.GetComponent<HealthComponent>();
        if (health == null || _trackedPlayers.ContainsKey(health)) return;

        _trackedPlayers[health] = pc;
        health.OnDied += OnPlayerDied;
    }

    private void OnPlayerDied()
    {
        // If a respawn is already queued, don't stack another.
        if (_respawnPending) return;

        // Only auto-respawn once every living player is dead.
        // GetAllLiving() excludes dead players — if none remain, trigger respawn.
        var living = PlayerRegistry.Instance?.GetAllLiving();
        if (living != null && living.Count > 0) return;

        StartCoroutine(RespawnAfterDelay());
    }

    // ════════════════════════════════════════════════════════
    // RESPAWN
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// Public entry point. Respawns all players at the active checkpoint
    /// (or the default spawn point if no checkpoint has been reached yet).
    ///
    /// Safe to call even while players are alive — use for a cooperative
    /// "reset" option or after a puzzle failure, etc.
    ///
    /// MULTIPLAYER MIGRATION: guard with IsServer, then call a ClientRpc
    /// so each client repositions its own local player.
    /// </summary>
    public void TriggerRespawn()
    {
        if (_respawnPending) return;
        StartCoroutine(RespawnAfterDelay());
    }

    private IEnumerator RespawnAfterDelay()
    {
        _respawnPending = true;
        OnRespawnStarted?.Invoke();

        // ── Fade out ─────────────────────────────────────────
        if (screenFader != null)
            yield return screenFader.FadeOut(fadeOutDuration);
        else
            yield return new WaitForSeconds(respawnDelay);   // fallback: plain delay

        // ── Reposition players and hold so we don't see camera movement ─────
        RespawnAll();
        
        if (screenFader != null)
            yield return new WaitForSeconds(holdDuration);

        

        // ── Fade back in ─────────────────────────────────────
        if (screenFader != null)
            yield return screenFader.FadeIn(fadeInDuration);

        OnRespawnComplete?.Invoke();

        _respawnPending = false;
    }

    private void RespawnAll()
    {
        Vector3 spawnPos = GetSpawnPosition();

        // Grab ALL players — alive or dead — from the tracked dictionary.
        // We can't use PlayerRegistry.GetAllLiving() here because dead players
        // are deregistered; we need to reach them to revive them.
        var allPlayers = new List<PlayerController>(_trackedPlayers.Values);

        // Offset multiple players slightly so they don't overlap exactly.
        float spacing = 1f;
        float totalWidth = (allPlayers.Count - 1) * spacing;

        for (int i = 0; i < allPlayers.Count; i++)
        {
            var pc = allPlayers[i];
            if (pc == null) continue;

            float xOffset = -totalWidth * 0.5f + i * spacing;
            Vector3 targetPos = spawnPos + new Vector3(xOffset, 0f, 0f);

            RespawnPlayer(pc, targetPos);
        }

        Debug.Log($"[CheckpointManager] Respawned {allPlayers.Count} player(s) at {spawnPos}");
    }

    private void RespawnPlayer(PlayerController pc, Vector3 position)
    {
        // 1. Revive health (resets IsDead, restores HP, fires OnHealthChanged).
        var health = pc.GetComponent<HealthComponent>();
        health?.Revive();

        // 2. Reposition before re-enabling so the player never flashes at the
        //    wrong location (PlayerController.OnEnable registers with the registry).
        var rb = pc.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.linearVelocity = Vector2.zero;
            rb.bodyType       = RigidbodyType2D.Dynamic;
        }

        pc.transform.position = position;

        // 3. Re-enable the PlayerController — this triggers OnEnable →
        //    PlayerRegistry.Register(), so enemies can target this player again.
        pc.enabled = true;
    }

    // ════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════

    private Vector3 GetSpawnPosition()
    {
        if (_activeCheckpoint != null)
            return _activeCheckpoint.SpawnPosition;

        if (defaultSpawnPoint != null)
            return defaultSpawnPoint.position;

        Debug.LogWarning("[CheckpointManager] No active checkpoint and no default spawn point set. Respawning at world origin.");
        return Vector3.zero;
    }

    // ════════════════════════════════════════════════════════
    // PUBLIC ACCESSORS
    // ════════════════════════════════════════════════════════

    /// <summary>The currently active checkpoint, or null if none reached yet.</summary>
    public Checkpoint ActiveCheckpoint => _activeCheckpoint;

    /// <summary>True while a respawn sequence is in progress.</summary>
    public bool IsRespawnPending => _respawnPending;
}