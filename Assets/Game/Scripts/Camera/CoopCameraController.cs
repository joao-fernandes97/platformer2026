using UnityEngine;

/// <summary>
/// Local co-op camera for two players.
///
/// ═══════════════════════════════════════════════════════════
///  BEHAVIOUR OVERVIEW
/// ═══════════════════════════════════════════════════════════
///
///  SHARED MODE  (players close together)
///    • Follows the midpoint of both living players.
///    • Zooms the orthographic size out so both players stay
///      inside a padded viewport.
///    • Once either player's look-ahead space (screen fraction
///      in their facing direction) drops below splitLookAheadThreshold,
///      split-screen activates.
///
///  SPLIT-SCREEN MODE  (players too far apart)
///    • This camera tracks Player 1; a spawned Camera tracks
///      Player 2.
///    • Each half gets a viewport rect covering its half of
///      the screen.
///    • The split and the camera POSITIONS blend smoothly —
///      no hard pop.
///
///  SINGLE-PLAYER FALLBACK
///    • If only one player is registered the camera follows
///      them at baseOrthoSize. No split occurs.
///
/// ═══════════════════════════════════════════════════════════
///  SETUP
/// ═══════════════════════════════════════════════════════════
///  1. Attach to your main Camera GameObject.
///  2. The second camera is created automatically at runtime.
///  3. Optionally assign cameraLimits (BoxCollider2D) to keep
///     the camera inside level bounds.
///  4. Tune the Inspector values; defaults work for most
///     side-scrollers.
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION (NGO / online co-op)
/// ═══════════════════════════════════════════════════════════
///  Each client gets their own camera, so split-screen is
///  irrelevant online. Migration path:
///
///  After spawning the local player call:
///      CoopCameraController.Instance.SetSingleTarget(localPlayer.transform);
///  This locks the camera to that transform only and disables
///  all split logic — no other code changes needed.
/// </summary>
[RequireComponent(typeof(Camera))]
public class CoopCameraController : MonoBehaviour
{
    public static CoopCameraController Instance { get; private set; }

    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    [Header("Following")]
    [Tooltip("Exponential follow speed. Higher = snappier.")]
    public float followSpeed = 5f;

    [Tooltip("Orthographic size with one player, or both players close.")]
    public float baseOrthoSize = 7f;

    [Tooltip("World-unit padding added around the player bounding box when zooming out.")]
    public float zoomPadding = 2f;

    [Tooltip("Hard cap on the shared zoom. The camera never zooms out beyond this regardless of player distance.")]
    public float maxOrthoSize = 12f;

    [Tooltip("Minimum orthographic size (prevents zooming in too far when players overlap).")]
    public float minOrthoSize = 5f;

    [Header("Split Screen")]
    [Tooltip("Whether the screen splits left/right (Vertical) or top/bottom (Horizontal).")]
    public SplitAxis splitAxis = SplitAxis.Vertical;

    [Tooltip("World-unit distance between players at which split-screen engages. " +
             "splitLookAheadThreshold and mergeOrthoSize are derived from this automatically " +
             "so the split and merge distances can never overlap and cause ping-pong.")]
    public float splitDistance = 80f;

    [Tooltip("Hysteresis gap in world units. Cameras re-merge when players close to " +
             "(splitDistance - mergeHysteresis). Larger = merge point further from split point.")]
    public float mergeHysteresis = 20f;
    
    [Tooltip("Blend speed between shared and split-screen modes (seconds to fully transition).")]
    public float splitTransitionSpeed = 2f;

    [Tooltip("Seconds to wait after a transition completes before the split/merge condition is " +
             "re-evaluated. Prevents rapid toggling when players hover right at a threshold.")]
    public float splitDecisionCooldown = 1.5f;

    // Derived each frame — not exposed in Inspector.
    private float _splitLookAheadThreshold;

    [Tooltip("Orthographic size used for each half-screen camera in split mode.")]
    public float splitOrthoSize = 7f;

    [Tooltip("Thickness in pixels of the divider bar between the two viewports.")]
    public int dividerPixels = 4;

    [Tooltip("Colour of the divider line.")]
    public Color dividerColor = Color.black;

    [Header("World Bounds")]
    [Tooltip("Optional BoxCollider2D that the camera cannot leave.")]
    public BoxCollider2D cameraLimits;

