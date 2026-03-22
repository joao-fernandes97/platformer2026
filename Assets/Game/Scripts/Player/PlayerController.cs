using UnityEngine;

/// <summary>
/// Core 2D platformer character controller.
///
/// ═══════════════════════════════════════════════════════════
///  ARCHITECTURE OVERVIEW
/// ═══════════════════════════════════════════════════════════
///  • State Machine  — each MoveState owns its own enter/tick/exit logic.
///                     Adding new states (WallSlide, Dash…) is additive only.
///
///  • Input is read from PlayerInputHandler, never from InputSystem directly.
///    Swap that component to go networked with zero changes here.
///
///  • Physics via Rigidbody2D. For NGO server-authority, move physics to
///    the server and send position snapshots; for client-authority, move
///    Rigidbody2D reads/writes behind an [IsOwner] guard.
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION CHECKLIST (Unity Netcode for GameObjects)
/// ═══════════════════════════════════════════════════════════
///  1. Inherit from NetworkBehaviour instead of MonoBehaviour.
///  2. Wrap Update/FixedUpdate bodies with:  if (!IsOwner) return;
///  3. Replace PlayerInputHandler with a networked input reader.
///  4. Expose _stamina.Current as a NetworkVariable<float> for UI sync.
///  5. Ledge ClimbTarget validation → ServerRpc + ClientRpc confirm.
///  6. Animator state → NetworkAnimator component.
///  7. Add a ClientNetworkTransform (or NetworkTransform) component.
/// ═══════════════════════════════════════════════════════════
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(PlayerInputHandler))]
[RequireComponent(typeof(LedgeDetector))]
public class PlayerController : MonoBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    [Header("Movement")]
    [Tooltip("Horizontal speed while walking.")]
    public float walkSpeed   = 6f;
    [Tooltip("Horizontal speed multiplier when sprinting.")]
    public float sprintMultiplier = 1.65f;
    [Tooltip("How quickly horizontal speed ramps up from zero.")]
    public float acceleration = 60f;
    [Tooltip("How quickly horizontal speed brakes to zero.")]
    public float deceleration = 80f;

    [Header("Jump")]
    [Tooltip("Initial upward velocity on jump.")]
    public float jumpForce = 14f;
    [Tooltip("Extra gravity applied when falling.")]
    public float fallGravityMultiplier = 2.5f;
    [Tooltip("Reduced gravity while holding jump at apex (floaty feel).")]
    public float lowJumpMultiplier = 2f;
    [Tooltip("Seconds after leaving a platform you can still jump.")]
    public float coyoteTime = 0.12f;
    [Tooltip("Seconds before landing a jump input is buffered.")]
    public float jumpBufferTime = 0.15f;
    [Tooltip("Maximum number of jumps (1 = single, 2 = double jump, etc).")]
    public int   maxJumps = 1;

    [Header("Ground Detection")]
    [Tooltip("Transform at the character's feet.")]
    public Transform groundCheck;
    [Tooltip("Radius of the overlap circle used to detect ground.")]
    public float groundCheckRadius = 0.15f;
    [Tooltip("Layer(s) that count as ground.")]
    public LayerMask groundLayers;

    [Header("Ledge Climb")]
    [Tooltip("Duration of the climb-up animation/tween in seconds.")]
    public float climbDuration = 0.25f;
    public float hangpointHorizontalOffset = 0.3f;
    public float hangpointVerticalOffset = 1.2f;

    [Header("Stamina")]
    public StaminaResource stamina;

    // ════════════════════════════════════════════════════════
    // MOVE STATES
    // ════════════════════════════════════════════════════════

    public enum MoveState
    {
        Grounded,
        Airborne,
        LedgeHang,
        LedgeClimb
    }

    private MoveState _state = MoveState.Airborne;

    // ════════════════════════════════════════════════════════
    // COMPONENTS
    // ════════════════════════════════════════════════════════

    private Rigidbody2D         _rb;
    private PlayerInputHandler  _input;
    private LedgeDetector       _ledgeDetector;
    private Animator            _animator;   // optional
    private HealthComponent     _health;

    // ════════════════════════════════════════════════════════
    // RUNTIME STATE
    // ════════════════════════════════════════════════════════

    private bool  _isGrounded;
    private float _facingSign    = 1f;   // +1 = right, -1 = left

    // Jump
    private int   _jumpsUsed;
    private float _coyoteTimer;
    private float _jumpBufferTimer;
    private bool  _jumpQueued;

    // Ledge
    private Vector3 _climbStart;
    private Vector3 _climbTarget;
    private float   _climbTimer;

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        _rb            = GetComponent<Rigidbody2D>();
        _input         = GetComponent<PlayerInputHandler>();
        _ledgeDetector = GetComponent<LedgeDetector>();
        _animator      = GetComponent<Animator>();   // null is fine

        _rb.freezeRotation = true;
        stamina.Initialize();

        _health = GetComponent<HealthComponent>();
        _health.OnDied += OnDied;
    }

    private void Start()
    {
        // Start is guaranteed to run after ALL Awake calls in the scene,
        // so PlayerRegistry.Instance is always valid here — even when both
        // players and the registry initialise in the same frame.
        PlayerRegistry.Instance?.Register(this);
    }

    private void OnEnable()
    {
        // Handles runtime re-enables (e.g. respawn after death).
        // At that point the registry is already initialised, so Instance
        // is never null. The Contains check inside Register prevents duplicates
        // if OnEnable fires before Start on first boot.
        PlayerRegistry.Instance?.Register(this);
    }
    
    private void OnDisable()
    {
        PlayerRegistry.Instance?.Deregister(this);
    }

    private void OnDestroy()
    {
        _health.OnDied -= OnDied;
    }

    private void Update()
    {
        // ── Buffer jump input ────────────────────────────────
        if (_input.JumpPressed)
        {
            _jumpBufferTimer = jumpBufferTime;
            _jumpQueued      = true;
        }
        if (_jumpBufferTimer > 0f)
            _jumpBufferTimer -= Time.deltaTime;
        else
            _jumpQueued = false;

        // ── Consume one-frame inputs ─────────────────────────
        _input.ConsumeFrameInputs();
    }

    private void FixedUpdate()
    {
        UpdateGroundState();
        _ledgeDetector.UpdateDetection(_facingSign);

        // Tick each state
        switch (_state)
        {
            case MoveState.Grounded:    TickGrounded();    break;
            case MoveState.Airborne:    TickAirborne();    break;
            case MoveState.LedgeHang:   TickLedgeHang();   break;
            case MoveState.LedgeClimb:  TickLedgeClimb();  break;
        }

        UpdateAnimator();
    }

    // ════════════════════════════════════════════════════════
    // GROUND CHECK
    // ════════════════════════════════════════════════════════

    private void UpdateGroundState()
    {
        bool wasGrounded = _isGrounded;
        _isGrounded = Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundLayers);

        if (_isGrounded && !wasGrounded)
            OnLanded();

        // Coyote time: keep the timer alive briefly after leaving ground
        if (_isGrounded)
            _coyoteTimer = coyoteTime;
        else if (_coyoteTimer > 0f)
            _coyoteTimer -= Time.fixedDeltaTime;
    }

    private void OnLanded()
    {
        _jumpsUsed = 0;
        if (_state == MoveState.Airborne)
            TransitionTo(MoveState.Grounded);
    }

    // ════════════════════════════════════════════════════════
    // STATE: GROUNDED
    // ════════════════════════════════════════════════════════

    private void TickGrounded()
    {
        if (!_isGrounded)
        {
            TransitionTo(MoveState.Airborne);
            return;
        }

        ApplyHorizontalMovement();

        if (CanJump())
            ExecuteJump();
    }

    // ════════════════════════════════════════════════════════
    // STATE: AIRBORNE
    // ════════════════════════════════════════════════════════

    private void TickAirborne()
    {
        if (_isGrounded)
        {
            TransitionTo(MoveState.Grounded);
            return;
        }

        // ── Ledge grab ──────────────────────────────────────
        if (_ledgeDetector.LedgeDetected && _rb.linearVelocityY <= 0f)
        {
            TransitionTo(MoveState.LedgeHang);
            return;
        }

        ApplyHorizontalMovement();

        // ── Variable jump gravity ────────────────────────────
        if (_rb.linearVelocityY < 0f)
        {
            _rb.linearVelocity += Vector2.up * Physics2D.gravity.y * (fallGravityMultiplier - 1f) * Time.fixedDeltaTime;
        }
        else if (_rb.linearVelocityY > 0f && !_input.JumpHeld)
        {
            _rb.linearVelocity += Vector2.up * Physics2D.gravity.y * (lowJumpMultiplier - 1f) * Time.fixedDeltaTime;
        }

        // ── Air jump (double jump etc.) ──────────────────────
        if (CanJump())
            ExecuteJump();
    }

    // ════════════════════════════════════════════════════════
    // STATE: LEDGE HANG
    // ════════════════════════════════════════════════════════

    private void TickLedgeHang()
    {
        // Freeze the rigidbody in place
        _rb.linearVelocity        = Vector2.zero;
        _rb.gravityScale    = 0f;

        // Snap position to hang point (hands at ledge)
        transform.position = new Vector3(
            _ledgeDetector.LedgePoint.x - _facingSign * hangpointHorizontalOffset,
            _ledgeDetector.LedgePoint.y - hangpointVerticalOffset,
            transform.position.z);

        // ── Let go downward ──────────────────────────────────
        if (_input.MoveInput.y < -0.5f)
        {
            _rb.gravityScale = 1f;
            TransitionTo(MoveState.Airborne);
            return;
        }

        // ── Climb up ─────────────────────────────────────────
        if (_jumpQueued || _input.ClimbPressed || _input.MoveInput.y > 0.5f)
        {
            _climbStart  = transform.position;
            _climbTarget = _ledgeDetector.ClimbTarget;
            _climbTimer  = 0f;
            _jumpQueued  = false;
            TransitionTo(MoveState.LedgeClimb);
        }
    }

    // ════════════════════════════════════════════════════════
    // STATE: LEDGE CLIMB
    // ════════════════════════════════════════════════════════

    private void TickLedgeClimb()
    {
        _climbTimer += Time.fixedDeltaTime;
        float t = Mathf.SmoothStep(0f, 1f, _climbTimer / climbDuration);

        transform.position = Vector3.Lerp(_climbStart, _climbTarget, t);
        _rb.linearVelocity       = Vector2.zero;
        _rb.gravityScale   = 0f;

        if (_climbTimer >= climbDuration)
        {
            transform.position = _climbTarget;
            _rb.gravityScale   = 1f;
            _jumpsUsed         = 0;
            TransitionTo(MoveState.Grounded);
        }
    }

    // ════════════════════════════════════════════════════════
    // SHARED MOVEMENT HELPERS
    // ════════════════════════════════════════════════════════

    private void ApplyHorizontalMovement()
    {
        float inputX = _input.MoveInput.x;

        // Update facing direction
        if (inputX != 0f)
        {
            _facingSign = Mathf.Sign(inputX);
            transform.localScale = new Vector3(_facingSign, 1f, 1f);
        }

        // Resolve sprint
        bool wantsSprint  = _input.SprintHeld && Mathf.Abs(inputX) > 0.1f;
        bool isSprinting  = stamina.Tick(wantsSprint, Time.fixedDeltaTime);
        float targetSpeed = inputX * walkSpeed * (isSprinting ? sprintMultiplier : 1f);

        // Smooth acceleration / deceleration
        float rate     = Mathf.Abs(inputX) > 0.01f ? acceleration : deceleration;
        float newVelX  = Mathf.MoveTowards(_rb.linearVelocityX, targetSpeed, rate * Time.fixedDeltaTime);

        _rb.linearVelocity = new Vector2(newVelX, _rb.linearVelocityY);
    }

    // ════════════════════════════════════════════════════════
    // JUMP
    // ════════════════════════════════════════════════════════

    private bool CanJump()
    {
        if (!_jumpQueued) return false;

        // Standard jump (ground or coyote)
        if (_coyoteTimer > 0f && _jumpsUsed == 0) return true;

        // Extra jumps (double jump etc.)
        if (_jumpsUsed < maxJumps) return true;

        return false;
    }

    private void ExecuteJump()
    {
        _rb.linearVelocity   = new Vector2(_rb.linearVelocityX, jumpForce);
        _jumpsUsed++;
        _jumpQueued    = false;
        _jumpBufferTimer = 0f;
        _coyoteTimer   = 0f;

        if (_state != MoveState.Airborne)
            TransitionTo(MoveState.Airborne);
    }

    // ════════════════════════════════════════════════════════
    // STATE MACHINE
    // ════════════════════════════════════════════════════════

    private void TransitionTo(MoveState next)
    {
        ExitState(_state);
        _state = next;
        EnterState(_state);
    }

    private void EnterState(MoveState s)
    {
        switch (s)
        {
            case MoveState.LedgeHang:
                _rb.bodyType        = RigidbodyType2D.Kinematic;

                _rb.gravityScale    = 0f;
                _rb.linearVelocity  = Vector2.zero;
                break;

            case MoveState.LedgeClimb:
                _rb.bodyType = RigidbodyType2D.Kinematic;   // hand off motion to Lerp during climb
                break;

            case MoveState.Grounded:
            case MoveState.Airborne:
                _rb.bodyType  = RigidbodyType2D.Dynamic;
                _rb.gravityScale = 1f;
                break;
        }
    }

    private void ExitState(MoveState s)
    {
        if (s == MoveState.LedgeClimb || s == MoveState.LedgeHang)
            _rb.bodyType = RigidbodyType2D.Dynamic;
    }

    // ════════════════════════════════════════════════════════
    // DEATH
    // ════════════════════════════════════════════════════════
    
    private void OnDied()
    {
        if(_animator != null)
            _animator.SetTrigger(AnimIsDead);

        _rb.linearVelocity = Vector2.zero;
        enabled = false;
    }
    
    // ════════════════════════════════════════════════════════
    // ANIMATOR BRIDGE  (optional — safe if no Animator attached)
    // Update with LedgeGrabbing, CLimbing animations later
    // ════════════════════════════════════════════════════════

    private static readonly int AnimSpeed     = Animator.StringToHash("AbsVelocityX");
    private static readonly int AnimGrounded  = Animator.StringToHash("IsGrounded");
    private static readonly int AnimVelY      = Animator.StringToHash("VelocityY");
    private static readonly int AnimLedge     = Animator.StringToHash("LedgeHang");
    private static readonly int AnimClimb     = Animator.StringToHash("LedgeClimb");
    private static readonly int AnimIsDead    = Animator.StringToHash("IsDead");
    //private static readonly int AnimSprint    = Animator.StringToHash("Sprinting");

    private void UpdateAnimator()
    {
        if (_animator == null) return;

        _animator.SetFloat(AnimSpeed,    Mathf.Abs(_rb.linearVelocityX));
        _animator.SetFloat(AnimVelY,     _rb.linearVelocityY);
        _animator.SetBool(AnimGrounded,  _isGrounded);
        _animator.SetBool(AnimLedge,     _state == MoveState.LedgeHang);
        _animator.SetBool(AnimClimb,     _state == MoveState.LedgeClimb);
        //_animator.SetBool(AnimSprint,    _input.SprintHeld && !stamina.IsExhausted);
    }

    // ════════════════════════════════════════════════════════
    // PUBLIC ACCESSORS  (for UI, abilities, external systems)
    // ════════════════════════════════════════════════════════

    public float StaminaNormalized => stamina.Normalized;
    public bool  IsGrounded        => _isGrounded;
    public bool  IsLedgeHanging    => _state == MoveState.LedgeHang;
    public MoveState CurrentState  => _state;
    public float FacingSign        => _facingSign;
}