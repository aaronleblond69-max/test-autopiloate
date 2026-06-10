using System;
using SFS.Parts.Modules;
using SFS.UI;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace RevolutionlessAutopilot
{
    public enum LandingState { Idle, Deorbit, Descent, Suicide, Touchdown }

    public class LandingAutopilot
    {
        private Rocket rocket;
        private LandingState state = LandingState.Idle;

        private const double DEORBIT_PERIAPSIS_TARGET = -5000;   // below surface = impact trajectory
        private const double SUICIDE_BURN_SAFETY_FACTOR = 1.15;  // start burn slightly early
        private const double HOVER_ALTITUDE = 200.0;             // start final hover at this altitude (m)
        private const double TOUCHDOWN_ALTITUDE = 5.0;           // consider landed
        private const float LANDING_THROTTLE_P = 0.05f;
        private const double GUIDED_SETTLE = 0.5;
        private const float PITCH_TOLERANCE = 3f;
        private const float ANGULAR_VEL_TOLERANCE = 5f;

        private double throttleUnlock;

        public bool IsActive { get; private set; }

        public LandingAutopilot(Rocket rocket) { this.rocket = rocket; }
        public void SetRocket(Rocket r) { rocket = r; }

        public void Start()
        {
            if (rocket == null) return;
            IsActive = true;
            state = LandingState.Deorbit;
            throttleUnlock = 0;
            MsgDrawer.main.Log("Landing autopilot started.");
        }

        public void Stop()
        {
            IsActive = false;
            state = LandingState.Idle;
            if (rocket != null)
            {
                rocket.throttle.throttleOn.Value = false;
                rocket.throttle.throttlePercent.Value = 0f;
                rocket.arrowkeys.turnAxis.Value = 0f;
            }
            MsgDrawer.main.Log("Landing autopilot stopped.");
        }

        public void FixedUpdate()
        {
            if (!IsActive || rocket == null) return;

            double altitude = rocket.location.Value.Radius - rocket.location.planet.Value.Radius;
            double verticalVelocity = GetVerticalVelocity();
            double speed = rocket.location.velocity.Value.magnitude;
            double gravity = GetGravity(rocket.location.Value.Radius);
            double maxAccel = GetMaxAccel();

            switch (state)
            {
                case LandingState.Deorbit:
                    // Point retrograde and burn to lower periapsis below surface
                    float retroAngle = GetRetrogradeAngle();
                    SetPitch(retroAngle);
                    if (IsPitchSettled(retroAngle))
                    {
                        var orbit = GetOrbit();
                        if (orbit != null && orbit.periapsis - rocket.location.planet.Value.Radius < DEORBIT_PERIAPSIS_TARGET)
                        {
                            CutEngines();
                            state = LandingState.Descent;
                            MsgDrawer.main.Log("Deorbit complete, descending.");
                        }
                        else SetThrottle(1f);
                    }
                    else SetThrottle(0f);
                    break;

                case LandingState.Descent:
                    // Coast, keep pointed retrograde for aerobraking/drag
                    rocket.arrowkeys.turnAxis.Value = 0f;
                    SetThrottle(0f);
                    // Start suicide burn when we can cancel velocity before surface
                    double timeToImpact = altitude / Math.Max(Math.Abs(verticalVelocity), 0.1);
                    double burnTime = maxAccel > 0 ? speed / maxAccel : double.PositiveInfinity;
                    if (altitude < HOVER_ALTITUDE * 5 && burnTime * SUICIDE_BURN_SAFETY_FACTOR >= timeToImpact)
                    {
                        state = LandingState.Suicide;
                        throttleUnlock = WorldTime.main.worldTime + GUIDED_SETTLE;
                        MsgDrawer.main.Log("Suicide burn initiated.");
                    }
                    break;

                case LandingState.Suicide:
                    float retroAngle2 = GetRetrogradeAngle();
                    SetPitch(retroAngle2);
                    if (altitude <= HOVER_ALTITUDE)
                    {
                        state = LandingState.Touchdown;
                        break;
                    }
                    if (WorldTime.main.worldTime >= throttleUnlock && IsPitchSettled(retroAngle2))
                    {
                        // Throttle to cancel horizontal speed + counteract gravity
                        float desiredDecel = (float)(speed / Math.Max(altitude / Math.Max(Math.Abs(verticalVelocity), 0.1), 0.5));
                        float throttle = Mathf.Clamp01((float)((desiredDecel + gravity) / Math.Max(maxAccel, 0.1)));
                        SetThrottle(throttle);
                    }
                    else SetThrottle(0f);
                    break;

                case LandingState.Touchdown:
                    // Point straight up, throttle to hover then slowly descend
                    float upAngle = GetUpAngle();
                    SetPitch(upAngle);
                    if (altitude <= TOUCHDOWN_ALTITUDE)
                    {
                        CutEngines();
                        Stop();
                        MsgDrawer.main.Log("Touchdown! Landing complete.");
                        return;
                    }
                    if (IsPitchSettled(upAngle))
                    {
                        // PID: target zero vertical velocity, slow descent at low altitude
                        double targetVvel = altitude > 20 ? -5.0 : -1.0;
                        double vvelError = targetVvel - verticalVelocity;
                        float hoverThrottle = Mathf.Clamp01((float)((gravity - vvelError * 2.0) / Math.Max(maxAccel, 0.1)));
                        SetThrottle(hoverThrottle);
                    }
                    else SetThrottle(Mathf.Clamp01((float)(gravity / Math.Max(maxAccel, 0.1))));
                    break;
            }
        }

        private double GetVerticalVelocity()
        {
            Double2 radial = rocket.location.position.Value.normalized;
            return Double2.Dot(rocket.location.velocity.Value, radial);
        }

        private float GetRetrogradeAngle()
        {
            var vel = rocket.location.velocity.Value;
            if (vel.magnitude < 0.1) return GetUpAngle() + 180f;
            return (float)(Math.Atan2(-vel.y, -vel.x) * Mathf.Rad2Deg);
        }

        private float GetUpAngle()
        {
            Double2 up = rocket.location.position.Value.normalized;
            return (float)(Math.Atan2(up.y, up.x) * Mathf.Rad2Deg);
        }

        private double GetGravity(double radius)
        {
            var planet = rocket.location.planet.Value;
            return planet.mass / (radius * radius);
        }

        private double GetMaxAccel()
        {
            double thrust = 0;
            foreach (var e in rocket.partHolder.GetModules<EngineModule>()) if (e.engineOn.Value) thrust += e.thrust.Value * 9.8;
            foreach (var b in rocket.partHolder.GetModules<BoosterModule>()) if (b.enabled) thrust += b.thrustVector.Value.magnitude * 9.8;
            double mass = rocket.mass.GetMass();
            return mass <= 0 ? 0 : thrust / mass;
        }

        private void SetThrottle(float v)
        {
            v = Mathf.Clamp01(v);
            if (v < 0.01f) v = 0f;
            rocket.throttle.throttlePercent.Value = v;
            rocket.throttle.throttleOn.Value = v > 0f;
        }

        private void CutEngines() => SetThrottle(0f);

        private float NormalizeAngle(float a) { float m = (a + 180f) % 360f; if (m < 0) m += 360f; return m - 180f; }

        private bool IsPitchSettled(float target) =>
            Mathf.Abs(NormalizeAngle(target - rocket.GetRotation())) <= PITCH_TOLERANCE &&
            Math.Abs(rocket.rb2d.angularVelocity) <= ANGULAR_VEL_TOLERANCE;

        private void SetPitch(float targetDeg)
        {
            float err = NormalizeAngle(targetDeg - rocket.GetRotation());
            float av = rocket.rb2d.angularVelocity;
            float turn = Mathf.Abs(err) < PITCH_TOLERANCE ? 0f : -Mathf.Sign(err) * Mathf.Clamp01(Mathf.Abs(err) / 20f + Mathf.Abs(av) * 0.1f);
            rocket.arrowkeys.turnAxis.Value = Mathf.Clamp(turn - av * 0.05f, -1f, 1f);
        }

        private Orbit GetOrbit() { if (rocket?.mapPlayer?.Trajectory == null) return null; var p = rocket.mapPlayer.Trajectory.paths; return p.Count == 0 ? null : p[0] as Orbit; }
    }
}
