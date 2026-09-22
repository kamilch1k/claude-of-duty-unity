using UnityEngine;

/// <summary>
/// Every number that defines how the player feels, ported from
/// src/player/tuning.js. Nothing here is invented: the file is transcribed so
/// the port can be tuned by the same numbers the browser build was tuned by.
///
/// Calibration upstream is matched to Modern Warfare (2019) / MWII, which are
/// authored in inches at 20 units = 1 ft. The comments kept below are the ones
/// that explain *why* a number is what it is — those are the ones worth keeping
/// when someone later decides to "fix" the movement and finds it feels worse.
/// </summary>
public static class PlayerTuning
{
    /// <summary>Negative, as upstream ships it (-20.6 m/s^2).</summary>
    public const float Gravity = -20.6f;
    public const float JumpApex = 0.6f;

    /// <summary>v = sqrt(2 g h), solved from the apex so tuning the apex is meaningful.</summary>
    public static readonly float JumpSpeed = Mathf.Sqrt(2f * Mathf.Abs(Gravity) * JumpApex);

    public const float PlayerHeight = 1.78f;
    public const float PlayerCrouchHeight = 1.12f;
    public const float EyeOffset = 0.12f;

    public struct Stance
    {
        public string Name;
        public float Height;
        public float Eye;
        public float Speed;
        public float StepHeight;
        public float StrideLength;
    }

    public static readonly Stance Stand = new()
    {
        Name = "stand", Height = PlayerHeight, Eye = PlayerHeight - EyeOffset,
        Speed = 4.0f, StepHeight = 0.42f, StrideLength = 1.48f,
    };

    public static readonly Stance Crouch = new()
    {
        Name = "crouch", Height = PlayerCrouchHeight, Eye = PlayerCrouchHeight - 0.1f,
        Speed = 2.1f, StepHeight = 0.3f, StrideLength = 1.05f,
    };

    public static readonly Stance Prone = new()
    {
        Name = "prone", Height = 0.7f, Eye = 0.4f,
        Speed = 1.01f, StepHeight = 0.14f, StrideLength = 0.78f,
    };

    public static class Move
    {
        public const float SprintSpeed = 6.1f;
        public const float TacSprintSpeed = 7.3f;

        /// <summary>Directional scaling — you are slower sideways and slower still backwards.</summary>
        public const float StrafeScale = 0.82f;
        public const float BackScale = 0.72f;

        /// <summary>ADS movement penalty (CoD: ~45% of base, a touch more forgiving here).</summary>
        public const float AdsScale = 0.44f;

        /// <summary>
        /// Ground response. WEIGHT LIVES HERE, not in the top speed.
        ///
        /// The original 92 m/s^2 reached full speed in 50 ms — the whole reason
        /// CoD movement feels weightless is that the body has no apparent mass.
        /// 38 takes ~105 ms to spin up and the same to wind down, which is
        /// roughly a real stride's worth of commitment: you feel the start, and a
        /// direction change costs you something.
        ///
        /// Deceleration stays below acceleration so there is a slide-off tail
        /// rather than a dead stop; stopDecel is the only fast number left —
        /// releasing every key should still plant you inside a step.
        /// </summary>
        public const float GroundAccel = 38f;
        public const float GroundDecel = 30f;
        public const float StopDecel = 26f;

        /// <summary>Air control: a quarter of ground authority, and it cannot add speed.</summary>
        public const float AirAccelScale = 0.18f;
        public const float AirSpeedCap = 3.4f;
        public const float TerminalSpeed = 55f;

        /// <summary>Grace windows that hide input/timing error.</summary>
        public const float CoyoteTime = 0.09f;
        public const float JumpBuffer = 0.13f;
        public const float JumpCooldown = 0.28f;

        /// <summary>Tactical sprint is a double-tap of sprint, MWII style.</summary>
        public const float TacSprintTapWindow = 0.32f;
        public const float TacSprintMaxTime = 6.0f;
        public const float TacSprintRecovery = 1.6f;
        public const float SprintForwardDot = 0.55f;
        public const float SprintStartDelay = 0.05f;
    }

    public static class Slide
    {
        /// <summary>Scaled with the slower sprint: minEntry must stay above sprintSpeed.</summary>
        public const float EntrySpeed = 7.7f;
        public const float MinEntry = 6.9f;
        public const float ExitSpeed = 2.95f;
        public const float Duration = 0.9f;
        public const float Drag = 0.75f;
        public const float Brake = 0.85f;
        public const float Cooldown = 0.55f;
        public const float MinSpeedToStart = 5.2f;
        /// <summary>Steering authority while sliding — enough to curve, not to turn around.</summary>
        public const float Steer = 2.6f;
        public const float SlopeAssist = 9.0f;
    }

    public static class Mantle
    {
        public const float AutoVaultMax = 0.72f;
        public const float MinHeight = 0.34f;
        public const float MaxHeight = 1.85f;
        public const float Reach = 0.62f;
        public const float LandDepth = 0.46f;
        public const float VaultTime = 0.34f;
        public const float MantleTime = 0.62f;
        public const float HighMantleTime = 0.82f;
        public const float Cooldown = 0.2f;
        public const float AutoSpeed = 2.4f;
        public const float ProactiveDistance = 0.2f;
        public const float ProactiveLookahead = 0.035f;
    }

    public static class Lean
    {
        /// <summary>Lateral camera travel at full lean — enough to clear a doorframe.</summary>
        public const float Offset = 0.46f;
        public const float Roll = 19f * Mathf.Deg2Rad;
        public const float Drop = 0.055f;
        /// <summary>tau. At 0.085 the lean snapped to full in two frames and read as a toggle.</summary>
        public const float Rate = 0.19f;
        public const float ProbeRadius = 0.17f;
    }

