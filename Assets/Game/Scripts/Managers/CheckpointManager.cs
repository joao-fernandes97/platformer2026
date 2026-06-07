using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Unity.Netcode;

/// <summary>
/// Singleton that owns the active checkpoint and drives respawning for all players.
/// </summary>
public class CheckpointManager : NetworkBehaviour
{
    public static CheckpointManager Instance { get; private set; }

    [Header("Checkpoints")]
    public List<Checkpoint> checkpoints = new();

    [Header("Spawn")]
    public Transform defaultSpawnPoint;
    public float respawnDelay = 1f;

    [Header("Screen Transition")]
    [Tooltip("Optional ScreenFader used to black out the screen during respawn.")]
    public ScreenFader screenFader;
    public float fadeOutDuration = 0.35f;
    public float holdDuration = 0.1f;
    public float fadeInDuration = 0.45f;

    [Header("Reset Input")]
    public bool allowManualReset = true;
    public KeyCode manualResetKey = KeyCode.R;

    [Header("Events")]
    public UnityEvent<Checkpoint> OnCheckpointActivated;
    public UnityEvent OnRespawnStarted;
    public UnityEvent OnRespawnComplete;

    // Network State
    /// <summary>
    /// Index into the checkpoints list.
    /// -1 means no checkpoint has been activated yet.
    /// Initialised to -1 via the default value; clients read it in
    /// OnNetworkSpawn to restore the correct visual state.
    /// </summary>
    private NetworkVariable<int> _activeCheckpointIndex = new(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Local State

    // Convenience reference resolved from _activeCheckpointIndex.
    private Checkpoint _activeCheckpoint;

    // Server-only: prevents stacking multiple respawn coroutines.
    private bool _respawnPending;

    private readonly Dictionary<HealthComponent, PlayerController>
        _trackedPlayers = new();

    
#region  Lifecycle

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        // Subscribe so every client (including server) reacts to checkpoint changes.
        _activeCheckpointIndex.OnValueChanged += OnActiveCheckpointIndexChanged;

        // If this client joins late, apply the current checkpoint state immediately.
        if (_activeCheckpointIndex.Value >= 0)
            ApplyCheckpointLocally(_activeCheckpointIndex.Value);

        // Server subscribes to player deaths and tracks existing players.
        if (IsServer)
        {
            foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
                TrackPlayer(pc);
        }
    }

    public override void OnNetworkDespawn()
    {
        _activeCheckpointIndex.OnValueChanged -= OnActiveCheckpointIndexChanged;

        if (IsServer)
        {
            foreach (var kvp in _trackedPlayers)
                if (kvp.Key != null)
                    kvp.Key.OnDied -= OnPlayerDied;

            _trackedPlayers.Clear();
        }
    }

    private void Update()
    {
        // Any client can press the reset key; the actual logic runs on the server.
        if (allowManualReset && Input.GetKeyDown(manualResetKey))
            TriggerRespawnServerRpc();
    }
#endregion

#region Checkpoint Activation

