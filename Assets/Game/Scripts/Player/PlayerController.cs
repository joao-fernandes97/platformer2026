using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Core 2D platformer character controller.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(PlayerInputHandler))]
[RequireComponent(typeof(LedgeDetector))]
public class PlayerController : NetworkBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    [Header("Movement")]
    public float walkSpeed   = 6f;
    public float sprintMultiplier = 1.65f;
    public float acceleration = 60f;
    public float deceleration = 80f;

    [Header("Jump")]
    public float jumpForce = 14f;
    public float fallGravityMultiplier = 2.5f;
    public float lowJumpMultiplier = 2f;
    public float coyoteTime = 0.12f;
    public float jumpBufferTime = 0.15f;
    public int   maxJumps = 1;

    [Header("Ground Detection")]
    public Transform groundCheck;
    public float groundCheckRadius = 0.15f;
    public LayerMask groundLayers;

    [Header("Ledge Climb")]
    public float climbDuration = 0.25f;
    public float hangpointHorizontalOffset = 0.3f;
    public float hangpointVerticalOffset = 1.2f;

    [Header("Stamina")]
    public StaminaResource stamina;

    //NetworkVariables
    public readonly NetworkVariable<float> StaminaNetworked = new(
    100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public readonly NetworkVariable<float> FacingSignNetworked = new(
    1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    
    // Move STates
    public enum MoveState
    {
        Grounded,
        Airborne,
        LedgeHang,
        LedgeClimb
    }

    private MoveState _state = MoveState.Airborne;

    // Components
    private Rigidbody2D         _rb;
    private PlayerInputHandler  _input;
    private LedgeDetector       _ledgeDetector;
    private Animator            _animator;   // optional
    private HealthComponent     _health;

    // Runtime State
    private bool  _isGrounded;
    private float _facingSign    = 1f;   // +1 = right, -1 = left

    // Jump
    private int   _jumpsUsed;
    private float _coyoteTimer;
    private float _jumpBufferTimer;
    private bool  _jumpQueued;

    // Ledge
    private Vector3 _climbStart;
    private Vector3 _confirmedLedgePoint; //confirmed by RPC
    private Vector3 _climbTarget;
    private float   _climbTimer;

    // Moving platforms
    private PlatformMover _currentPlatform;
    private Vector2 _lastPlatformPosition;

#region Lifecycle

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
        _health.OnRevived += OnRevived;
    }

    public override void OnNetworkSpawn()
    {
        // Start is guaranteed to run after ALL Awake calls in the scene,
        // so PlayerRegistry.Instance is always valid here — even when both
        // players and the registry initialise in the same frame.
        if (IsServer)
        {
            PlayerRegistry.Instance?.Register(this);
            NetworkCameraController.Instance.SetTarget(transform); //follow latest player on Server
        }        

        if(IsOwner)
        {
            if(NetworkCameraController.Instance == null)
            {
                Debug.Log("CameraController null");
            } 
            else
            {
                NetworkCameraController.Instance.SetTarget(transform);

                var bar = GetComponent<StaminaBar>();
                if (bar != null)
                {
                    Debug.Log("[PlayerController] Bind Stamina Bar");
                    bar.Bind(this);
                }
                    
            }
        }
        
        if(!IsOwner)
        {
            FacingSignNetworked.OnValueChanged += OnFacingChanged;
            Debug.Log("[PlayerController] Hide non owner stam bar");
            var bar = GetComponent<StaminaBar>();
                if (bar != null) bar.staminaBar.SetActive(false);
        } 
    }

    public override void OnNetworkDespawn()
    {
        FacingSignNetworked.OnValueChanged -= OnFacingChanged;
    }

    private void OnEnable()
    {
        // Handles runtime re-enables (e.g. respawn after death).
        // At that point the registry is already initialised, so Instance
        // is never null. The Contains check inside Register prevents duplicates
        // if OnEnable fires before Start on first boot.
        if (!IsServer) return;
        PlayerRegistry.Instance?.Register(this);
    }
    
    private void OnDisable()
    {
        if(!IsSpawned) return;
        PlayerRegistry.Instance?.Deregister(this);
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        _health.OnDied -= OnDied;
        _health.OnRevived -= OnRevived;
    }

    private void Update()
    {
        if(!IsOwner) return;
        
        // Buffer jump input
        if (_input.JumpPressed)
        {
            _jumpBufferTimer = jumpBufferTime;
            _jumpQueued      = true;
        }
        if (_jumpBufferTimer > 0f)
            _jumpBufferTimer -= Time.deltaTime;
        else
            _jumpQueued = false;

        // Consume one-frame inputs
        _input.ConsumeFrameInputs();
             
    }

    private void FixedUpdate()
    {
        if(!IsOwner) return;
        
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
#endregion

#region Ground Check
    
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

        TrackPlatform();
    }

    private void OnLanded()
    {
        _jumpsUsed = 0;
        if (_state == MoveState.Airborne)
            TransitionTo(MoveState.Grounded);
    }

    private void TrackPlatform()
    {
        if (!_isGrounded)
        {
            _currentPlatform = null;
            return;
        }

        // Cast downward from ground check to find whatever we're standing on
        RaycastHit2D hit = Physics2D.Raycast(
            groundCheck.position,
            Vector2.down,
            groundCheckRadius + 0.1f,
            groundLayers);

        if (hit.collider == null)
        {
            _currentPlatform = null;
            return;
        }

        PlatformMover platform = hit.collider.GetComponentInParent<PlatformMover>();

        if (platform == null)
        {
            _currentPlatform = null;
            return;
        }

        if (platform != _currentPlatform)
        {
            // Just stepped onto a new platform, record its position
            _currentPlatform = platform;
            _lastPlatformPosition = platform.transform.position;
        }
        else
        {
            // Already on this platform, apply delta
            Vector2 platformDelta = (Vector2)platform.transform.position - _lastPlatformPosition;
            if (platformDelta != Vector2.zero)
                _rb.position += platformDelta;

            _lastPlatformPosition = platform.transform.position;
        }
    }
#endregion
    
#region States
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

    private void TickAirborne()
    {
        if (_isGrounded)
        {
            TransitionTo(MoveState.Grounded);
            return;
        }

        // Ledge grab, with Network confirmation guard
        if (_ledgeDetector.LedgeDetected && _rb.linearVelocityY <= 0f)
        {
            RequestLedgeGrabServerRpc(_ledgeDetector.LedgePoint, _ledgeDetector.ClimbTarget, _facingSign);
            //TransitionTo(MoveState.LedgeHang);
            return;
        }

        ApplyHorizontalMovement();

        // Variable jump gravity
        if (_rb.linearVelocityY < 0f)
        {
            _rb.linearVelocity += Vector2.up * Physics2D.gravity.y * (fallGravityMultiplier - 1f) * Time.fixedDeltaTime;
        }
        else if (_rb.linearVelocityY > 0f && !_input.JumpHeld)
        {
            _rb.linearVelocity += Vector2.up * Physics2D.gravity.y * (lowJumpMultiplier - 1f) * Time.fixedDeltaTime;
        }

        // Air jump (double jump etc.)
        if (CanJump())
            ExecuteJump();
    }

    private void TickLedgeHang()
    {
        // Freeze the rigidbody in place
        _rb.linearVelocity        = Vector2.zero;
        _rb.gravityScale    = 0f;

        // Snap position to hang point (hands at ledge)
        transform.position = new Vector3(
            _confirmedLedgePoint.x - _facingSign * hangpointHorizontalOffset,
            _confirmedLedgePoint.y - hangpointVerticalOffset,
            transform.position.z);

        // Let go downward
        if (_input.MoveInput.y < -0.5f)
        {
            _rb.gravityScale = 1f;
            TransitionTo(MoveState.Airborne);
            return;
        }

        // Climb up
        if (_jumpQueued || _input.ClimbPressed || _input.MoveInput.y > 0.5f)
        {
            _climbStart  = transform.position;
            _climbTimer  = 0f;
            _jumpQueued  = false;
            TransitionTo(MoveState.LedgeClimb);
        }
    }

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
#endregion
    
#region Shared Movement Helpers
    
    private void ApplyHorizontalMovement()
    {
        float inputX = _input.MoveInput.x;

        // Update facing direction
        if (inputX != 0f)
        {
            _facingSign = Mathf.Sign(inputX);
            FacingSignNetworked.Value = _facingSign;
            transform.localScale = new Vector3(_facingSign, 1f, 1f);
        }

        // Resolve sprint
        bool wantsSprint  = _input.SprintHeld && Mathf.Abs(inputX) > 0.1f;
        bool isSprinting  = stamina.Tick(wantsSprint, Time.fixedDeltaTime);
        StaminaNetworked.Value = stamina.Current; //sync in network
        float targetSpeed = inputX * walkSpeed * (isSprinting ? sprintMultiplier : 1f);

        // Smooth acceleration / deceleration
        float rate     = Mathf.Abs(inputX) > 0.01f ? acceleration : deceleration;
        float newVelX  = Mathf.MoveTowards(_rb.linearVelocityX, targetSpeed, rate * Time.fixedDeltaTime);

        _rb.linearVelocity = new Vector2(newVelX, _rb.linearVelocityY);
    }

    private void OnFacingChanged(float prev, float current)
    {
        _facingSign = current;
        transform.localScale = new Vector3(current, 1f, 1f);
    }

    // Jump
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

    // State Machine
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
#endregion

    // Death
    private void OnDied()
    {
        if(_animator != null)
            _animator.SetTrigger(AnimIsDead);

        _rb.linearVelocity = Vector2.zero;
        enabled = false;
    }

    private void OnRevived()
    {
        // Reset the Animator back to its default state so the death
        // pose/hide doesn't persist. Rebind() resets all parameters and
        // replays the default state from time 0.
        if (_animator != null)
        {
            _animator.ResetTrigger(AnimIsDead);
            _animator.Rebind();
            _animator.Update(0f);
        }

        // Ensure the sprite is fully opaque in case a fade-out was in progress.
        if (TryGetComponent<SpriteRenderer>(out var sr))
        {
            var c = sr.color;
            c.a = 1f;
            sr.color = c;
        }

        // Reset physics to a clean dynamic state. CheckpointManager positions
        // the transform before calling pc.enabled = true, so we only need
        // to clear velocity and restore the body type here.
        _rb.linearVelocity = Vector2.zero;
        _rb.bodyType       = RigidbodyType2D.Dynamic;
        _rb.gravityScale   = 1f;

        // Drop back into Airborne — UpdateGroundState() will settle it to
        // Grounded on the very next FixedUpdate if the player is on the floor.
        _state = MoveState.Airborne;
    }
    
    // Animator Bridge

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

    // Public accessors

    public float StaminaNormalized => stamina.Normalized;
    public bool  IsGrounded        => _isGrounded;
    public bool  IsLedgeHanging    => _state == MoveState.LedgeHang;
    public MoveState CurrentState  => _state;
    public float FacingSign        => _facingSign;

    
#region Network RPCS
    
    [ServerRpc]
    private void RequestLedgeGrabServerRpc(Vector3 ledgePoint, Vector3 climbTarget, float facingSign)
    {
        Vector2 eyePos  = (Vector2)transform.position + Vector2.up * _ledgeDetector.wallRayHeight;
        Vector2 dir     = Vector2.right * facingSign;
        bool    wallHit = Physics2D.Raycast(eyePos, dir,
                            _ledgeDetector.rayLength, _ledgeDetector.geometryLayers);

        Vector2 ledgeEye = (Vector2)transform.position + Vector2.up * _ledgeDetector.ledgeRayHeight;
        bool    ledgeHit = Physics2D.Raycast(ledgeEye, dir,
                            _ledgeDetector.rayLength, _ledgeDetector.geometryLayers);

        if (wallHit && !ledgeHit)
            ConfirmLedgeGrabClientRpc(ledgePoint, climbTarget);
    }

    [ClientRpc]
    private void ConfirmLedgeGrabClientRpc(Vector3 ledgePoint, Vector3 climbTarget)
    {
        if (!IsOwner) return;

        // Cache the confirmed points so TickLedgeHang and TickLedgeClimb
        // use the server-validated values rather than re-reading from the detector
        _confirmedLedgePoint  = ledgePoint;
        _climbTarget          = climbTarget;
        TransitionTo(MoveState.LedgeHang);
    }
#endregion
}