    /// <summary>Stance transition time constants (seconds to 63%).</summary>
    public static class StanceTau
    {
        public const float StandCrouch = 0.062f;
        public const float CrouchStand = 0.072f;
        public const float Prone = 0.16f;
    }

    public static class Camera
    {
        /// <summary>
        /// View bob. Figure-eight (1:2 Lissajous) locked to the footstep cadence,
        /// so the eye is at a horizontal extreme exactly when a foot lands.
        /// Amplitudes are metres at base run speed — deliberately small; anything
        /// larger reads as nausea rather than weight.
        /// </summary>
        public static class Bob
        {
            public const float AmpX = 0.023f;
            public const float AmpY = 0.017f;
            public const float AmpZ = 0.0085f;
            public const float Roll = 0.62f * Mathf.Deg2Rad;
            public const float Pitch = 0.24f * Mathf.Deg2Rad;
            public const float SpeedExp = 0.85f;
            public const float SpeedCap = 1.55f;
            public const float AdsScale = 0.22f;
            public const float AirFade = 0.11f;
        }

        /// <summary>
        /// Per-footstep vertical micro-shift, on top of the bob. A discrete
        /// impulse per foot plant, not a continuous wave.
        /// </summary>
        public static class Step
        {
            public const float Impulse = 0.16f;
            public const float Freq = 5.4f;
            public const float Damping = 0.52f;
            public const float SprintScale = 1.7f;
        }

        public static class Land
        {
            public const float MinSpeed = 2.2f;
            public const float FullSpeed = 12.5f;
            public const float DipImpulse = 2.35f;
            public const float Pitch = 3.4f * Mathf.Deg2Rad;
            public const float Roll = 0.9f * Mathf.Deg2Rad;
            public const float Freq = 3.05f;
            public const float Damping = 0.52f;
            public const float Trauma = 0.34f;
            /// <summary>Hard landings cost health (CoD only damages above ~14 m/s).</summary>
            public const float DamageSpeed = 15.0f;
            public const float DamagePerSpeed = 7.0f;
        }

        /// <summary>Lean-into-the-turn. Small; it is felt, not seen.</summary>
        public static class Roll
        {
            public const float Strafe = 1.05f * Mathf.Deg2Rad;
            public const float YawRate = 0.055f;
            public const float YawRateMax = 1.5f * Mathf.Deg2Rad;
            public const float Tau = 0.11f;
            public const float Slide = 5.2f * Mathf.Deg2Rad;
            public const float Air = 0.9f * Mathf.Deg2Rad;
        }

        public static class Recoil
        {
            public const float Freq = 9.5f;
            public const float Damping = 0.5f;
            public const float ResidualTau = 0.28f;
            public const float ResidualShare = 0.34f;
            public const float PunchFreq = 12f;
            public const float PunchDamping = 0.62f;
        }

        public static class Shake
        {
            public const float Decay = 1.85f;
            public const float Rot = 1.35f;
            public const float Pos = 0.022f;
            public const float Freq = 22f;
        }

        public static class Breath
        {
            /// <summary>Resting respiration ~14/min while idle.</summary>
            public const float FreqA = 0.235f;
            public const float FreqB = 0.155f;
            public const float Amp = 0.0021f;
            public const float PosAmp = 0.0035f;
            /// <summary>Scoped optics magnify hold error — sway grows when aiming.</summary>
            public const float AdsScale = 1.85f;
            public const float LowHealthScale = 2.6f;
            public const float MoveDamp = 0.78f;
            public const float SuppressionScale = 2.2f;
        }

        public static class Fov
        {
            /// <summary>Multipliers on the base FOV.</summary>
            public const float Sprint = 1.055f;
            public const float TacSprint = 1.1f;
            public const float Slide = 1.085f;
            public const float Air = 1.015f;
            /// <summary>Time constants — ADS must be crisp, sprint can breathe.</summary>
            public const float AdsTau = 0.052f;
            public const float MoveTau = 0.13f;
        }

        /// <summary>Camera never gets closer than this to a wall when leaning/mantling.</summary>
        public const float WallPad = 0.09f;
        public const float PitchLimit = 88f * Mathf.Deg2Rad;
    }

    public static class Health
    {
        public const float Max = 100f;
        /// <summary>CoD: regen starts ~5 s after the last hit and refills in ~2.5 s.</summary>
        public const float RegenDelay = 4.6f;
        public const float RegenRate = 34f;
        public const float RegenRamp = 0.55f;
        public const float LowThreshold = 0.36f;
        public const float CriticalThreshold = 0.18f;
        public const float IndicatorTime = 1.8f;
        public const int IndicatorMax = 4;

        public static class Suppression
        {
            public const float PerNearMiss = 0.28f;
            public const float PerHit = 0.5f;
            public const float PerExplosion = 0.85f;
            public const float Radius = 3.2f;
            public const float Decay = 0.62f;
            public const float SwayScale = 1.5f;
            public const float ShakeScale = 0.28f;
        }
    }

    public static class Footstep
    {
        /// <summary>Foot is offset laterally from the capsule centre so FX/audio pan.</summary>
        public const float Lateral = 0.13f;
        public const float Probe = 0.9f;
        /// <summary>A step is only "running" (louder, dustier) above this speed.</summary>
        public const float RunSpeed = 4.7f;
        /// <summary>Landing suppresses the next step so you do not get a double transient.</summary>
        public const float LandHold = 0.12f;
    }
}