    /// <summary>
    /// Called by Checkpoint.OnTriggerEnter2D on the local client.
    /// Forwards the request to the server via ServerRpc.
    /// </summary>
    public void PlayerReachedCheckpoint(Checkpoint checkpoint)
    {
        int idx = checkpoints.IndexOf(checkpoint);
        if (idx < 0)
        {
            Debug.LogWarning($"[CheckpointManager] Checkpoint '{checkpoint.name}' is not in the checkpoints list.");
            return;
        }
        PlayerReachedCheckpointServerRpc(idx);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void PlayerReachedCheckpointServerRpc(int checkpointIndex)
    {
        if (checkpointIndex < 0 || checkpointIndex >= checkpoints.Count) return;

        Checkpoint checkpoint = checkpoints[checkpointIndex];

        // Respect one-way ordering: never step backward.
        if (_activeCheckpoint != null &&
            checkpoint.orderIndex < _activeCheckpoint.orderIndex)
            return;

        
        if (checkpoint == _activeCheckpoint) return;

        // Writing the NetworkVariable broadcasts the new index to all clients,
        // which triggers OnActiveCheckpointIndexChanged on each one.
        _activeCheckpointIndex.Value = checkpointIndex;

        Debug.Log($"[CheckpointManager] Active checkpoint → {checkpoint.name} (index {checkpoint.orderIndex})");
        OnCheckpointActivated?.Invoke(checkpoint);
    }

    /// <summary>
    /// Runs on every client (including host) when the NetworkVariable changes.
    /// Updates the local _activeCheckpoint reference and visual states.
    /// </summary>
    private void OnActiveCheckpointIndexChanged(int oldIndex, int newIndex)
    {
        ApplyCheckpointLocally(newIndex);
    }

    private void ApplyCheckpointLocally(int newIndex)
    {
        if (newIndex < 0 || newIndex >= checkpoints.Count) return;

        // Downgrade the previous one to Visited.
        if (_activeCheckpoint != null)
            _activeCheckpoint.SetVisualState(Checkpoint.CheckpointState.Visited);

        _activeCheckpoint = checkpoints[newIndex];
        _activeCheckpoint.SetVisualState(Checkpoint.CheckpointState.Active);
    }
#endregion
    
#region  Death Tracking
    /// <summary>
    /// Subscribe to a player's death events. Server-only.
    /// Call this whenever a new PlayerController NetworkObject is spawned.
    /// </summary>
    public void TrackPlayer(PlayerController pc)
    {
        if (!IsServer) return;

        var health = pc.GetComponent<HealthComponent>();
        if (health == null || _trackedPlayers.ContainsKey(health)) return;

        _trackedPlayers[health] = pc;
        health.OnDied += OnPlayerDied;
    }

    private void OnPlayerDied()
    {
        // Server-only: death events are subscribed only on the server.
        if (_respawnPending) return;

        var living = PlayerRegistry.Instance?.GetAllLiving();
        if (living != null && living.Count > 0) return;

        StartCoroutine(RespawnSequenceServerCoroutine());
    }
#endregion
    
#region Respawn (Server)
    /// <summary>
    /// Any client (or server) can call this to request a full-party respawn.
    /// The server validates and runs the sequence.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TriggerRespawnServerRpc()
    {
        if (_respawnPending) return;
        StartCoroutine(RespawnSequenceServerCoroutine());
    }

    private IEnumerator RespawnSequenceServerCoroutine()
    {
        _respawnPending = true;
        OnRespawnStarted?.Invoke();

        // Give clients time to play their fade-out before we move anyone.
        // The ClientRpc below tells each client to start its own fader;
        // we wait the same duration here so repositioning is hidden.
        BeginFadeOutClientRpc(fadeOutDuration);
        yield return new WaitForSeconds(fadeOutDuration);

        // ── Resolve spawn positions on the server ─────────────
        Vector3 spawnPos   = GetSpawnPosition();
        var allPlayers     = new List<PlayerController>(_trackedPlayers.Values);
        float spacing      = 1f;
        float totalWidth   = (allPlayers.Count - 1) * spacing;

        // Build per-player spawn data arrays for the ClientRpc.
        // We send NetworkObjectId so each client can identify its own player.
        var ids       = new ulong[allPlayers.Count];
        var positions = new Vector3[allPlayers.Count];

        for (int i = 0; i < allPlayers.Count; i++)
        {
            var pc = allPlayers[i];
            if (pc == null) continue;

            float xOffset = -totalWidth * 0.5f + i * spacing;
            positions[i]  = spawnPos + new Vector3(xOffset, 0f, 0f);
            ids[i]        = pc.GetComponent<NetworkObject>().NetworkObjectId;

            // Revive on the server so IsDead is cleared before the ClientRpc fires.
            pc.GetComponent<HealthComponent>()?.Revive();
            pc.enabled = true;
        }

        Debug.Log($"[CheckpointManager] Respawning {allPlayers.Count} player(s) at {spawnPos}");

        // Reset all living enemies to their spawn positions
        foreach (var enemy in FindObjectsByType<EnemyController>(FindObjectsSortMode.None))
            enemy.ResetToSpawn();

        // Tell every client to reposition its local player
        RespawnAllClientRpc(ids, positions, holdDuration, fadeInDuration);

        _respawnPending = false;
    }
#endregion
    
#region Respawn (Client)
        /// <summary>
    /// Tells each client to fade its screen to black (before the server
    /// moves anyone). Called at the start of the server sequence so client
    /// and server timings stay in sync.
    /// </summary>
    [ClientRpc]
    private void BeginFadeOutClientRpc(float duration)
    {
        screenFader?.FadeOut(duration);
    }

    /// <summary>
    /// Runs on every client. Each client finds its own local player in the
    /// ids array, repositions it, then runs the hold + fade-in.
    /// </summary>
    [ClientRpc]
    private void RespawnAllClientRpc(ulong[] ids, Vector3[] positions, float hold, float fadeIn)
    {
        StartCoroutine(ClientRespawnCoroutine(ids, positions, hold, fadeIn));
    }

    private IEnumerator ClientRespawnCoroutine(
        ulong[] ids, Vector3[] positions, float hold, float fadeIn)
    {
        // Reposition only the locally owned player so clients don't fight
        // NetworkTransform for authority on other players' objects.
        for (int i = 0; i < ids.Length; i++)
        {
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects
                    .TryGetValue(ids[i], out var netObj)) continue;

            var pc = netObj.GetComponent<PlayerController>();
            if (pc == null) continue;

            // Each client moves only its own player.
            if (!netObj.IsOwner) continue;

            RespawnPlayerLocally(pc, positions[i]);
        }

        // Snap the camera now that the player is in place.
        NetworkCameraController.Instance?.SnapNow();

        // Hold on black, then fade back in.
        yield return new WaitForSeconds(hold);

        if (screenFader != null)
            yield return screenFader.FadeIn(fadeIn);

        OnRespawnComplete?.Invoke();
    }

    /// <summary>
    /// Repositions and re-enables a single PlayerController on the local client.
    /// Mirrors the old RespawnPlayer() but without the Revive() call
    /// (health is revived on the server; HealthComponent syncs via NetworkVariable).
    /// </summary>
    private void RespawnPlayerLocally(PlayerController pc, Vector3 position)
    {
        var rb = pc.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.linearVelocity = Vector2.zero;
            rb.bodyType       = RigidbodyType2D.Dynamic;
        }

        pc.transform.position = position;
        pc.enabled            = true;
    }
#endregion

    // Helpers
    private Vector3 GetSpawnPosition()
    {
        if (_activeCheckpoint != null)
            return _activeCheckpoint.SpawnPosition;

        if (defaultSpawnPoint != null)
            return defaultSpawnPoint.position;

        Debug.LogWarning("[CheckpointManager] No active checkpoint and no default spawn point. Respawning at world origin.");
        return Vector3.zero;
    }

    // Public accessors
    /// <summary>The currently active checkpoint, or null if none reached yet.</summary>
    public Checkpoint ActiveCheckpoint => _activeCheckpoint;

    /// <summary>True while a respawn sequence is in progress. Server-only.</summary>
    public bool IsRespawnPending => _respawnPending;
}