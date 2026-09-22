using UnityEngine;

/// <summary>
/// Hitscan firing, using the rifle's own numbers from WEAPON_DEFS.
///
/// Upstream simulates projectiles with travel time and drop; this does not yet.
/// What it does carry over is everything that decides whether firing feels
/// right: the rate of fire, the magazine, the reload split between tactical and
/// empty, the per-shot spread, and the recoil pattern that climbs and drifts
/// rather than jumping.
/// </summary>
public class WeaponSystem : MonoBehaviour
{
    [Header("From WEAPON_DEFS (rifle)")]
    public float rpm = 800f;
    public int magSize = 30;
    public float muzzleVelocity = 880f;
    public float damage = 33f;
    public float reloadTac = 2.1f;
    public float reloadEmpty = 2.9f;
    public float adsTime = 0.22f;

    [Header("Recoil")]
    public float climb = 0.0085f;
    public float drift = 0.55f;
    public float recoilFreq = 9.5f;
    public float recoilDamping = 0.5f;

    [Header("References")]
    public Camera viewCamera;
    public Transform recoilPivot;
    public Transform muzzle;
    public Light muzzleFlash;
    public Hud hud;

    [Header("Audio (baked from the game's own synth)")]
    public AudioClip fireClip;
    public AudioClip reloadClip;
    public AudioClip hitClip;

    public int Ammo { get; private set; }
    public bool Reloading { get; private set; }
    public bool Ads { get; private set; }
    public int ShotsFired { get; private set; }
    public int Hits { get; private set; }

    /// <summary>
    /// Rounds in flight. Upstream simulates with travel time and drop rather than
    /// hitscanning, and at 880 m/s over the street's 60 m that is a tenth of a
    /// second to impact with ~11 cm of drop — small, but it is the difference
    /// between a rifle that hits where it is pointed and one that feels like it
    /// reaches out and touches things.
    /// </summary>
    struct Shot
    {
        public Vector3 position;
        public Vector3 velocity;
        public float born;
        public bool live;
        public int bounces;
    }

    const int MaxShots = 64;
    readonly Shot[] _shots = new Shot[MaxShots];
    public float gravity = -PlayerTuning.Gravity;

    void StepShots() => StepShots(Time.deltaTime);

    /// <summary>
    /// Advance the rounds in flight. Public and time-stepped so a burst can be
    /// verified without play mode — the travel time is part of what is checked.
    /// </summary>
    public void StepFor(float seconds, float step = 0.004f)
    {
        for (float t = 0f; t < seconds; t += step) StepShots(step);
    }

    void StepShots(float dt)
    {
        for (int i = 0; i < MaxShots; i++)
        {
            if (!_shots[i].live) continue;
            var shot = _shots[i];
            var next = shot.position + shot.velocity * dt;
            shot.velocity += Vector3.up * gravity * dt;

            if (Physics.Linecast(shot.position, next, out var hit, ~0, QueryTriggerInteraction.Ignore))
            {
                Fx.Tracer(shot.position, hit.point);
                ResolveHit(hit, shot.velocity);
                shot.live = false;
            }
            else
            {
                Fx.Tracer(shot.position, next);
                shot.position = next;
            }
            if (Time.time - shot.born > 2f) shot.live = false;
            _shots[i] = shot;
        }
    }

    void SpawnShot(Vector3 origin, Vector3 direction)
    {
        for (int i = 0; i < MaxShots; i++)
        {
            if (_shots[i].live) continue;
            _shots[i] = new Shot
            {
                position = origin,
                velocity = direction * muzzleVelocity,
                born = Time.time,
                live = true,
            };
            Fx.Flash(origin, direction);
            return;
        }
    }

    void ResolveHit(RaycastHit hit, Vector3 incoming)
    {
        _ = incoming;
        var enemy = hit.collider.GetComponentInParent<Enemy>();
        var target = hit.collider.GetComponentInParent<Target>();
        if (enemy != null || target != null)
        {
            if (enemy != null) enemy.TakeDamage(damage, hit.point, hit.normal * -1f);
            else target.TakeDamage(damage, hit.point, hit.normal * -1f);
            Hits++;
            if (hitClip) Sfx.Play(hitClip, hit.point, 0.8f);
            if (hud) hud.FlashHit();
        }
        Fx.Impact(hit.point, hit.normal, target == null && enemy == null);
    }

    float _nextShot;
    float _reloadEnds;
    float _adsBlend;
    float _spread;      // radians, grows per shot and settles
    Vector2 _recoil;    // spring state, radians
    Vector2 _recoilVel;
    Vector2 _residual;

    void Awake()
    {
        Prime();
    }

    /// <summary>
    /// Ready to fire. Public because a MonoBehaviour added in edit mode never
    /// gets Awake, and the scene builder verifies firing without play mode.
    /// </summary>
    public void Prime()
    {
        Ammo = magSize;
        Reloading = false;
        if (muzzleFlash) muzzleFlash.enabled = false;
    }

