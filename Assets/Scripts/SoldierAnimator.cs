using UnityEngine;

/// <summary>
/// Drives a baked skeleton procedurally.
///
/// Upstream animates these bones from authored clips (src/ai/clips.js) through a
/// CPU animator, and porting that faithfully is its own job. This is the honest
/// smaller version: the bind pose IS a patrol carry — stock in the shoulder,
/// support hand on the handguard — so a walk mostly needs legs, a bob and a
/// little counter-rotation, and it reads as a soldier walking rather than a
/// mannequin sliding along the ground.
///
/// Bones are found by name, the names being rig.js's own table.
/// </summary>
public class SoldierAnimator : MonoBehaviour
{
    public float walkSpeed = 2.4f;
    public float strideFreq = 1.85f;

    Transform _hips, _spine, _spine2, _neck, _head;
    Transform _upLegR, _legR, _footR, _upLegL, _legL, _footL;
    Transform _armR, _foreR, _armL, _foreL;

    Quaternion _upLegR0, _legR0, _footR0, _upLegL0, _legL0, _footL0;
    Quaternion _spine0, _spine20, _neck0;
    Quaternion _armR0, _foreR0, _armL0, _foreL0;

    float _phase;
    CharacterController _cc;
    Enemy _enemy;

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        _enemy = GetComponent<Enemy>();

        var root = transform.Find("body") ?? transform;
        _hips = Find(root, "Hips");
        _spine = Find(root, "Spine");
        _spine2 = Find(root, "Spine2");
        _neck = Find(root, "Neck");
        _head = Find(root, "Head");
        _upLegR = Find(root, "UpLegR"); _legR = Find(root, "LegR"); _footR = Find(root, "FootR");
        _upLegL = Find(root, "UpLegL"); _legL = Find(root, "LegL"); _footL = Find(root, "FootL");
        _armR = Find(root, "UpperArmR"); _foreR = Find(root, "ForearmR");
        _armL = Find(root, "UpperArmL"); _foreL = Find(root, "ForearmL");

        Capture(_upLegR, ref _upLegR0); Capture(_legR, ref _legR0); Capture(_footR, ref _footR0);
        Capture(_upLegL, ref _upLegL0); Capture(_legL, ref _legL0); Capture(_footL, ref _footL0);
        Capture(_spine, ref _spine0); Capture(_spine2, ref _spine20); Capture(_neck, ref _neck0);
        Capture(_head, ref _head0);
        Capture(_armR, ref _armR0); Capture(_foreR, ref _foreR0);
        Capture(_armL, ref _armL0); Capture(_foreL, ref _foreL0);
    }

    static Transform Find(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>())
            if (t.name == name) return t;
        return null;
    }

    static void Capture(Transform t, ref Quaternion store)
    {
        if (t) store = t.localRotation;
    }

    void Update()
    {
        if (_enemy != null && _enemy.Dead) return;
        float speed = Speed();

        // Cadence follows the ground speed, so the legs cannot skate: the stride
        // frequency is fixed and the stride *length* is what changes.
        if (speed > 0.15f) _phase += Time.deltaTime * strideFreq * Mathf.PI * 2f * (speed / walkSpeed);
        else _phase = Mathf.MoveTowards(_phase, Mathf.Round(_phase / Mathf.PI) * Mathf.PI, Time.deltaTime * 4f);

        float swing = Mathf.Clamp01(speed / walkSpeed) * 34f;
        float s = Mathf.Sin(_phase);
        float c = Mathf.Cos(_phase);

        Apply(_upLegR, _upLegR0, Quaternion.Euler(s * swing, 0f, 0f));
        Apply(_upLegL, _upLegL0, Quaternion.Euler(-s * swing, 0f, 0f));
        // Knees only bend one way, and they bend on the back half of the stride.
        Apply(_legR, _legR0, Quaternion.Euler(Mathf.Max(0f, -c) * swing * 1.4f, 0f, 0f));
        Apply(_legL, _legL0, Quaternion.Euler(Mathf.Max(0f, c) * swing * 1.4f, 0f, 0f));
        Apply(_footR, _footR0, Quaternion.Euler(-Mathf.Max(0f, -c) * swing * 0.5f, 0f, 0f));
        Apply(_footL, _footL0, Quaternion.Euler(-Mathf.Max(0f, c) * swing * 0.5f, 0f, 0f));

        // Torso counter-rotates against the stride and leans into the walk; the
        // arms hold the carry pose with a little sway, because a weapon at the
        // shoulder is what stops the figure reading as a pedestrian.
        float move = Mathf.Clamp01(speed / walkSpeed);
        Apply(_spine, _spine0, Quaternion.Euler(move * 4f, -s * move * 5f, s * move * 1.5f));
        if (_spine2) Apply(_spine2, _spine20, Quaternion.Euler(move * 2f, -s * move * 2.5f, 0f));
        if (_neck) Apply(_neck, _neck0, Quaternion.Euler(-move * 3f, 0f, 0f));
        if (_head) Apply(_head, _head0, Quaternion.Euler(-move * 2f, 0f, 0f));

        // The weapon hand rides the shoulder; the support hand is steadier, which
        // is the tell that this is a trained carry rather than a free swing.
        Apply(_armR, _armR0, Quaternion.Euler(c * move * 3.5f, -s * move * 2.5f, 0f));
        Apply(_foreR, _foreR0, Quaternion.Euler(-c * move * 2f, 0f, 0f));
        Apply(_armL, _armL0, Quaternion.Euler(-c * move * 2.5f, s * move * 2f, 0f));
        Apply(_foreL, _foreL0, Quaternion.Euler(c * move * 1.5f, 0f, 0f));
    }

    Quaternion _head0;

    float Speed()
    {
        if (_cc && _cc.enabled) return new Vector2(_cc.velocity.x, _cc.velocity.z).magnitude;
        // A CharacterController only reports velocity when it is moved through
        // Move(); while idle it reports zero, which is what we want.
        return 0f;
    }

    void Apply(Transform t, Quaternion bind, Quaternion delta)
    {
        if (t) t.localRotation = bind * delta;
    }
}
