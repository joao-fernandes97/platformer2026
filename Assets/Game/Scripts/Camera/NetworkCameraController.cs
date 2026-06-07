using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Online co-op camera controller.
/// Each client runs this on its own Camera. It tracks the locally-owned
/// PlayerController only.
/// </summary>
[RequireComponent(typeof(Camera))]
public class NetworkCameraController : MonoBehaviour
{
    public static NetworkCameraController Instance { get; private set; }

    [Header("Following")]
    public float followSpeed = 6f;
    public float orthoSize = 7f;

    [Header("Look-ahead")]
    public float lookAheadDistance = 2f;
    public float lookAheadSpeed = 4f;

    [Header("World Bounds")]
    public BoxCollider2D cameraLimits;

    private Camera    _cam;
    private Transform _target;

    // Smoothed world position of the camera (excludes Z).
    private Vector3   _smoothedPos;

    // Current look-ahead offset, blended over time.
    private float     _currentLookAhead;

#region Lifecycle

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _cam                   = GetComponent<Camera>();
        _cam.orthographic      = true;
        _cam.orthographicSize  = orthoSize;
        _smoothedPos           = transform.position;
    }

    private void LateUpdate()
    {
        if (_target == null) return;

        //look-ahead
        float facingSign = 1f;
        if (_target.TryGetComponent<PlayerController>(out var pc))
            facingSign = pc.FacingSign;

        float targetLookAhead = facingSign * lookAheadDistance;
        _currentLookAhead = Mathf.Lerp(
            _currentLookAhead, targetLookAhead, lookAheadSpeed * Time.deltaTime);

        //follow decay
        Vector2 desiredXY = (Vector2)_target.position + Vector2.right * _currentLookAhead;

        float   k          = Mathf.Exp(-followSpeed * Time.deltaTime);
        float   z          = transform.position.z;
        Vector3 desired3   = new Vector3(desiredXY.x, desiredXY.y, z);

        _smoothedPos = new Vector3(
            Mathf.Lerp(desired3.x, _smoothedPos.x, k),
            Mathf.Lerp(desired3.y, _smoothedPos.y, k),
            z);

        transform.position = ClampToBounds(_smoothedPos);
    }
#endregion

#region Public API
    /// <summary>
    /// Set the transform this camera will follow.
    /// Call from NetworkPlayerSpawner after the local player is spawned.
    /// Pass null to stop following.
    /// </summary>
    public void SetTarget(Transform target)
    {
        _target = target;

        if (target != null)
            SnapNow();
    }

    /// <summary>
    /// Instantly teleport the camera to the current target.
    /// Called by CheckpointManager after a respawn so there's no
    /// visible slide-in when the screen fades back in.
    /// </summary>
    public void SnapNow()
    {
        if (_target == null) return;

        float    z   = transform.position.z;
        Vector3  pos = new Vector3(_target.position.x, _target.position.y, z);
        pos = ClampToBounds(pos);

        _smoothedPos       = pos;
        transform.position = pos;
        _currentLookAhead  = 0f;
    }
#endregion

#region Helpers
    private Vector3 ClampToBounds(Vector3 pos)
    {
        if (cameraLimits == null) return pos;

        Bounds b    = cameraLimits.bounds;
        float halfH = _cam.orthographicSize;
        float halfW = _cam.orthographicSize * _cam.aspect;

        pos.x = Mathf.Clamp(pos.x, b.min.x + halfW, b.max.x - halfW);
        pos.y = Mathf.Clamp(pos.y, b.min.y + halfH, b.max.y - halfH);
        return pos;
    }

    private void OnDrawGizmosSelected()
    {
        if (cameraLimits == null) return;
        Bounds b = cameraLimits.bounds;
        Gizmos.color = Color.green;
        Gizmos.DrawLine(new Vector3(b.min.x, b.min.y), new Vector3(b.max.x, b.min.y));
        Gizmos.DrawLine(new Vector3(b.max.x, b.min.y), new Vector3(b.max.x, b.max.y));
        Gizmos.DrawLine(new Vector3(b.max.x, b.max.y), new Vector3(b.min.x, b.max.y));
        Gizmos.DrawLine(new Vector3(b.min.x, b.max.y), new Vector3(b.min.x, b.min.y));
    }
#endregion
}