using UnityEngine;

/// <summary>
/// The effects the port has so far: tracers, a muzzle flash quad and impact
/// decals. Upstream these are GPU particles, decal buffers and a pooled tracer
/// system; this is the cheap version, and it exists so that firing is legible.
/// </summary>
public static class Fx
{
    static Material _tracerMat;
    static Material _decalMat;
    static Material _sparkMat;
    static Transform _root;

    static void Ensure()
    {
        if (_root != null) return;
        var go = new GameObject("FX");
        _root = go.transform;

        var lit = Shader.Find("Universal Render Pipeline/Unlit");
        _tracerMat = new Material(lit) { color = new Color(1f, 0.85f, 0.55f, 0.85f) };
        _sparkMat = new Material(lit) { color = new Color(1f, 0.75f, 0.4f, 0.9f) };
        _decalMat = new Material(lit) { color = new Color(0.04f, 0.035f, 0.03f, 1f) };
    }

    /// <summary>Destroy now in the editor: Object.Destroy is a no-op there.</summary>
    static void Kill(GameObject go, float delay)
    {
        if (Application.isPlaying) Object.Destroy(go, delay);
        else Object.DestroyImmediate(go);
    }

    /// <summary>A tracer from muzzle to impact, alive for two frames.</summary>
    public static void Tracer(Vector3 from, Vector3 to)
    {
        Ensure();
        var go = new GameObject("tracer");
        go.transform.SetParent(_root, false);
        var line = go.AddComponent<LineRenderer>();
        line.material = _tracerMat;
        line.startWidth = 0.012f;
        line.endWidth = 0.006f;
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.SetPosition(0, from);
        line.SetPosition(1, to);
        Kill(go, 0.035f);
    }

    public static void Flash(Vector3 at, Vector3 direction)
    {
        Ensure();
        var go = new GameObject("flash");
        go.transform.SetParent(_root, false);
        go.transform.position = at;
        go.transform.rotation = Quaternion.LookRotation(direction);
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(go.transform, false);
        quad.transform.localScale = new Vector3(0.12f, 0.12f, 1f);
        Object.Destroy(quad.GetComponent<Collider>());
        quad.GetComponent<MeshRenderer>().sharedMaterial = _sparkMat;
        Kill(go, 0.03f);
    }

    /// <summary>An impact: a spark flash, and a decal when it lands on geometry.</summary>
    public static void Impact(Vector3 point, Vector3 normal, bool decal)
    {
        Ensure();
        var go = new GameObject("impact");
        go.transform.SetParent(_root, false);
        go.transform.position = point + normal * 0.01f;
        go.transform.rotation = Quaternion.LookRotation(-normal);
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(go.transform, false);
        quad.transform.localScale = decal ? Vector3.one * 0.09f : Vector3.one * 0.05f;
        Object.Destroy(quad.GetComponent<Collider>());
        quad.GetComponent<MeshRenderer>().sharedMaterial = decal ? _decalMat : _sparkMat;
        Kill(go, decal ? 25f : 0.06f);
    }
}

