using System;
using System.Collections.Generic;
using System.Linq;
using SFS.Parts.Modules;
using SFS.UI;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace RevolutionlessAutopilot
{
    public enum RendezvousState { Idle, AlignOrbit, CoastToPhase, HohmannBurn1, Coast, HohmannBurn2, Approach, Done }

    public class RendezvousAutopilot
    {
        private Rocket rocket;
        private RendezvousState state = RendezvousState.Idle;
        private Rocket target;

        private const double CLOSE_ENOUGH_DISTANCE = 200.0;     // m, switch to docking mode
        private const double APPROACH_SPEED = 10.0;             // m/s max approach speed
        private const double FINE_APPROACH_SPEED = 2.0;         // m/s when < 500m
        private const double PHASE_ANGLE_TOLERANCE = 2.0;       // degrees
        private const double BURN_SETTLE = 0.75;
        private const float PITCH_TOLERANCE = 2f;
        private const float ANGULAR_VEL_TOL = 4f;

        private double throttleUnlock;
        private double burnStartTime;
        private bool burn1Done;

        public bool IsActive { get; private set; }

        public RendezvousAutopilot(Rocket rocket) { this.rocket = rocket; }
        public void SetRocket(Rocket r) { rocket = r; }
        public void SetTarget(Rocket t) { target = t; }
        public Rocket Target => target;

        public void Start()
        {
            if (rocket == null) { MsgDrawer.main.Log("No rocket."); return; }
            if (target == null) { MsgDrawer.main.Log("Select a target first."); return; }
            IsActive = true;
            burn1Done = false;
            state = RendezvousState.AlignOrbit;
            MsgDrawer.main.Log($"Rendezvous autopilot started.");
        }

        public void Stop()
        {
            IsActive = false;
            state = RendezvousState.Idle;
            if (rocket != null)
            {
                rocket.throttle.throttleOn.Value = false;
                rocket.throttle.throttlePercent.Value = 0f;
                rocket.arrowkeys.turnAxis.Value = 0f;
            }
            MsgDrawer.main.Log("Rendezvous autopilot stopped.");
        }

        public void FixedUpdate()
        {
            if (!IsActive || rocket == null || target == null) { if (IsActive) Stop(); return; }

            double relDist = GetRelativeDistance();
            double relSpeed = GetRelativeSpeed();

            switch (state)
            {
                case RendezvousState.AlignOrbit:
                    // Match target orbital altitude: burn prograde/retrograde
                    double myApo = GetApoapsis(rocket);
                    double tgtRadius = GetOrbitRadius(target);
                    if (tgtRadius <= 0) { Stop(); return; }

                    double apoError = tgtRadius - myApo;
                    if (Math.Abs(apoError) < 2000.0)
                    {
                        state = RendezvousState.CoastToPhase;
                        CutEngines();
                        MsgDrawer.main.Log("Orbit aligned, waiting for phase angle.");
                        break;
                    }

                    float burnDir = apoError > 0 ? GetProgradeAngle() : GetRetrogradeAngle();
                    SetPitch(burnDir);
                    if (IsPitchSettled(burnDir))
                        SetThrottle(Mathf.Clamp01((float)(Math.Abs(apoError) / 20000.0)));
                    else SetThrottle(0f);
                    break;

                case RendezvousState.CoastToPhase:
                    CutEngines();
                    rocket.arrowkeys.turnAxis.Value = 0f;
                    // Wait until phase angle is right for Hohmann transfer
                    double phase = GetPhaseAngle();
                    if (Math.Abs(phase) < PHASE_ANGLE_TOLERANCE || Math.Abs(phase - 360) < PHASE_ANGLE_TOLERANCE)
                    {
                        state = RendezvousState.HohmannBurn1;
                        throttleUnlock = WorldTime.main.worldTime + BURN_SETTLE;
                        MsgDrawer.main.Log("Phase angle good, executing transfer burn 1.");
                    }
                    break;

                case RendezvousState.HohmannBurn1:
                    float pg1 = GetProgradeAngle();
                    SetPitch(pg1);
                    double tgtOrbit1 = GetOrbitRadius(target);
                    double myApo1 = GetApoapsis(rocket);
                    if (WorldTime.main.worldTime >= throttleUnlock && IsPitchSettled(pg1))
                    {
                        if (myApo1 >= tgtOrbit1 - 1000)
                        {
                            CutEngines();
                            state = RendezvousState.Coast;
                            burn1Done = true;
                            MsgDrawer.main.Log("Burn 1 done, coasting to apoapsis.");
                        }
                        else SetThrottle(Mathf.Clamp01((float)((tgtOrbit1 - myApo1) / 20000.0)));
                    }
                    else SetThrottle(0f);
                    break;

                case RendezvousState.Coast:
                    CutEngines();
                    rocket.arrowkeys.turnAxis.Value = 0f;
                    // Wait until near apoapsis
                    double timeToApo = GetTimeToApoapsis(rocket);
                    if (timeToApo < 5.0 || relDist < CLOSE_ENOUGH_DISTANCE * 5)
                    {
                        state = RendezvousState.HohmannBurn2;
                        throttleUnlock = WorldTime.main.worldTime + BURN_SETTLE;
                        MsgDrawer.main.Log("Near apoapsis, executing circularization burn.");
                    }
                    break;

                case RendezvousState.HohmannBurn2:
                    float pg2 = GetProgradeAngle();
                    SetPitch(pg2);
                    if (WorldTime.main.worldTime >= throttleUnlock && IsPitchSettled(pg2))
                    {
                        if (relDist < CLOSE_ENOUGH_DISTANCE * 3 || IsCircularized())
                        {
                            CutEngines();
                            state = RendezvousState.Approach;
                            MsgDrawer.main.Log("Approaching target...");
                        }
                        else SetThrottle(Mathf.Clamp01((float)(relDist / 50000.0)));
                    }
                    else SetThrottle(0f);
                    break;

                case RendezvousState.Approach:
                    if (relDist < CLOSE_ENOUGH_DISTANCE)
                    {
                        CutEngines();
                        state = RendezvousState.Done;
                        MsgDrawer.main.Log("Rendezvous complete! Ready for docking.");
                        Stop();
                        break;
                    }
                    // Point toward target and burn, limiting speed
                    float toTarget = GetAngleToTarget();
                    SetPitch(toTarget);
                    double maxSpeed = relDist < 500 ? FINE_APPROACH_SPEED : APPROACH_SPEED;
                    if (IsPitchSettled(toTarget))
                    {
                        // If closing too fast, point retrograde and brake
                        Double2 relVel = GetRelativeVelocity();
                        Double2 relPos = GetRelativePosition();
                        double closingSpeed = -Double2.Dot(relVel, relPos.normalized);
                        if (closingSpeed > maxSpeed)
                        {
                            float brake = GetRetrogradeAngle();
                            SetPitch(brake);
                            SetThrottle(Mathf.Clamp01((float)((closingSpeed - maxSpeed) / 5.0)));
                        }
                        else SetThrottle(Mathf.Clamp01((float)((maxSpeed - closingSpeed) / maxSpeed * 0.3f)));
                    }
                    else SetThrottle(0f);
                    break;
            }
        }

        private double GetRelativeDistance()
        {
            if (target == null) return double.MaxValue;
            return (rocket.location.position.Value - target.location.position.Value).magnitude;
        }

        private double GetRelativeSpeed()
        {
            if (target == null) return 0;
            return (rocket.location.velocity.Value - target.location.velocity.Value).magnitude;
        }

        private Double2 GetRelativeVelocity() => rocket.location.velocity.Value - (target?.location.velocity.Value ?? Double2.zero);
        private Double2 GetRelativePosition() => target != null ? (target.location.position.Value - rocket.location.position.Value) : Double2.zero;

        private float GetAngleToTarget()
        {
            Double2 dir = GetRelativePosition();
            return (float)(Math.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        }

        private float GetProgradeAngle()
        {
            var v = rocket.location.velocity.Value;
            return (float)(Math.Atan2(v.y, v.x) * Mathf.Rad2Deg);
        }

        private float GetRetrogradeAngle() => GetProgradeAngle() + 180f;

        private double GetPhaseAngle()
        {
            if (target == null) return 0;
            Double2 myPos = rocket.location.position.Value.normalized;
            Double2 tgtPos = target.location.position.Value.normalized;
            double dot = Double2.Dot(myPos, tgtPos);
            dot = Math.Max(-1, Math.Min(1, dot));
            return Math.Acos(dot) * Mathf.Rad2Deg;
        }

        private double GetOrbitRadius(Rocket r)
        {
            if (r?.mapPlayer?.Trajectory == null) return 0;
            var paths = r.mapPlayer.Trajectory.paths;
            if (paths.Count == 0) return 0;
            var orbit = paths[0] as Orbit;
            if (orbit == null) return 0;
            return (orbit.apoapsis + orbit.periapsis) / 2.0;
        }

        private double GetApoapsis(Rocket r)
        {
            if (r?.mapPlayer?.Trajectory == null) return 0;
            var paths = r.mapPlayer.Trajectory.paths;
            if (paths.Count == 0) return 0;
            var orbit = paths[0] as Orbit;
            return orbit?.apoapsis ?? 0;
        }

        private double GetTimeToApoapsis(Rocket r)
        {
            if (r?.mapPlayer?.Trajectory == null) return double.PositiveInfinity;
            var paths = r.mapPlayer.Trajectory.paths;
            if (paths.Count == 0) return double.PositiveInfinity;
            var orbit = paths[0] as Orbit;
            if (orbit == null) return double.PositiveInfinity;
            double now = WorldTime.main.worldTime;
            return orbit.GetNextTrueAnomalyPassTime(now, Math.PI) - now;
        }

        private bool IsCircularized()
        {
            if (rocket?.mapPlayer?.Trajectory == null) return false;
            var paths = rocket.mapPlayer.Trajectory.paths;
            if (paths.Count == 0) return false;
            var orbit = paths[0] as Orbit;
            if (orbit == null) return false;
            double tgt = GetOrbitRadius(target);
            return Math.Abs(orbit.periapsis - tgt) < 5000 && Math.Abs(orbit.apoapsis - tgt) < 5000;
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

        private bool IsPitchSettled(float target2) =>
            Mathf.Abs(NormalizeAngle(target2 - rocket.GetRotation())) <= PITCH_TOLERANCE &&
            Math.Abs(rocket.rb2d.angularVelocity) <= ANGULAR_VEL_TOL;

        private void SetPitch(float targetDeg)
        {
            float err = NormalizeAngle(targetDeg - rocket.GetRotation());
            float av = rocket.rb2d.angularVelocity;
            float turn = -Mathf.Sign(err) * Mathf.Clamp01(Mathf.Abs(err) / 20f + Mathf.Abs(av) * 0.1f);
            rocket.arrowkeys.turnAxis.Value = Mathf.Clamp(turn - av * 0.05f, -1f, 1f);
        }
    }
}
