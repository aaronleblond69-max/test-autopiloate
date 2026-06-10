using System;
using SFS.Parts.Modules;
using SFS.UI;
using SFS.World;
using UnityEngine;

namespace RevolutionlessAutopilot
{
    public enum DockingState { Idle, Align, Approach, Fine, Done }

    public class DockingAutopilot
    {
        private Rocket rocket;
        private DockingState state = DockingState.Idle;
        private Rocket target;

        private const double ALIGN_DISTANCE = 50.0;      // m, switch to fine approach
        private const double DOCK_DISTANCE = 5.0;        // m, consider docked
        private const double MAX_APPROACH_SPEED = 5.0;   // m/s
        private const double FINE_APPROACH_SPEED = 1.0;  // m/s
        private const float PITCH_TOLERANCE = 1.5f;
        private const float ANGULAR_VEL_TOL = 3f;

        public bool IsActive { get; private set; }

        public DockingAutopilot(Rocket rocket) { this.rocket = rocket; }
        public void SetRocket(Rocket r) { rocket = r; }
        public void SetTarget(Rocket t) { target = t; }
        public Rocket Target => target;

        public void Start()
        {
            if (rocket == null) { MsgDrawer.main.Log("No rocket."); return; }
            if (target == null) { MsgDrawer.main.Log("Select a target for docking."); return; }
            IsActive = true;
            state = DockingState.Align;
            MsgDrawer.main.Log("Docking autopilot started.");
        }

        public void Stop()
        {
            IsActive = false;
            state = DockingState.Idle;
            if (rocket != null)
            {
                rocket.throttle.throttleOn.Value = false;
                rocket.throttle.throttlePercent.Value = 0f;
                rocket.arrowkeys.turnAxis.Value = 0f;
            }
            MsgDrawer.main.Log("Docking autopilot stopped.");
        }

        public void FixedUpdate()
        {
            if (!IsActive || rocket == null || target == null) { if (IsActive) Stop(); return; }

            Double2 relPos = target.location.position.Value - rocket.location.position.Value;
            Double2 relVel = rocket.location.velocity.Value - target.location.velocity.Value;
            double dist = relPos.magnitude;
            double closingSpeed = -Double2.Dot(relVel, relPos.normalized); // positive = closing in

            switch (state)
            {
                case DockingState.Align:
                    // Kill relative velocity first, then align toward target
                    if (relVel.magnitude > 0.5)
                    {
                        // Kill relative velocity
                        float killDir = (float)(Math.Atan2(-relVel.y, -relVel.x) * Mathf.Rad2Deg);
                        SetPitch(killDir);
                        if (IsPitchSettled(killDir))
                            SetThrottle(Mathf.Clamp01((float)(relVel.magnitude / 2.0)));
                        else SetThrottle(0f);
                    }
                    else
                    {
                        CutEngines();
                        state = dist <= ALIGN_DISTANCE ? DockingState.Fine : DockingState.Approach;
                    }
                    break;

                case DockingState.Approach:
                    if (dist <= ALIGN_DISTANCE) { state = DockingState.Fine; break; }
                    double targetSpeed = dist > 200 ? MAX_APPROACH_SPEED : FINE_APPROACH_SPEED;
                    float toTarget = GetAngleToTarget(relPos);

                    if (closingSpeed > targetSpeed * 1.2)
                    {
                        // Braking
                        float brakeDir = (float)(Math.Atan2(-relVel.y, -relVel.x) * Mathf.Rad2Deg);
                        SetPitch(brakeDir);
                        if (IsPitchSettled(brakeDir)) SetThrottle(Mathf.Clamp01((float)((closingSpeed - targetSpeed) / 3.0)));
                        else SetThrottle(0f);
                    }
                    else
                    {
                        SetPitch(toTarget);
                        if (IsPitchSettled(toTarget))
                            SetThrottle(Mathf.Clamp01((float)((targetSpeed - closingSpeed) / targetSpeed * 0.3f)));
                        else SetThrottle(0f);
                    }
                    break;

                case DockingState.Fine:
                    if (dist <= DOCK_DISTANCE) { CutEngines(); state = DockingState.Done; MsgDrawer.main.Log("Docking complete!"); Stop(); break; }

                    float toTargetFine = GetAngleToTarget(relPos);
                    if (closingSpeed > FINE_APPROACH_SPEED * 1.2)
                    {
                        float brakeDir2 = (float)(Math.Atan2(-relVel.y, -relVel.x) * Mathf.Rad2Deg);
                        SetPitch(brakeDir2);
                        if (IsPitchSettled(brakeDir2)) SetThrottle(Mathf.Clamp01((float)((closingSpeed - FINE_APPROACH_SPEED) / 2.0)));
                        else SetThrottle(0f);
                    }
                    else
                    {
                        SetPitch(toTargetFine);
                        if (IsPitchSettled(toTargetFine))
                            SetThrottle(Mathf.Clamp01((float)((FINE_APPROACH_SPEED - closingSpeed) / FINE_APPROACH_SPEED * 0.2f)));
                        else SetThrottle(0f);
                    }
                    break;
            }
        }

        private float GetAngleToTarget(Double2 relPos) =>
            (float)(Math.Atan2(relPos.y, relPos.x) * Mathf.Rad2Deg);

        private void SetThrottle(float v)
        {
            v = Mathf.Clamp01(v);
            if (v < 0.01f) v = 0f;
            rocket.throttle.throttlePercent.Value = v;
            rocket.throttle.throttleOn.Value = v > 0f;
        }

        private void CutEngines() => SetThrottle(0f);
        private float NormalizeAngle(float a) { float m = (a + 180f) % 360f; if (m < 0) m += 360f; return m - 180f; }
        private bool IsPitchSettled(float t) => Mathf.Abs(NormalizeAngle(t - rocket.GetRotation())) <= PITCH_TOLERANCE && Math.Abs(rocket.rb2d.angularVelocity) <= ANGULAR_VEL_TOL;
        private void SetPitch(float t) { float e = NormalizeAngle(t - rocket.GetRotation()); float av = rocket.rb2d.angularVelocity; rocket.arrowkeys.turnAxis.Value = Mathf.Clamp(-Mathf.Sign(e) * Mathf.Clamp01(Mathf.Abs(e) / 20f + Mathf.Abs(av) * 0.1f) - av * 0.05f, -1f, 1f); }
    }
}