/// <summary>
/// An enemy that hunts the player, shoots back, and dies.
///
/// It is a mannequin in bind pose, not an animated soldier: the bake carries the
/// geometry, not the rig. Everything a firefight needs is here though — sight,
/// approach, a firing cadence, damage in both directions, and a death you can
/// see. Navmesh pathing, cover and the animator are the remaining pass.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class Enemy : MonoBehaviour
{
    [Header("Tuning")]
    public float sightRange = 45f;
    public float attackRange = 26f;
    public float moveSpeed = 2.4f;
    public float fireInterval = 0.55f;
    public float damagePerShot = 11f;
    public float spreadDegrees = 3.2f;
    public float health = 100f;

    [Header("References")]
    public Transform eye;
    public Transform muzzle;
    public Light muzzleFlash;
    public AudioClip fireClip;
    public AudioClip deathClip;

    CharacterController _cc;
    Transform _player;
    float _nextShot;
    float _vertical;
    bool _dead;
    float _deadAt;
    float _alerted;

    public bool Dead => _dead;

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        if (muzzleFlash) muzzleFlash.enabled = false;
    }

    void Start()
    {
        var player = GameObject.Find("Player");
        if (player) _player = player.transform;
    }

    void Update()
    {
        if (_dead)
        {
            // Settle into the ground, then stop costing anything.
            if (Time.time - _deadAt > 6f) enabled = false;
            return;
        }
        if (_player == null) return;

        var self = eye ? eye.position : transform.position + Vector3.up * 1.6f;
        var target = _player.position + Vector3.up * PlayerTuning.Stand.Eye;
        var toPlayer = target - self;
        float distance = toPlayer.magnitude;
        bool canSee = distance <= sightRange && HasLineOfSight(self, target);

        if (canSee) _alerted = Time.time - _alerted < 0.01f ? _alerted : Time.time;
        bool aware = canSee || Time.time - _lastSeen < 3f;
        if (canSee) _lastSeen = Time.time;

        if (!aware)
        {
            // Idle: hold position, face where it was spawned.
            ApplyGravity();
            return;
        }

        // Face the player on the horizontal plane only, so it does not tip over
        // trying to look up at a player standing on a roof.
        var flat = new Vector3(toPlayer.x, 0f, toPlayer.z);
        if (flat.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(flat), 1f - Mathf.Exp(-6f * Time.deltaTime));

        // Close the distance, then hold and fire.
        if (distance > attackRange * 0.75f)
        {
            var step = flat.normalized * moveSpeed;
            _vertical += PlayerTuning.Gravity * Time.deltaTime;
            if (_cc.isGrounded && _vertical < 0f) _vertical = -2f;
            _cc.Move((step + Vector3.up * _vertical) * Time.deltaTime);
        }
        else
        {
            ApplyGravity();
        }

        if (canSee && distance <= attackRange) TryFire(self, target);
    }

    float _lastSeen = -10f;

    void ApplyGravity()
    {
        _vertical += PlayerTuning.Gravity * Time.deltaTime;
        if (_cc.isGrounded && _vertical < 0f) _vertical = -2f;
        _cc.Move(Vector3.up * _vertical * Time.deltaTime);
    }

    bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        var dir = to - from;
        if (Physics.Raycast(from, dir.normalized, out var hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore))
            return hit.collider.transform.IsChildOf(_player);
        return true;
    }

    void TryFire(Vector3 from, Vector3 to)
    {
        if (Time.time < _nextShot) return;
        _nextShot = Time.time + fireInterval;

        if (muzzleFlash)
        {
            muzzleFlash.enabled = true;
            Invoke(nameof(KillFlash), 0.04f);
        }
        if (fireClip) AudioSource.PlayClipAtPoint(fireClip, from, 0.7f);

        // Spread is applied to the direction, then a ray decides the hit: the
        // player has to be able to be missed.
        var dir = (to - from).normalized;
        dir = Quaternion.Euler(
            Random.Range(-spreadDegrees, spreadDegrees),
            Random.Range(-spreadDegrees, spreadDegrees),
            0f) * dir;

        if (Physics.Raycast(from, dir, out var hit, sightRange * 2f, ~0, QueryTriggerInteraction.Ignore))
        {
            var playerHealth = hit.collider.GetComponentInParent<PlayerHealth>();
            if (playerHealth != null) playerHealth.TakeDamage(damagePerShot, transform.position);
        }
    }

    void KillFlash()
    {
        if (muzzleFlash) muzzleFlash.enabled = false;
    }

    public void TakeDamage(float amount, Vector3 point, Vector3 direction)
    {
        _ = point;
        if (_dead) return;
        health -= amount;
        _lastSeen = Time.time;   // being shot at counts as being seen
        if (health <= 0f) Die(direction);
    }

    void Die(Vector3 direction)
    {
        _dead = true;
        _deadAt = Time.time;
        if (_cc) _cc.enabled = false;
        foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
        var flat = new Vector3(direction.x, 0f, direction.z);
        if (flat.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(flat);
        // Tip over: not a ragdoll, but unmistakably dead.
        transform.rotation *= Quaternion.Euler(88f, 0f, Random.Range(-12f, 12f));
        transform.position += Vector3.down * 0.45f;
        if (deathClip) AudioSource.PlayClipAtPoint(deathClip, transform.position, 0.8f);
    }
}

/// <summary>
/// Player health with the regen curve from PlayerTuning.Health: nothing for
/// ~4.6 seconds, then a ramp back to full. Dying respawns rather than ending
/// the session, because there is no menu yet.
/// </summary>
public class PlayerHealth : MonoBehaviour
{
    public float Health { get; private set; } = PlayerTuning.Health.Max;
    public float LastHitAt { get; private set; } = -100f;
    public int Deaths { get; private set; }
    public AudioClip hurtClip;
    public AudioClip deathClip;