    // ════════════════════════════════════════════════════════
    // TYPES
    // ════════════════════════════════════════════════════════

    public enum SplitAxis { Vertical, Horizontal }

    // ════════════════════════════════════════════════════════
    // PRIVATE STATE
    // ════════════════════════════════════════════════════════

    private Camera _cam;      // this camera  — shared OR P1 in split
    private Camera _cam2;     // auto-created — P2 in split

    // Smoothed world positions for each "view"
    private Vector3 _sharedPos;   // midpoint follow target
    private Vector3 _p1Pos;       // P1 follow target
    private Vector3 _p2Pos;       // P2 follow target

    // Single-target override (online migration)
    private Transform _singleTarget;
    private bool      _forceSingle;

    // 0 = fully shared,  1 = fully split
    private float _splitBlend = 0f;
    private bool  _wantSplit   = false;
    private float _splitCooldownTimer = 0f;

    // Divider GUI texture
    private Texture2D _dividerTex;

    // ════════════════════════════════════════════════════════
    // VIEWPORT RECTS
    // ════════════════════════════════════════════════════════

    private static readonly Rect FullRect  = new Rect(0f,   0f,   1f,   1f  );
    // Vertical split (left / right)
    private static readonly Rect LeftRect  = new Rect(0f,   0f,   0.5f, 1f  );
    private static readonly Rect RightRect = new Rect(0.5f, 0f,   0.5f, 1f  );
    // Horizontal split (top / bottom)
    private static readonly Rect TopRect   = new Rect(0f,   0.5f, 1f,   0.5f);
    private static readonly Rect BotRect   = new Rect(0f,   0f,   1f,   0.5f);

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _cam = GetComponent<Camera>();

        // Initialise smoothed positions at current location so there's
        // no slide-in on the first frame.
        _sharedPos = transform.position;
        _p1Pos     = transform.position;
        _p2Pos     = transform.position;

