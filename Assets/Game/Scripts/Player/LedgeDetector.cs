using UnityEngine;

/// <summary>
/// Detects climbable ledges via two raycasts:
///   1. "Wall check"  — horizontal, detects a wall at chest height
///   2. "Ledge check" — horizontal at a higher point; if wall hit but ledge missed → valid ledge
///
/// Attach to the same GameObject as PlayerController.
///
/// MULTIPLAYER NOTE:
///   This runs on every client. For server-auth, validate the detected ledge point
///   on the server before committing the grab state (send a ServerRpc with the point,
///   server re-casts and confirms).
/// </summary>
public class LedgeDetector : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Layer(s) that count as climbable geometry.")]
    public LayerMask geometryLayers;

    [Tooltip("How far forward to cast rays.")]
    public float rayLength = 0.6f;

    [Tooltip("Height offset from transform.position for the lower (wall) ray.")]
    public float wallRayHeight = 0.8f;

    [Tooltip("Height offset for the upper (ledge top) ray — must be above ledge.")]
    public float ledgeRayHeight = 1.4f;

    [Tooltip("Small downward offset applied to find the exact ledge surface.")]
    public float ledgeSurfaceDropOffset = 0.1f;

    [Tooltip("Climb target offset")]
    public float climbTargetOffset = 0.3f;

    // ── Public Results ────────────────────────────────────────────────────────
    /// <summary>True when a valid ledge is in front of the player.</summary>
    public bool  LedgeDetected   { get; private set; }

    /// <summary>World-space point the player's hands should reach (wall surface).</summary>
    public Vector3 LedgePoint    { get; private set; }

    /// <summary>World-space point the player should stand at after climbing.</summary>
    public Vector3 ClimbTarget   { get; private set; }

    // ── Internal ─────────────────────────────────────────────────────────────
    private Transform _tf;
    private float     _facingSign = 1f;   // +1 right, -1 left

    private void Awake() => _tf = transform;

    // ── Called by PlayerController each FixedUpdate ──────────────────────────
    public void UpdateDetection(float facingSign)
    {
        _facingSign  = facingSign;
        LedgeDetected = false;

        Vector3 origin     = _tf.position;
        Vector3 direction  = Vector3.right * _facingSign;

        Vector3 wallOrigin  = origin + Vector3.up * wallRayHeight;
        Vector3 ledgeOrigin = origin + Vector3.up * ledgeRayHeight;

        bool wallHit  = Physics2D.Raycast(wallOrigin,  direction, rayLength, geometryLayers);
        bool ledgeHit = Physics2D.Raycast(ledgeOrigin, direction, rayLength, geometryLayers);

        // Wall present but top is clear → there's a ledge to grab
        if (wallHit && !ledgeHit)
        {
            RaycastHit2D hit = Physics2D.Raycast(wallOrigin, direction, rayLength, geometryLayers);

            if (hit.collider != null)
            {
                LedgePoint  = hit.point;

                // Find the top surface of the ledge via a downward cast from above
                Vector3 aboveLedge = new Vector3(
                    hit.point.x + direction.x * 0.1f,
                    ledgeOrigin.y + ledgeSurfaceDropOffset,
                    origin.z);

                RaycastHit2D surfaceHit = Physics2D.Raycast(aboveLedge, Vector3.down, ledgeRayHeight, geometryLayers);
                if (surfaceHit.collider != null)
                {
                    ClimbTarget   = new Vector3(
                        surfaceHit.point.x - direction.x * climbTargetOffset,  // stand slightly back from edge
                        surfaceHit.point.y,
                        origin.z);
                    LedgeDetected = true;
                }
            }
        }
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────
    private void OnDrawGizmosSelected()
    {
        if (_tf == null) _tf = transform;

        Vector3 dir = Vector3.right * _facingSign;

        // Wall ray
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(_tf.position + Vector3.up * wallRayHeight,  dir * rayLength);

        // Ledge top ray
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(_tf.position + Vector3.up * ledgeRayHeight, dir * rayLength);

        if (LedgeDetected)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(LedgePoint,  0.05f);
            Gizmos.DrawSphere(ClimbTarget, 0.08f);
        }
    }
}