    Vector3 _spawn;
    float _regenStart;

    void Start()
    {
        _spawn = transform.position;
    }

    void Update()
    {
        if (Health <= 0f) return;
        if (Time.time - LastHitAt < PlayerTuning.Health.RegenDelay) return;
        if (Health >= PlayerTuning.Health.Max) return;
        if (Time.time - _regenStart < PlayerTuning.Health.RegenRamp) _regenStart = Time.time;
        Health = Mathf.Min(PlayerTuning.Health.Max, Health + PlayerTuning.Health.RegenRate * Time.deltaTime);
    }

    public void TakeDamage(float amount, Vector3 from)
    {
        if (Health <= 0f) return;
        Health -= amount;
        LastHitAt = Time.time;
        if (hurtClip) AudioSource.PlayClipAtPoint(hurtClip, transform.position, 0.5f);
        _ = from;
        if (Health <= 0f) Die();
    }

    void Die()
    {
        Health = 0f;
        Deaths++;
        if (deathClip) AudioSource.PlayClipAtPoint(deathClip, transform.position, 0.8f);
        var cc = GetComponent<CharacterController>();
        if (cc) cc.enabled = false;
        transform.position = _spawn;
        transform.rotation = Quaternion.identity;
        if (cc) cc.enabled = true;
        Health = PlayerTuning.Health.Max;
        LastHitAt = -100f;
    }
}

/// <summary>
/// IMGUI HUD. Placeholder on purpose: upstream's HUD is DOM and CSS, and porting
/// that is its own job. This exists so the game state is legible while playing.
/// </summary>
public class Hud : MonoBehaviour
{
    public WeaponSystem weapon;
    public PlayerHealth health;
    public Enemy[] enemies;

    Texture2D _white;
    float _hitFlash;

    void OnGUI()
    {
        if (_white == null)
        {
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
        }
        var centred = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        var left = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontSize = 16 };

        // Crosshair.
        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.85f);
        GUI.DrawTexture(new Rect(cx - 1f, cy - 9f, 2f, 6f), _white);
        GUI.DrawTexture(new Rect(cx - 1f, cy + 3f, 2f, 6f), _white);
        GUI.DrawTexture(new Rect(cx - 9f, cy - 1f, 6f, 2f), _white);
        GUI.DrawTexture(new Rect(cx + 3f, cy - 1f, 6f, 2f), _white);
        GUI.color = prev;

        if (weapon != null)
        {
            GUI.Label(new Rect(Screen.width - 190f, Screen.height - 60f, 170f, 24f),
                $"{weapon.Ammo} / {weapon.magSize}", centred);
            if (weapon.Reloading)
                GUI.Label(new Rect(Screen.width - 190f, Screen.height - 84f, 170f, 24f), "RELOADING", centred);
        }

        if (health != null)
        {
            GUI.color = new Color(0.9f, 0.25f, 0.25f, 0.9f);
            GUI.DrawTexture(new Rect(24f, Screen.height - 48f, 220f * (health.Health / PlayerTuning.Health.Max), 10f), _white);
            GUI.color = prev;
            GUI.Label(new Rect(24f, Screen.height - 72f, 220f, 24f), $"{Mathf.CeilToInt(health.Health)}", left);
        }

        if (enemies != null)
        {
            int alive = 0;
            int total = enemies.Length;
            foreach (var e in enemies) if (e != null && !e.Dead) alive++;
            GUI.Label(new Rect(Screen.width - 190f, 20f, 170f, 24f), $"enemies {alive}/{total}", left);
        }

        // Hit marker, so a hit registers without watching the target.
        if (Time.time - _hitFlash < 0.12f)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            GUI.DrawTexture(new Rect(cx - 7f, cy - 7f, 3f, 3f), _white);
            GUI.DrawTexture(new Rect(cx + 4f, cy - 7f, 3f, 3f), _white);
            GUI.DrawTexture(new Rect(cx - 7f, cy + 4f, 3f, 3f), _white);
            GUI.DrawTexture(new Rect(cx + 4f, cy + 4f, 3f, 3f), _white);
            GUI.color = prev;
        }

        if (health != null && health.Health <= 0f)
            GUI.Label(new Rect(0f, cy - 60f, Screen.width, 40f), "DOWN — respawning", centred);
    }

    public void FlashHit() => _hitFlash = Time.time;
}