        CreateSecondCamera();
        CreateDividerTexture();
    }

    private void Start()
    {
        SnapToTargets();
    }

    private void LateUpdate()
    {
        var players = PlayerRegistry.Instance?.GetAllLiving();
        int count   = players?.Count ?? 0;

        // ── Derive thresholds from splitDistance ────────────────────────────────
        // split: threshold = 0.5 - splitDistance / (2 * orthoSize * aspect)
        // merge: fires when player separation falls below (splitDistance - mergeHysteresis),
        //        expressed as the ortho size that would frame that distance.
        // Because merge distance < split distance by construction, they can never overlap.
        float currentOrtho = _cam.orthographicSize > 0f ? _cam.orthographicSize : baseOrthoSize;
        _splitLookAheadThreshold = Mathf.Clamp(
            0.5f - splitDistance / (2f * currentOrtho * _cam.aspect), 0.05f, 0.49f);
        
        // ── Determine whether split should be active ─────────────────────────
        // _wantSplit is only re-evaluated when:
        //   (a) the blend has fully settled at 0 or 1, AND
        //   (b) the post-transition cooldown has expired.
        // This prevents two failure modes:
        //   1. Camera movement during a blend feeding back into the split
        //      condition and reversing the blend mid-way.
        //   2. Both engage and disengage conditions being true at the same
        //      player separation, causing the transition to ping-pong.
        bool transitionComplete = _splitBlend <= 0f || _splitBlend >= 1f;

        if (transitionComplete)
        {
            if (_splitCooldownTimer > 0f)
                _splitCooldownTimer -= Time.deltaTime;
        }
        else
        {
            // Reset the cooldown while transitioning so it always runs fresh
            // from the moment the blend settles.
            _splitCooldownTimer = splitDecisionCooldown;
        }

        bool canDecide = transitionComplete && _splitCooldownTimer <= 0f;

        if (!_forceSingle && count >= 2 && canDecide)
        {
            bool prevWantSplit = _wantSplit;

            if (_wantSplit)
            {
                // Fully split — merge when raw player separation drops below
                // (splitDistance - mergeHysteresis). Comparing distance directly
                // avoids the baseOrthoSize floor inside DesiredOrthoSize, which
                // would otherwise keep 'needed' above _mergeOrthoSize permanently.
                float dist = Vector2.Distance(players[0].transform.position,
                                              players[1].transform.position);
                _wantSplit = dist > (splitDistance - mergeHysteresis);
            }
            else
            {
                // Fully merged — engage if look-ahead pressure is high on either player.
                _wantSplit = ShouldSplit(players[0], players[1]);
            }

            // If the decision just flipped, arm the cooldown so the reverse
            // transition must also fully complete before re-evaluation.
            if (_wantSplit != prevWantSplit)
                _splitCooldownTimer = splitDecisionCooldown;
        }


        // ── Blend toward target split value ──────────────────────────────────
        float blendTarget = _wantSplit ? 1f : 0f;
        _splitBlend = Mathf.MoveTowards(
            _splitBlend, blendTarget, splitTransitionSpeed * Time.deltaTime);

        // ── Run the appropriate follow logic ─────────────────────────────────
        if (_forceSingle)
        {
            Vector2 t = _singleTarget != null
                ? (Vector2)_singleTarget.position
                : (Vector2)transform.position;
            FollowSingle(t);
        }
        else if (count == 0)
        {
            // No players — ensure split state is cleared
            _wantSplit            = false;
            _splitCooldownTimer   = 0f;
        }
        else if (count == 1)
        {
            // A player has died — force the blend back to shared mode
            _wantSplit            = false;
            _splitCooldownTimer   = 0f;
            FollowSingle(players[0].transform.position);
        }
        else
        {
            FollowTwo(players[0].transform.position, players[1].transform.position);
        }
    }

    // ════════════════════════════════════════════════════════
    // FOLLOW MODES
    // ════════════════════════════════════════════════════════

    private void FollowSingle(Vector2 target)
    {
        _sharedPos = ExponentialDecay(_sharedPos, target);
        _cam.transform.position = ClampToBounds(_sharedPos, _cam.orthographicSize, _cam);
        _cam.orthographicSize   = Mathf.Lerp(_cam.orthographicSize, baseOrthoSize,
                                              followSpeed * Time.deltaTime);
        _cam.rect     = FullRect;
        _cam2.enabled = false;
    }

    private void FollowTwo(Vector2 p1, Vector2 p2)
    {
        Vector2 mid     = (p1 + p2) * 0.5f;
        float   desired = Mathf.Clamp(DesiredOrthoSize(p1, p2), minOrthoSize, maxOrthoSize);

        // Always advance all three smoothed positions every frame so that
        // when we blend between them the transition is seamless.
        _sharedPos = ExponentialDecay(_sharedPos, mid);
        _p1Pos     = ExponentialDecay(_p1Pos,     p1 );
        _p2Pos     = ExponentialDecay(_p2Pos,     p2 );

        // ── Blend camera positions ───────────────────────────────────────────
        // At _splitBlend = 0: both cameras sit at the shared midpoint.
        // At _splitBlend = 1: cam → p1 only, cam2 → p2 only.
        Vector3 cam1WorldPos = Vector3.Lerp(_sharedPos, _p1Pos, _splitBlend);
        Vector3 cam2WorldPos = Vector3.Lerp(_sharedPos, _p2Pos, _splitBlend);

        // ── Blend ortho size ─────────────────────────────────────────────────
        float targetOrtho = Mathf.Lerp(desired, splitOrthoSize, _splitBlend);
        float newOrtho    = Mathf.Lerp(_cam.orthographicSize, targetOrtho,
                                       followSpeed * Time.deltaTime);

        _cam.orthographicSize  = newOrtho;
        _cam2.orthographicSize = newOrtho;

        // ── Apply clamped positions ──────────────────────────────────────────
        _cam.transform.position  = ClampToBounds(cam1WorldPos, newOrtho, _cam);
        _cam2.transform.position = ClampToBounds(cam2WorldPos, newOrtho, _cam2);

        // ── Viewport rects ───────────────────────────────────────────────────
        Rect p1SplitRect, p2SplitRect;
        if (splitAxis == SplitAxis.Vertical)
        {
            p1SplitRect = LeftRect;
            p2SplitRect = RightRect;
        }
        else
        {
            p1SplitRect = TopRect;
            p2SplitRect = BotRect;
        }

        _cam.rect     = LerpRect(FullRect,  p1SplitRect, _splitBlend);
        _cam2.rect    = LerpRect(FullRect,  p2SplitRect, _splitBlend);
        _cam2.enabled = _splitBlend > 0.001f;
    }

    // ════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// Split when either player's look-ahead space (screen fraction in their
    /// facing direction) drops below splitLookAheadThreshold.
    ///
    /// Example: threshold = 0.3  →  split once the leading player has less
    /// than 30% of the shared viewport in front of them.
    ///
    /// This is viewport-space, so it automatically accounts for zoom level —
    /// the camera must already be zoomed out to keep both players on screen,
    /// so the fraction is measured within that zoomed view.
    /// </summary>
    private bool ShouldSplit(PlayerController p1, PlayerController p2)
    {
        // If the shared camera is pressed against either horizontal edge of the
        // bounding box, skip the split check entirely. The reduced look-ahead
        // space is caused by the level boundary, not by player separation.
        // If the players then genuinely move apart, their midpoint will pull
        // the camera away from the wall first, at which point the real
        // look-ahead pressure will be evaluated correctly.
        if (IsAgainstHorizontalBounds()) return false;
        
        // Where is each player in the current shared viewport? (0..1 range)
        float vp1x = _cam.WorldToViewportPoint(p1.transform.position).x;
        float vp2x = _cam.WorldToViewportPoint(p2.transform.position).x;

        // How much screen is ahead of each player in their facing direction?
        // FacingSign +1 = looking right → ahead space is (1 - viewportX)
        // FacingSign -1 = looking left  → ahead space is viewportX
        float ahead1 = p1.FacingSign >= 0f ? (1f - vp1x) : vp1x;
        float ahead2 = p2.FacingSign >= 0f ? (1f - vp2x) : vp2x;

        return ahead1 < _splitLookAheadThreshold || ahead2 < _splitLookAheadThreshold;
    }
    
    /// <summary>
    /// Returns true when the shared camera is sitting against either the left
    /// or right edge of cameraLimits. Detected by comparing the unclamped
    /// smoothed position to the clamped result — any horizontal difference
    /// means a boundary is active.
    /// </summary>
    private bool IsAgainstHorizontalBounds()
    {
        if (cameraLimits == null) return false;

        Vector3 clamped = ClampToBounds(_sharedPos, _cam.orthographicSize, _cam);
        return Mathf.Abs(clamped.x - _sharedPos.x) > 0.01f;
    }
    
    /// <summary>
    /// Returns the ortho size needed to frame both players,
    /// accounting for screen aspect ratio and padding.
    /// </summary>
    private float DesiredOrthoSize(Vector2 p1, Vector2 p2)
    {
        float halfDx = Mathf.Abs(p1.x - p2.x) * 0.5f + zoomPadding;
        float halfDy = Mathf.Abs(p1.y - p2.y) * 0.5f + zoomPadding;

        // orthoSize = world half-height; world half-width = orthoSize * aspect.
        // Solve for the smallest size that fits both axes.
        float sizeForWidth  = halfDx / _cam.aspect;
        float sizeForHeight = halfDy;

        return Mathf.Max(sizeForWidth, sizeForHeight, baseOrthoSize);
    }

    /// <summary>Smooth exponential decay follow (framerate-independent).</summary>
    private Vector3 ExponentialDecay(Vector3 current, Vector2 target)
    {
        float z  = transform.position.z;
        var   t3 = new Vector3(target.x, target.y, z);
        float k  = Mathf.Exp(-followSpeed * Time.deltaTime);
        return new Vector3(
            Mathf.Lerp(t3.x, current.x, k),
            Mathf.Lerp(t3.y, current.y, k),
            z);
    }

    /// <summary>
    /// Clamps a camera position so it doesn't reveal space outside cameraLimits.
    /// Accepts the Camera whose viewport rect and aspect should be used, so each
    /// split camera is clamped correctly with its own half-screen dimensions.
    /// Unity's camera.aspect already reflects the viewport rect, so no manual
    /// halving is needed — the correct world half-width comes out automatically.
    /// </summary>
    private Vector3 ClampToBounds(Vector3 pos, float orthoSize, Camera cam)
    {
        if (cameraLimits == null) return pos;

        Bounds b    = cameraLimits.bounds;
        float halfH = orthoSize;
        float halfW = orthoSize * cam.aspect;

        pos.x = Mathf.Clamp(pos.x, b.min.x + halfW, b.max.x - halfW);
        pos.y = Mathf.Clamp(pos.y, b.min.y + halfH, b.max.y - halfH);
        return pos;
    }

    private static Rect LerpRect(Rect a, Rect b, float t) =>
        new Rect(
            Mathf.Lerp(a.x,      b.x,      t),
            Mathf.Lerp(a.y,      b.y,      t),
            Mathf.Lerp(a.width,  b.width,  t),
            Mathf.Lerp(a.height, b.height, t));

    /// <summary>
    /// Teleports all tracked positions to the current target midpoint.
    /// Called on Start; also useful after a scene load or respawn.
    /// </summary>
    private void SnapToTargets()
    {
        var players = PlayerRegistry.Instance?.GetAllLiving();
        if (players == null || players.Count == 0) return;

        Vector2 mid = players[0].transform.position;
        if (players.Count >= 2)
            mid = ((Vector2)players[0].transform.position +
                   (Vector2)players[1].transform.position) * 0.5f;

        float   z   = transform.position.z;
        Vector3 pos = new Vector3(mid.x, mid.y, z);

        _sharedPos = _p1Pos = _p2Pos    = pos;
        _cam.transform.position          = pos;
        _cam2.transform.position         = pos;
    }

    // ════════════════════════════════════════════════════════
    // SECOND CAMERA SETUP
    // ════════════════════════════════════════════════════════

    private void CreateSecondCamera()
    {
        var go = new GameObject("CoopCamera_P2");
        go.transform.SetParent(null);   // world-space, not parented to main cam

        _cam2 = go.AddComponent<Camera>();
        _cam2.CopyFrom(_cam);
        _cam2.orthographic     = true;
        _cam2.orthographicSize = baseOrthoSize;
        _cam2.depth            = _cam.depth - 1;
        _cam2.rect             = splitAxis == SplitAxis.Vertical ? RightRect : BotRect;
        _cam2.enabled          = false;
    }

    // ════════════════════════════════════════════════════════
    // DIVIDER BAR  (screen-space via OnGUI)
    // ════════════════════════════════════════════════════════

    private void CreateDividerTexture()
    {
        _dividerTex = new Texture2D(1, 1);
        _dividerTex.SetPixel(0, 0, Color.white);  // tinted per-draw via GUI.color
        _dividerTex.Apply();
    }

    private void OnGUI()
    {
        if (_splitBlend < 0.001f || _dividerTex == null) return;

        GUI.color = new Color(
            dividerColor.r, dividerColor.g, dividerColor.b,
            dividerColor.a * _splitBlend);

        Rect barRect = splitAxis == SplitAxis.Vertical
            ? new Rect(Screen.width  * 0.5f - dividerPixels * 0.5f,
                       0, dividerPixels, Screen.height)
            : new Rect(0, Screen.height * 0.5f - dividerPixels * 0.5f,
                       Screen.width, dividerPixels);

        GUI.DrawTexture(barRect, _dividerTex);
        GUI.color = Color.white;
    }

    // ════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// Online co-op migration: lock this camera to one transform.
    /// Disables all split-screen logic.
    /// Call from your NetworkPlayer spawner after spawning the local player.
    /// </summary>
    public void SetSingleTarget(Transform target)
    {
        _singleTarget = target;
        _forceSingle  = true;
        _splitBlend   = 0f;
    }

    /// <summary>Revert to automatic multi-player tracking (local co-op).</summary>
    public void ClearSingleTarget()
    {
        _singleTarget = null;
        _forceSingle  = false;
    }

    /// <summary>
    /// Instantly teleport to current targets — skips lerp.
    /// Useful after a scene load or respawn.
    /// </summary>
    public void SnapNow() => SnapToTargets();

    // ════════════════════════════════════════════════════════
    // CLEANUP
    // ════════════════════════════════════════════════════════

    private void OnDestroy()
    {
        if (_cam2       != null) Destroy(_cam2.gameObject);
        if (_dividerTex != null) Destroy(_dividerTex);
    }

    // ════════════════════════════════════════════════════════
    // GIZMOS
    // ════════════════════════════════════════════════════════

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
}