    void UpdateWeapon()
    {
        Ads = Input.GetMouseButton(1);
        _adsBlend = Mathf.Lerp(_adsBlend, Ads ? 1f : 0f, 1f - Mathf.Exp(-Time.deltaTime / adsTime));

        if (Reloading && Time.time >= _reloadEnds)
        {
            Reloading = false;
            Ammo = magSize;
        }

        if (Input.GetKeyDown(KeyCode.R) && !Reloading && Ammo < magSize)
        {
            Reloading = true;
            _reloadEnds = Time.time + (Ammo == 0 ? reloadEmpty : reloadTac);
            if (reloadClip) Sfx.Play(reloadClip, transform.position, 0.6f);
        }

        if (Input.GetMouseButton(0) && Time.time >= _nextShot && !Reloading)
        {
            if (Ammo > 0) Fire();
            else if (!Reloading) { Reloading = true; _reloadEnds = Time.time + reloadEmpty; }
        }

        TickRecoil();
        if (muzzleFlash && muzzleFlash.enabled && Time.time > _nextShot - 0.03f) muzzleFlash.enabled = false;
    }

    /// <summary>Fires one round. Public so the scene builder can prove it works.</summary>
    public bool Fire() => Fire(Time.time);

    /// <summary>
    /// Fires one round as of `now`. The clock is a parameter rather than read
    /// from Time inside, so a burst can be verified in one editor frame — the
    /// rate of fire is part of what is being checked.
    /// </summary>
    public bool Fire(float now)
    {
        if (Ammo <= 0 || Reloading) return false;
        _nextShot = now + 60f / rpm;
        Ammo--;
        ShotsFired++;

        if (muzzleFlash)
        {
            muzzleFlash.enabled = true;
            muzzleFlash.intensity = 6f;
        }
        if (fireClip) Sfx.Play(fireClip, transform.position, 0.9f);

        // Recoil is a spring, not a step: the pattern climbs, then drifts, and
        // the residual share is what keeps the sight picture from snapping back.
        _recoilVel.y += climb * Mathf.Lerp(1.6f, 1f, Mathf.Clamp01(ShotsFired / 6f));
        _recoilVel.x += Random.Range(-1f, 1f) * climb * drift * 0.35f;
        _spread = Mathf.Min(_spread + 0.0022f, 0.02f);

        var cam = viewCamera ? viewCamera : Camera.main;
        if (cam == null) return false;

        // Spread is applied in the camera's own basis, so it follows the aim.
        Vector2 offset = Random.insideUnitCircle * _spread;
        Vector3 dir = cam.transform.rotation * new Vector3(offset.x, offset.y, 1f).normalized;

        // From the muzzle when there is one, so the tracer starts where the
        // flash is; the direction is still the camera's.
        Vector3 origin = muzzle ? muzzle.position : cam.transform.position;
        SpawnShot(origin, dir);
        return true;
    }

    void Update()
    {
        UpdateWeapon();
        StepShots();
    }

    void TickRecoil()
    {
        // Damped spring, the same shape as CAMERA.recoil upstream.
        float k = recoilFreq * recoilFreq;
        _recoilVel -= _recoil * k * Time.deltaTime;
        _recoilVel *= Mathf.Exp(-recoilDamping * recoilFreq * Time.deltaTime);
        _recoil += _recoilVel * Time.deltaTime;

        // Residual: a fraction of each shot stays, so the view drifts off target
        // during a burst and has to be pulled back.
        _residual = Vector2.Lerp(_residual, Vector2.zero, 1f - Mathf.Exp(-Time.deltaTime / 0.28f));

        var cam = viewCamera ? viewCamera : Camera.main;
        if (cam == null) return;
        float pitch = (_recoil.y + _residual.y) * Mathf.Rad2Deg;
        float yaw = (_recoil.x + _residual.x) * Mathf.Rad2Deg;
        // Recoil is written to its own pivot, never to the head: the head holds
        // the player's aim, and a spring fighting it would make the gun feel
        // like it was dragging the mouse.
        if (recoilPivot) recoilPivot.localRotation = Quaternion.Euler(-pitch, yaw, 0f);
        _spread = Mathf.Lerp(_spread, 0f, 1f - Mathf.Exp(-Time.deltaTime / 0.25f));
    }

    /// <summary>Used by the scene builder to fire a burst without play mode.</summary>
    public void SetMuzzle(Transform t)
    {
        muzzle = t;
    }
}

/// <summary>
/// Something that can be shot. Deliberately minimal: it exists so that firing
/// has a consequence to verify, and so the port has the hook that damage,
/// death and the hit marker will hang off.
/// </summary>
public class Target : MonoBehaviour
{
    public float health = 100f;
    public float damagePerHit = 33f;

    public bool Dead { get; private set; }
    public float LastHitAt { get; private set; }
    public Vector3 LastHitPoint { get; private set; }

    public void TakeDamage(float amount, Vector3 point, Vector3 direction)
    {
        if (Dead) return;
        health -= amount;
        LastHitAt = Time.time;
        LastHitPoint = point;
        if (health <= 0f) Die(direction);
    }

    void Die(Vector3 direction)
    {
        Dead = true;
        // Fall over rather than vanish: upstream ragdolls, and a target that
        // simply disappears makes it impossible to see that a hit landed.
        var dir = new Vector3(direction.x, 0f, direction.z).normalized;
        transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(90f, 0f, 0f);
        transform.position += Vector3.down * 0.35f;
        foreach (var r in GetComponentsInChildren<Renderer>()) r.sharedMaterial = DeadMaterial;
    }

    static Material _dead;
    static Material DeadMaterial
    {
        get
        {
            if (_dead == null)
            {
                _dead = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                _dead.color = new Color(0.12f, 0.10f, 0.10f);
            }
            return _dead;
        }
    }
}
