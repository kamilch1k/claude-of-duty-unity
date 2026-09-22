using UnityEngine;

/// <summary>
/// First-person movement, driven entirely by <see cref="PlayerTuning"/>.
///
/// This is the part of the port that has to feel right before anything else
/// matters, so the numbers are upstream's rather than Unity-idiomatic ones: the
/// acceleration is deliberately low because that is what gives the movement its
/// weight, and the sprint speed is below what a CharacterController would
/// normally be given.
///
/// Not ported yet: slide, mantle, lean, prone, the spring-based bob and the
/// recoil springs. Those are listed in the README as remaining work rather than
/// half-implemented here.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMotor : MonoBehaviour
{
    [Header("Base")]
    public float baseFov = 90f;
    public float lookSensitivity = 0.12f;
    public Transform head;

    [Header("Input (legacy Input Manager names)")]
    public string forwardAxis = "Vertical";
    public string strafeAxis = "Horizontal";

    CharacterController _cc;
    Vector3 _velocity;
    float _yaw;
    float _pitch;

    bool _sprinting;
    bool _tacSprint;
    float _tacSprintTime;
    float _tacSprintLock;
    float _lastSprintTap = -10f;
    float _lastGrounded = -10f;
    float _jumpQueuedAt = -10f;
    float _lastJump = -10f;

    public bool IsGrounded => _cc.isGrounded;
    public bool Ads { get; set; }

    /// <summary>Horizontal speed, which is what every other system asks about.</summary>
    public float PlanarSpeed => new Vector2(_velocity.x, _velocity.z).magnitude;

    /// <summary>Upstream reports standing/crouching/prone; only two are ported.</summary>
    public PlayerTuning.Stance Stance { get; private set; } = PlayerTuning.Stand;

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        _yaw = transform.eulerAngles.y;
        ApplyStance(PlayerTuning.Stand, instant: true);
        Lock(true);
    }

    void OnDisable() => Lock(false);

    /// <summary>
    /// Capture the mouse. There is no menu yet, so Escape is the only way out —
    /// and the state has to be republished every frame, because the editor
    /// releases the cursor on its own when focus moves.
    /// </summary>
    static void Lock(bool on)
    {
        Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !on;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) Lock(false);
        if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked) Lock(true);
        TickLook();
        TickStance();
        TickMove();
        TickFov();
    }

    void TickLook()
    {
        _yaw += Input.GetAxisRaw("Mouse X") * lookSensitivity * 10f;
        // Pitch is inverted here rather than in the axis because upstream's
        // camera treats up as negative pitch and this keeps the sign explicit.
        _pitch -= Input.GetAxisRaw("Mouse Y") * lookSensitivity * 10f;
        _pitch = Mathf.Clamp(_pitch, -PlayerTuning.Camera.PitchLimit * Mathf.Rad2Deg,
                                     PlayerTuning.Camera.PitchLimit * Mathf.Rad2Deg);
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        if (head) head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    void TickStance()
    {
        bool wantsCrouch = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);
        var want = wantsCrouch ? PlayerTuning.Crouch : PlayerTuning.Stand;
        if (want.Name != Stance.Name) ApplyStance(want, instant: false);
    }

    /// <summary>
    /// Stance changes move the eye and the capsule together; the time constants
    /// are upstream's (63% in 62-72 ms), so a crouch is quick but not a snap.
    /// </summary>
    void ApplyStance(PlayerTuning.Stance stance, bool instant)
    {
        Stance = stance;
        float tau = stance.Name == "crouch" ? PlayerTuning.StanceTau.StandCrouch : PlayerTuning.StanceTau.CrouchStand;
        float k = instant ? 1f : 1f - Mathf.Exp(-Time.deltaTime / tau);
        _cc.height = Mathf.Lerp(_cc.height, stance.Height, k);
        _cc.center = new Vector3(0f, _cc.height * 0.5f, 0f);
        if (head)
        {
            var p = head.localPosition;
            head.localPosition = new Vector3(p.x, Mathf.Lerp(p.y, stance.Eye, k), p.z);
        }
    }

    void TickMove()
    {
        Vector2 input = new Vector2(Input.GetAxisRaw(strafeAxis), Input.GetAxisRaw(forwardAxis));
        input = Vector2.ClampMagnitude(input, 1f);

        bool wantSprint = Input.GetKey(KeyCode.LeftShift);
        TickSprint(wantSprint, input);

        // Directional scaling: slower sideways, slower still backwards.
        float scale = 1f;
        if (input.y < 0f) scale = PlayerTuning.Move.BackScale;
        else if (Mathf.Abs(input.x) > Mathf.Abs(input.y)) scale = PlayerTuning.Move.StrafeScale;
        if (Ads) scale *= PlayerTuning.Move.AdsScale;

        float top = Stance.Speed * scale;
        if (_tacSprint) top = PlayerTuning.Move.TacSprintSpeed * scale;
        else if (_sprinting) top = PlayerTuning.Move.SprintSpeed * scale;

        Vector3 wish = transform.right * input.x + transform.forward * input.y;
        Vector3 planar = new(_velocity.x, 0f, _velocity.z);
        Vector3 target = wish * top;

        bool grounded = _cc.isGrounded;
        if (grounded) _lastGrounded = Time.time;

        // Accel and decel are separate numbers, and stopDecel is the only fast
        // one: releasing every key should still plant you inside a step.
        float rate;
        if (input.sqrMagnitude > 0.0001f) rate = PlayerTuning.Move.GroundAccel;
        else rate = PlayerTuning.Move.StopDecel;
        if (!grounded) rate = PlayerTuning.Move.GroundAccel * PlayerTuning.Move.AirAccelScale;

        Vector3 delta = target - planar;
        float step = rate * Time.deltaTime;
        if (delta.magnitude > step) delta = delta.normalized * step;
        planar += delta;

        // Air control cannot add speed, and the ground decel tail is not applied
        // in the air: you keep what you left the ground with.
        if (!grounded && planar.magnitude > Mathf.Max(PlayerTuning.Move.AirSpeedCap, _velocity.magnitude))
        {
            planar = planar.normalized * Mathf.Max(PlayerTuning.Move.AirSpeedCap, new Vector2(_velocity.x, _velocity.z).magnitude);
        }

        _velocity.x = planar.x;
        _velocity.z = planar.z;

        // Jump: buffered input, coyote time, and a cooldown, in that order.
        if (Input.GetKeyDown(KeyCode.Space)) _jumpQueuedAt = Time.time;
        bool canJump = Time.time - _lastJump >= PlayerTuning.Move.JumpCooldown;
        bool buffered = Time.time - _jumpQueuedAt <= PlayerTuning.Move.JumpBuffer;
        bool coyote = Time.time - _lastGrounded <= PlayerTuning.Move.CoyoteTime;
        if (buffered && coyote && canJump && _velocity.y <= 0.01f)
        {
            _velocity.y = PlayerTuning.JumpSpeed;
            _jumpQueuedAt = -10f;
            _lastJump = Time.time;
            _lastGrounded = -10f;
        }

        if (grounded && _velocity.y < 0f) _velocity.y = -2f;
        _velocity.y = Mathf.Max(_velocity.y + PlayerTuning.Gravity * Time.deltaTime, -PlayerTuning.Move.TerminalSpeed);

        _cc.Move(_velocity * Time.deltaTime);
        if (_cc.isGrounded && _velocity.y < 0f) _velocity.y = 0f;
    }

    /// <summary>
    /// Tactical sprint is a double-tap of sprint, MWII style, and it only holds
    /// while the stick stays near dead ahead.
    /// </summary>
    void TickSprint(bool wantSprint, Vector2 input)
    {
        bool forward = input.y > PlayerTuning.Move.SprintForwardDot;

        if (wantSprint && !_sprinting && Time.time - _lastSprintTap <= PlayerTuning.Move.TacSprintTapWindow)
        {
            _tacSprint = true;
            _tacSprintTime = 0f;
            _tacSprintLock = Time.time;
        }
        if (wantSprint && !_sprinting) _lastSprintTap = Time.time;
        if (!wantSprint) _sprinting = false;

        if (wantSprint && forward && Time.time - _lastSprintTap >= PlayerTuning.Move.SprintStartDelay)
        {
            _sprinting = true;
        }
        if (!forward || !wantSprint || Ads) _tacSprint = false;

        if (_tacSprint)
        {
            _tacSprintTime += Time.deltaTime;
            if (_tacSprintTime >= PlayerTuning.Move.TacSprintMaxTime) _tacSprint = false;
        }
        _ = _tacSprintLock;
    }

    /// <summary>
    /// FOV follows the movement state, not the other way round: sprint opens it
    /// up, ADS pulls it in, and the time constants differ because ADS has to be
    /// crisp while a sprint transition can breathe.
    /// </summary>
    void TickFov()
    {
        if (!head) return;
        var cam = head.GetComponent<Camera>();
        if (!cam) return;

        float want = baseFov;
        float tau = PlayerTuning.Camera.Fov.MoveTau;
        if (Ads)
        {
            want = baseFov * 0.7f;
            tau = PlayerTuning.Camera.Fov.AdsTau;
        }
        else if (_tacSprint) want = baseFov * PlayerTuning.Camera.Fov.TacSprint;
        else if (_sprinting) want = baseFov * PlayerTuning.Camera.Fov.Sprint;
        else if (!_cc.isGrounded) want = baseFov * PlayerTuning.Camera.Fov.Air;

        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, want, 1f - Mathf.Exp(-Time.deltaTime / tau));
    }
}
