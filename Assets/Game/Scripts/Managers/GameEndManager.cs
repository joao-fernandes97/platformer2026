using System.Collections;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Handles the end-of-level sequence for all clients.
/// </summary>
public class GameEndManager : NetworkBehaviour
{
    [Header("References")]
    public ScreenFader screenFader;
    public GameObject endScreenPanel;

    [Header("Timing")]
    public float fadeOutDuration = 0.6f;
    public float holdDuration = 0.3f;
    public float fadeInDuration = 0.8f;

    
    // Runtime state
    private bool _ended = false;

#region Lifecycle
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // When a respawn completes (R pressed mid-end-screen), hide the panel
        // and allow the end sequence to fire again if the exit is re-triggered.
        if (CheckpointManager.Instance != null)
            CheckpointManager.Instance.OnRespawnComplete.AddListener(OnRespawnComplete);
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        if (CheckpointManager.Instance != null)
            CheckpointManager.Instance.OnRespawnComplete.RemoveListener(OnRespawnComplete);
    }

    private void OnRespawnComplete()
    {
        // Runs on every client — hide the end panel and reset state so the
        // level can be completed again after a manual restart.
        if (endScreenPanel != null)
            endScreenPanel.SetActive(false);

        // Reset the server-side guard via Rpc so both sides agree.
        ResetEndedServerRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ResetEndedServerRpc()
    {
        _ended = false;
    }
#endregion

    /// <summary>
    /// Wire this to CoopExitTrigger.OnExitTriggered in the Inspector.
    /// Safe to call from any client or the server — forwards to the server.
    /// </summary>
    public void OnLevelExitTriggered()
    {
        if (_ended) return;
        TriggerEndServerRpc();
    }

    // Server RPC
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void TriggerEndServerRpc()
    {
        if (_ended) return;
        _ended = true;
        ShowEndScreenClientRpc();
    }

    // Client RPC
    [ClientRpc]
    private void ShowEndScreenClientRpc()
    {
        Debug.Log("[GameEndManger] ShowEndScreenClientRPC");
        StartCoroutine(EndSequence());
    }

    // End Sequence
    private IEnumerator EndSequence()
    {
        // Fade to black
        if (screenFader != null)
            yield return screenFader.FadeOut(fadeOutDuration);
        else
            yield return new WaitForSeconds(fadeOutDuration);

        // Hold
        yield return new WaitForSeconds(holdDuration);

        if (endScreenPanel != null)
            endScreenPanel.SetActive(true);

        // Fade back in to reveal it
        if (screenFader != null)
            yield return screenFader.FadeIn(fadeInDuration);
    }
}