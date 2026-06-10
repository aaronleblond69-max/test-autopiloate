using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SFS;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.UI;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace RevolutionlessAutopilot
{
    public enum AscentState { Idle, Liftoff, PitchOver, Coast, Circularize, TransferBurn }

    public class AscentAutopilot
    {
        private Rocket rocket;
        private AscentState state = AscentState.Idle;
        private float targetAltitude;
        private float requestedTargetAltitude;
        private double targetRadius;
        private bool pendingTransferBurn;

        // BUG FIX: debug was always true in original
        private const bool debug = false;

        private const float MIN_TARGET_ALTITUDE = 1000f;
        private const float DIRECT_ASCENT_MAX_EXTRA_ALTITUDE = 80000f;
        private const float PARKING_ORBIT_MIN_ALTITUDE = 5000f;
        private const float PARKING_ORBIT_ATMOSPHERE_MARGIN = 5000f;
        private const double COAST_TO_CIRC_MIN_LEAD_TIME = 0.75;
        private const double COAST_TO_CIRC_BURN_BUFFER = 0.35;
        private const float THROTTLE_SNAP_THRESHOLD = 0.01f;
        private const float PITCH_START_ALTITUDE = 100f;
        private const float TURN_END_ANGLE = 0f;
        private const float TURN_SHAPE_EXPONENT = 0.8f;
        private const float MIN_PITCH = 5f;
        private const double TURN_TARGET_ALTITUDE_FACTOR = 0.45;
        private const double TURN_ATMOSPHERE_ALTITUDE_FACTOR = 0.9;
        private const double TURN_NO_ATMOSPHERE_ALTITUDE_FACTOR = 0.35;
        private const double TURN_MIN_END_ALTITUDE = 2500.0;
        private const double APOAPSIS_TARGET_MARGIN = 100;
        private const double PERIAPSIS_TOLERANCE = 250;
        private const double CORRECTION_GAIN = 40000;
        private const float PERI_MAX_PITCH_CORRECTION = 2.0f;
        private const float APO_MAX_PITCH_CORRECTION = 5.0f;
        private const double APOAPSIS_OVER_CORRECTION_GAIN = 20000;
        private const double APO_THROTTLE_REDUCTION_START = 250;
        private const double APO_THROTTLE_REDUCTION_END = 1500;
        private const float APO_MIN_THROTTLE_FACTOR = 0.25f;
        private const double COAST_TO_CIRC_DISTANCE = 200;
        private const double CIRCULARIZE_MAX_DURATION = 90.0;
        private const float CIRCULARIZE_PITCH_UP_BIAS = 0.6f;
        private const double CIRCULARIZE_VEL_TOLERANCE = 2.0;
        private const double PERI_THROTTLE_GAIN = 120000.0;
        private const float PERI_THROTTLE_MIN = 0.0f;
        private const double CIRCULARIZE_NEAR_APO_DISTANCE = 5000.0;
        private const double CIRCULARIZE_NEAR_APO_TIME = 8.0;
        private const double CIRCULARIZE_BURN_WINDOW_DISTANCE = 12000.0;
        private const double CIRCULARIZE_BURN_WINDOW_TIME = 14.0;
        private const double CIRCULARIZE_PERI_CORRECTION_DISTANCE = 250.0;
        private const double CIRCULARIZE_PERI_CORRECTION_LEAD_TIME = 0.35;
        private const double MAJOR_PERIAPSIS_RAISE_MIN_ERROR = 3000.0;
        private const double MAJOR_APOAPSIS_CORRECTION_GAIN = 8000.0;
        private const float MAJOR_APOAPSIS_MAX_PITCH_CORRECTION = 1.5f;
        private const double CIRCULARIZE_RETRY_COAST_TIME = 25.0;
        private const double GUIDED_BURN_SETTLE_TIME = 0.75;
        private const float GUIDED_BURN_PITCH_TOLERANCE = 2.0f;
        private const float GUIDED_BURN_ANGULAR_VELOCITY_TOLERANCE = 4.0f;

        private double burnDuration;
        private double circularizeStartWorldTime;
        private double deltaVTarget;
        private double circularizeEntryWorldTime;
        private double throttleUnlockWorldTime;
        private bool enginesInitialized = false;
        private Stage currentStage = null;
        private HashSet<Stage> stagingAttempted = new HashSet<Stage>();
        private double actualTurnStartAltitude = 0;
        private double atmosphereHeight = 0;
        private int coastLogCounter = 0;

        public bool IsActive { get; private set; }

        public AscentAutopilot(Rocket rocket)
        {
            this.rocket = rocket;
            RefreshRuntimeSettings();
            if (rocket?.location.planet.Value != null)
                atmosphereHeight = rocket.location.planet.Value.AtmosphereHeightPhysics;
        }

        public void SetRocket(Rocket rocket)
        {
            this.rocket = rocket;
            RefreshRuntimeSettings();
        }

        public void Start()
        {
            if (rocket == null) return;
            RefreshRuntimeSettings();
            InitializeMissionTargets();
            IsActive = true;
            state = AscentState.Liftoff;
            enginesInitialized = false;
            currentStage = null;
            stagingAttempted.Clear();
            actualTurnStartAltitude = 0;
            throttleUnlockWorldTime = 0.0;
        }

        public void Stop()
        {
            IsActive = false;
            state = AscentState.Idle;
            if (rocket != null)
            {
                rocket.throttle.throttleOn.Value = false;
                rocket.throttle.throttlePercent.Value = 0f;
                rocket.arrowkeys.turnAxis.Value = 0f;
            }
        }

        private void RefreshRuntimeSettings(bool logTargetChanges = false)
        {
            requestedTargetAltitude = Mathf.Max(MIN_TARGET_ALTITUDE, Settings.data.targetOrbitAltitude);
            if (rocket?.location.planet.Value != null)
                atmosphereHeight = rocket.location.planet.Value.AtmosphereHeightPhysics;
            if (!IsActive)
            {
                targetAltitude = GetInitialInsertionTargetAltitude(requestedTargetAltitude);
                pendingTransferBurn = requestedTargetAltitude > targetAltitude + 0.1f;
            }
        }

        // BUG FIX: removed empty Update() method that was called every frame for nothing
        public void FixedUpdate()
        {
            if (!IsActive || rocket == null) return;
            RefreshRuntimeSettings(logTargetChanges: true);
            if (rocket.staging.stages.Count == 0) { Stop(); return; }

            double altitude = rocket.location.Value.Radius - rocket.location.planet.Value.Radius;
            double velocity = rocket.location.velocity.Value.magnitude;
            double planetRadius = rocket.location.planet.Value.Radius;
            targetRadius = targetAltitude + planetRadius;

            double rawApoapsis = GetApoapsis();
            double rawPeriapsis = GetPeriapsis();
            double apoapsis = rawApoapsis;
            double periapsis = rawPeriapsis;
            double apoapsisAltitude = (rawApoapsis > planetRadius) ? rawApoapsis - planetRadius : 0;
            bool aboveAtmosphere = altitude > atmosphereHeight;

            Stage newStage = rocket.staging.stages.Count > 0 ? rocket.staging.stages[0] : null;
            if (newStage != currentStage) { currentStage = newStage; enginesInitialized = false; }
            if (!enginesInitialized && currentStage != null) { InitializeEngines(); enginesInitialized = true; }
            CheckStaging();

            switch (state)
            {
                case AscentState.Liftoff:
                    SetThrottle(1f);
                    SetPitch(90f);
                    if (altitude > PITCH_START_ALTITUDE) { actualTurnStartAltitude = altitude; state = AscentState.PitchOver; }
                    break;

                case AscentState.PitchOver:
                    double turnEndAltitude = GetPitchProgramEndAltitude();
                    double turnProgress = Math.Max(0, Math.Min(1, (altitude - actualTurnStartAltitude) / (turnEndAltitude - actualTurnStartAltitude)));
                    float targetPitch = turnProgress < 1 ? Math.Max(90f - (float)Math.Pow(turnProgress, TURN_SHAPE_EXPONENT) * 90f, MIN_PITCH) : TURN_END_ANGLE;
                    SetPitch(targetPitch);
                    SetThrottle(CalculateThrottleDynamic(apoapsis, targetRadius));
                    if (aboveAtmosphere && apoapsis >= targetRadius - APOAPSIS_TARGET_MARGIN) { CutEngines(); state = AscentState.Coast; coastLogCounter = 0; }
                    break;

                case AscentState.Coast:
                    rocket.arrowkeys.turnAxis.Value = 0f;
                    coastLogCounter++;
                    if (aboveAtmosphere && WorldTime.CanTimewarp(false, false))
                    {
                        double timeToApoapsis = GetTimeToApoapsis();
                        double dv = Math.Max(0.0, CalculateTargetOrbitalSpeed(rocket.location.Value.Radius) - GetHorizontalSpeed());
                        double burnLead = Math.Max(COAST_TO_CIRC_MIN_LEAD_TIME, EstimateCircularizationBurnDuration(dv) * 0.5 + COAST_TO_CIRC_BURN_BUFFER);
                        double warpStop = timeToApoapsis - burnLead;
                        if (warpStop > 120) WorldTime.main.SetState(WorldTime.MaxTimewarpSpeed, false, false);
                        else if (warpStop > 60) WorldTime.main.SetState(50, false, false);
                        else if (warpStop > 30) WorldTime.main.SetState(25, false, false);
                        else if (warpStop > 15) WorldTime.main.SetState(10, false, false);
                        else if (warpStop > 8) WorldTime.main.SetState(5, false, false);
                        else if (WorldTime.main.timewarpSpeed > 1) WorldTime.main.StopTimewarp(false);
                    }
                    else if (WorldTime.main.timewarpSpeed > 1) WorldTime.main.StopTimewarp(false);

                    double timeToApo = GetTimeToApoapsis();
                    double distToApo = Math.Abs(apoapsisAltitude - altitude);
                    double hspd = GetHorizontalSpeed();
                    double tspd = CalculateTargetOrbitalSpeed(rocket.location.Value.Radius);
                    double rdv = Math.Max(0.0, tspd - hspd);
                    double eBurn = EstimateCircularizationBurnDuration(rdv);
                    double coastLead = Math.Max(COAST_TO_CIRC_MIN_LEAD_TIME, eBurn * 0.5 + COAST_TO_CIRC_BURN_BUFFER);

                    if (((timeToApo - coastLead <= 0.0) && WorldTime.main.timewarpSpeed <= 1) || distToApo < COAST_TO_CIRC_DISTANCE)
                    {
                        WorldTime.main.StopTimewarp(false);
                        CutEngines();
                        state = AscentState.Circularize;
                        deltaVTarget = rdv;
                        double acc = CalculateMaxAcceleration();
                        if (acc <= 0.00001) acc = CalculateMaxThrust() / rocket.mass.GetMass();
                        burnDuration = acc > 0.00001 ? deltaVTarget / acc : eBurn;
                        if (burnDuration <= 0.0 && deltaVTarget > 0.0) burnDuration = 10.0;
                        circularizeStartWorldTime = WorldTime.main.worldTime;
                        circularizeEntryWorldTime = circularizeStartWorldTime;
                        throttleUnlockWorldTime = circularizeStartWorldTime + GUIDED_BURN_SETTLE_TIME;
                        if (deltaVTarget <= 0.0001) { CutEngines(); if (pendingTransferBurn) BeginTransferBurn(); else Stop(); }
                    }
                    break;

                case AscentState.Circularize:
                    float targetPitchCirc = rocket.GetRotation();
                    bool orientationReady = false;
                    if (rocket.location.velocity.Value.magnitude > 0.1)
                    {
                        float progradeAngle = (float)Math.Atan2(rocket.location.velocity.Value.y, rocket.location.velocity.Value.x) * Mathf.Rad2Deg;
                        float horizontalAngle = GetHorizontalAngle();
                        double periError = targetRadius - periapsis;
                        bool needMajorPeri = periError > Math.Max(MAJOR_PERIAPSIS_RAISE_MIN_ERROR, Math.Min(50000.0, targetAltitude * 0.05));
                        targetPitchCirc = Mathf.LerpAngle(progradeAngle, horizontalAngle, needMajorPeri ? 0.8f : 0.35f);
                        double apoOvershoot = apoapsis - targetRadius;
                        if (Math.Abs(periError) > PERIAPSIS_TOLERANCE)
                            targetPitchCirc += Mathf.Clamp((float)(periError / CORRECTION_GAIN), -PERI_MAX_PITCH_CORRECTION, PERI_MAX_PITCH_CORRECTION);
                        if (Math.Abs(apoOvershoot) > APOAPSIS_TARGET_MARGIN)
                        {
                            double gain = needMajorPeri ? MAJOR_APOAPSIS_CORRECTION_GAIN : APOAPSIS_OVER_CORRECTION_GAIN;
                            float limit = needMajorPeri ? MAJOR_APOAPSIS_MAX_PITCH_CORRECTION : APO_MAX_PITCH_CORRECTION;
                            float corr = Mathf.Clamp((float)(Math.Abs(apoOvershoot) / gain), 0f, limit);
                            targetPitchCirc += apoOvershoot > 0 ? -corr : corr;
                        }
                        if (!needMajorPeri && apoOvershoot <= APO_THROTTLE_REDUCTION_START) targetPitchCirc += CIRCULARIZE_PITCH_UP_BIAS;
                        SetPitch(targetPitchCirc);
                        orientationReady = IsPitchSettled(targetPitchCirc);
                    }

                    double maxAccel2 = CalculateMaxAcceleration();
                    double rawTTA = GetTimeToApoapsis();
                    double tta2 = double.IsInfinity(rawTTA) || double.IsNaN(rawTTA) ? 2.0 : Math.Max(-5.0, Math.Min(20.0, rawTTA));
                    double hspd2 = GetHorizontalSpeed();
                    double tspd2 = CalculateTargetOrbitalSpeed(rocket.location.Value.Radius);
                    double periErr2 = targetRadius - periapsis;
                    bool needMajorPeri2 = periErr2 > Math.Max(MAJOR_PERIAPSIS_RAISE_MIN_ERROR, Math.Min(50000.0, targetAltitude * 0.05));
                    double apoAlt2 = apoapsisAltitude - altitude;
                    double distApo2 = Math.Abs(apoAlt2);
                    bool inBurnWindow = distApo2 <= CIRCULARIZE_BURN_WINDOW_DISTANCE || Math.Abs(rawTTA) <= CIRCULARIZE_BURN_WINDOW_TIME;
                    bool canPeriCorr = apoAlt2 <= CIRCULARIZE_PERI_CORRECTION_DISTANCE || rawTTA <= CIRCULARIZE_PERI_CORRECTION_LEAD_TIME;
                    float periThrottle2 = 0f;
                    if (inBurnWindow && (needMajorPeri2 || canPeriCorr) && periErr2 > PERIAPSIS_TOLERANCE)
                        periThrottle2 = Mathf.Clamp((float)(periErr2 / PERI_THROTTLE_GAIN), needMajorPeri2 ? 0.05f : PERI_THROTTLE_MIN, needMajorPeri2 ? 0.35f : 0.15f);

                    double dvErr2 = tspd2 - hspd2;
                    float circThrottle2 = 0f;
                    if (maxAccel2 > 0.00001 && dvErr2 > 0.0 && rawTTA > -1.0 && inBurnWindow)
                    {
                        circThrottle2 = Mathf.Clamp01((float)(dvErr2 / Math.Max(Math.Abs(rawTTA), 0.5) / maxAccel2));
                        double apoOver2 = apoapsis - targetRadius;
                        if (apoOver2 > APO_THROTTLE_REDUCTION_START)
                        {
                            double range = needMajorPeri2 ? Math.Max(5000.0, targetAltitude * 0.05) : (APO_THROTTLE_REDUCTION_END - APO_THROTTLE_REDUCTION_START);
                            float fac = (float)Math.Max(Math.Max(0.0, Math.Min(1.0, 1.0 - (apoOver2 - APO_THROTTLE_REDUCTION_START) / range)), needMajorPeri2 ? 0.35f : APO_MIN_THROTTLE_FACTOR);
                            circThrottle2 *= fac;
                        }
                    }

                    double closePeri = Math.Max(1500.0, Math.Min(5000.0, targetAltitude * 0.015));
                    double closeApo = Math.Max(2500.0, Math.Min(25000.0, targetAltitude * 0.03));
                    double apoOver3 = apoapsis - targetRadius;
                    float finalThrottle2 = 0f;
                    if (inBurnWindow)
                    {
                        finalThrottle2 = Mathf.Max(circThrottle2, periThrottle2);
                        if (!needMajorPeri2 && apoOver3 > APO_THROTTLE_REDUCTION_START && dvErr2 <= 0.0) finalThrottle2 = Mathf.Min(finalThrottle2, periThrottle2);
                        if (!needMajorPeri2 && apoOver3 > APO_THROTTLE_REDUCTION_END) finalThrottle2 = Mathf.Min(finalThrottle2, 0.02f);
                        if (needMajorPeri2 && periErr2 > Math.Max(closePeri, 2000.0)) finalThrottle2 = Mathf.Max(finalThrottle2, 0.05f);
                    }
                    else if (!needMajorPeri2 && dvErr2 > CIRCULARIZE_VEL_TOLERANCE && apoOver3 <= APO_THROTTLE_REDUCTION_START)
                        finalThrottle2 = Mathf.Min(circThrottle2, 0.05f);

                    if (WorldTime.main.worldTime < throttleUnlockWorldTime || !orientationReady) finalThrottle2 = 0f;

                    if (periErr2 > closePeri && finalThrottle2 <= 0.0f && rawTTA > CIRCULARIZE_RETRY_COAST_TIME)
                        UpdateTimewarpToApoapsisWindow(rawTTA, Math.Max(CIRCULARIZE_NEAR_APO_TIME, EstimateCircularizationBurnDuration(Math.Max(dvErr2, 0.0)) * 0.5 + 1.0), aboveAtmosphere);
                    else if (WorldTime.main.timewarpSpeed > 1) WorldTime.main.StopTimewarp(false);

                    SetThrottle(finalThrottle2);

                    double closePeri2 = Math.Max(3000.0, Math.Min(8000.0, targetAltitude * 0.02));
                    double closeApo2 = Math.Max(2500.0, Math.Min(25000.0, targetAltitude * 0.03));
                    bool orbitOK = Math.Abs(periapsis - targetRadius) < PERIAPSIS_TOLERANCE && Math.Abs(tspd2 - hspd2) <= CIRCULARIZE_VEL_TOLERANCE;
                    bool orbitClose = Math.Abs(periapsis - targetRadius) <= closePeri2 && Math.Abs(dvErr2) <= CIRCULARIZE_VEL_TOLERANCE && Math.Abs(apoapsis - targetRadius) <= closeApo2;
                    if (orbitOK || orbitClose) { CutEngines(); if (pendingTransferBurn) BeginTransferBurn(); else Stop(); }
                    else if (WorldTime.main.worldTime - circularizeEntryWorldTime > CIRCULARIZE_MAX_DURATION) { CutEngines(); Stop(); }
                    break;

                case AscentState.TransferBurn:
                    float tpTransfer = GetHorizontalAngle();
                    if (rocket.location.velocity.Value.magnitude > 0.1)
                        tpTransfer = Mathf.LerpAngle((float)Math.Atan2(rocket.location.velocity.Value.y, rocket.location.velocity.Value.x) * Mathf.Rad2Deg, tpTransfer, 0.25f);
                    SetPitch(tpTransfer);
                    double apoErrT = targetRadius - apoapsis;
                    float throttleT = 0f;
                    if (apoErrT > 0.0)
                    {
                        throttleT = CalculateThrottleDynamic(apoapsis, targetRadius);
                        if (apoErrT < 5000.0) throttleT = Mathf.Min(throttleT, 0.25f);
                        if (apoErrT < 2000.0) throttleT = Mathf.Min(throttleT, 0.1f);
                    }
                    if (WorldTime.main.worldTime < throttleUnlockWorldTime || !IsPitchSettled(tpTransfer)) throttleT = 0f;
                    SetThrottle(throttleT);
                    if (apoapsis >= targetRadius - APOAPSIS_TARGET_MARGIN) { CutEngines(); state = AscentState.Coast; coastLogCounter = 0; }
                    break;
            }
        }

        private void InitializeEngines()
        {
            if (currentStage == null || !rocket.staging.stages.Contains(currentStage)) return;
            foreach (var part in currentStage.parts)
                foreach (var engine in part.GetModules<EngineModule>())
                {
                    if (!engine.engineOn.Value) engine.engineOn.Value = true;
                    if (engine.hasGimbal && engine.gimbalOn != null && !engine.gimbalOn.Value) engine.gimbalOn.Value = true;
                }
        }

        private void SetThrottle(float percent)
        {
            float v = Mathf.Clamp01(percent);
            if (v < THROTTLE_SNAP_THRESHOLD) v = 0f;
            rocket.throttle.throttlePercent.Value = v;
            rocket.throttle.throttleOn.Value = v > 0f;
        }

        private float NormalizeAngle(float a) { float m = (a + 180f) % 360f; if (m < 0) m += 360f; return m - 180f; }

        private bool IsPitchSettled(float target) =>
            Mathf.Abs(NormalizeAngle(target - rocket.GetRotation())) <= GUIDED_BURN_PITCH_TOLERANCE &&
            Math.Abs(rocket.rb2d.angularVelocity) <= GUIDED_BURN_ANGULAR_VELOCITY_TOLERANCE;

        private void SetPitch(float targetDeg)
        {
            float err = NormalizeAngle(targetDeg - rocket.GetRotation());
            float av = rocket.rb2d.angularVelocity;
            float turn;
            try
            {
                var method = typeof(Rocket).GetMethod("GetTorque", BindingFlags.NonPublic | BindingFlags.Instance);
                float torque = (float)method.Invoke(rocket, null);
                float mass = rocket.rb2d.mass;
                if (mass > 200f) torque /= Mathf.Pow(mass / 200f, 0.35f);
                float maxAcc = torque * Mathf.Rad2Deg / mass;
                float stopTime = Mathf.Abs(av / maxAcc);
                float timeToTarget = Mathf.Abs(av) > 0.001f ? Mathf.Abs(err / av) : float.PositiveInfinity;
                turn = float.IsInfinity(timeToTarget) || stopTime > timeToTarget ? Mathf.Sign(av) : -Mathf.Sign(err);
            }
            catch { turn = (-err * 0.8f - av * 0.5f) / 30f; }
            rocket.arrowkeys.turnAxis.Value = Mathf.Clamp(turn, -1f, 1f);
        }

        private void CutEngines() => SetThrottle(0f);

        private float CalculateThrottleDynamic(double cur, double tgt)
        {
            if (cur <= 0) return 1f;
            double d = tgt - cur;
            if (d <= 0) return 0f;
            if (d > 20000) return 1f; if (d > 10000) return 0.85f; if (d > 5000) return 0.65f; if (d > 2000) return 0.4f; return 0.25f;
        }

        private void InitializeMissionTargets()
        {
            requestedTargetAltitude = Mathf.Max(MIN_TARGET_ALTITUDE, Settings.data.targetOrbitAltitude);
            targetAltitude = GetInitialInsertionTargetAltitude(requestedTargetAltitude);
            pendingTransferBurn = requestedTargetAltitude > targetAltitude + 0.1f;
            MsgDrawer.main.Log(pendingTransferBurn
                ? $"Autopilot target: {requestedTargetAltitude / 1000f:0.#} km via {targetAltitude / 1000f:0.#} km parking orbit."
                : $"Autopilot target: {targetAltitude / 1000f:0.#} km.");
        }

        private float GetInitialInsertionTargetAltitude(float req)
        {
            float park = GetParkingOrbitAltitude();
            return req > park + DIRECT_ASCENT_MAX_EXTRA_ALTITUDE ? park : req;
        }

        private float GetParkingOrbitAltitude() =>
            Math.Max(PARKING_ORBIT_MIN_ALTITUDE, (float)Math.Max(atmosphereHeight + PARKING_ORBIT_ATMOSPHERE_MARGIN, TURN_MIN_END_ALTITUDE));

        private void BeginTransferBurn()
        {
            targetAltitude = requestedTargetAltitude;
            targetRadius = targetAltitude + rocket.location.planet.Value.Radius;
            pendingTransferBurn = false;
            state = AscentState.TransferBurn;
            throttleUnlockWorldTime = WorldTime.main.worldTime + GUIDED_BURN_SETTLE_TIME;
            circularizeEntryWorldTime = WorldTime.main.worldTime;
            CutEngines();
        }

        private double GetPitchProgramEndAltitude()
        {
            double desired = atmosphereHeight > 1.0
                ? Math.Min(targetAltitude * TURN_TARGET_ALTITUDE_FACTOR, atmosphereHeight * TURN_ATMOSPHERE_ALTITUDE_FACTOR)
                : targetAltitude * TURN_NO_ATMOSPHERE_ALTITUDE_FACTOR;
            desired = Math.Max(actualTurnStartAltitude + TURN_MIN_END_ALTITUDE, desired);
            return Math.Max(actualTurnStartAltitude + 1.0, Math.Min(desired, Math.Max(actualTurnStartAltitude + 1000.0, targetAltitude - 2000.0)));
        }

        private double EstimateCircularizationBurnDuration(double dv)
        {
            if (dv <= 0.0) return 0.0;
            double acc = CalculateMaxAcceleration();
            if (acc <= 0.00001) { double m = rocket.mass.GetMass(); if (m > 0) acc = CalculateMaxThrust() / m; }
            return acc > 0.00001 ? dv / acc : 0.0;
        }

        private void UpdateTimewarpToApoapsisWindow(double tta, double lead, bool aboveAtmo)
        {
            if (!aboveAtmo || !WorldTime.CanTimewarp(false, false)) { if (WorldTime.main.timewarpSpeed > 1) WorldTime.main.StopTimewarp(false); return; }
            double stop = tta - lead;
            if (stop > 120) WorldTime.main.SetState(WorldTime.MaxTimewarpSpeed, false, false);
            else if (stop > 60) WorldTime.main.SetState(50, false, false);
            else if (stop > 30) WorldTime.main.SetState(25, false, false);
            else if (stop > 15) WorldTime.main.SetState(10, false, false);
            else if (stop > 8) WorldTime.main.SetState(5, false, false);
            else if (WorldTime.main.timewarpSpeed > 1) WorldTime.main.StopTimewarp(false);
        }

        private double CalculateTargetOrbitalSpeed(double radius)
        {
            double sr = Math.Max(radius, rocket.location.planet.Value.Radius + 1.0);
            double st = Math.Max(targetRadius, rocket.location.planet.Value.Radius + 1.0);
            double vv = 2.0 / sr - 1.0 / st;
            return vv <= 0.0 ? Math.Sqrt(rocket.location.planet.Value.mass / st) : Math.Sqrt(rocket.location.planet.Value.mass * vv);
        }

        public double GetHorizontalSpeed() => Double2.Dot(rocket.location.velocity.Value, GetHorizontalDirection());
        public float GetHorizontalAngle() => (float)(GetHorizontalDirection().AngleRadians * Mathf.Rad2Deg);

        private Double2 GetHorizontalDirection()
        {
            Double2 r = rocket.location.position.Value.normalized;
            if (r.sqrMagnitude < 1E-10) return Double2.right;
            return new Double2(-r.y, r.x) * GetOrbitalDirectionSign();
        }

        private double GetOrbitalDirectionSign() =>
            Double3.Cross(rocket.location.position.Value, rocket.location.velocity.Value).z < 0.0 ? -1.0 : 1.0;

        public double CalculateMaxThrust()
        {
            double t = 0;
            foreach (var e in rocket.partHolder.GetModules<EngineModule>()) if (e.engineOn.Value) t += e.thrust.Value;
            foreach (var b in rocket.partHolder.GetModules<BoosterModule>()) if (b.enabled) t += b.thrustVector.Value.magnitude;
            return t * 9.8;
        }

        public double CalculateMaxAcceleration()
        {
            double t = 0;
            foreach (var e in rocket.partHolder.GetModules<EngineModule>()) if (e.engineOn.Value) t += e.thrust.Value * 9.8;
            foreach (var b in rocket.partHolder.GetModules<BoosterModule>()) if (b.enabled) t += b.thrustVector.Value.magnitude * 9.8;
            double m = rocket.mass.GetMass();
            return m <= 0.00001 ? 0.0 : t / m;
        }

        private void CheckStaging()
        {
            if (currentStage == null || stagingAttempted.Contains(currentStage)) return;
            var engines = currentStage.parts.SelectMany(p => p.GetModules<EngineModule>()).Where(e => e != null).ToList();
            var detaches = currentStage.parts.SelectMany(p => p.GetModules<DetachModule>()).ToList();
            if (engines.Count == 0) { stagingAttempted.Add(currentStage); if (rocket.staging.stages.Contains(currentStage)) rocket.staging.RemoveStage(currentStage, false); return; }
            bool hasFuel = engines.Any(e => e.engineOn.Value && e.source.CanFlow(new MsgNone()));
            if (!hasFuel)
            {
                stagingAttempted.Add(currentStage);
                var parts = currentStage.parts.ToArray();
                Rocket.UseParts(true, parts.Select(p => new ValueTuple<Part, PolygonData>(p, null)).ToArray());
                if (detaches.Count > 0) { var sd = new UsePartData.SharedData(true); foreach (var d in detaches) d.Detach(new UsePartData(sd, null)); }
                if (rocket.staging.stages.Contains(currentStage)) rocket.staging.RemoveStage(currentStage, false);
            }
        }

        private double GetApoapsis() { var o = GetCurrentOrbit(); if (o == null) return 0; double a = o.apoapsis; return double.IsInfinity(a) || a > 1e9 ? double.PositiveInfinity : a; }
        private double GetPeriapsis() { var o = GetCurrentOrbit(); return o == null ? 0 : o.periapsis; }
        private double GetTimeToApoapsis() { var o = GetCurrentOrbit(); if (o == null) return double.PositiveInfinity; double now = WorldTime.main.worldTime; return o.GetNextTrueAnomalyPassTime(now, Math.PI) - now; }
        private Orbit GetCurrentOrbit() { if (rocket?.mapPlayer?.Trajectory == null) return null; var p = rocket.mapPlayer.Trajectory.paths; return p.Count == 0 ? null : p[0] as Orbit; }
    }
}
