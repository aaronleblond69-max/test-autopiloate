using SFS.UI;
using SFS.World;
using UnityEngine;

namespace RevolutionlessAutopilot
{
    public class AutopilotUpdater : MonoBehaviour
    {
        public static AutopilotUpdater Instance { get; private set; }

        private Rocket currentRocket;
        private AscentAutopilot ascentAutopilot;
        private LandingAutopilot landingAutopilot;
        private RendezvousAutopilot rendezvousAutopilot;
        private DockingAutopilot dockingAutopilot;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (PlayerController.main?.player?.Value is Rocket rocket)
            {
                currentRocket = rocket;
            }
            else
            {
                currentRocket = null;
                StopAll();
            }
        }

        private void FixedUpdate()
        {
            if (currentRocket == null) return;
            if (ascentAutopilot != null && ascentAutopilot.IsActive) ascentAutopilot.FixedUpdate();
            if (landingAutopilot != null && landingAutopilot.IsActive) landingAutopilot.FixedUpdate();
            if (rendezvousAutopilot != null && rendezvousAutopilot.IsActive) rendezvousAutopilot.FixedUpdate();
            if (dockingAutopilot != null && dockingAutopilot.IsActive) dockingAutopilot.FixedUpdate();
        }

        private void StopAll()
        {
            if (ascentAutopilot != null && ascentAutopilot.IsActive) ascentAutopilot.Stop();
            if (landingAutopilot != null && landingAutopilot.IsActive) landingAutopilot.Stop();
            if (rendezvousAutopilot != null && rendezvousAutopilot.IsActive) rendezvousAutopilot.Stop();
            if (dockingAutopilot != null && dockingAutopilot.IsActive) dockingAutopilot.Stop();
        }

        public void ToggleAscent()
        {
            if (currentRocket == null) { MsgDrawer.main.Log("No rocket controlled."); return; }
            StopOthers("ascent");
            if (ascentAutopilot == null) ascentAutopilot = new AscentAutopilot(currentRocket);
            else ascentAutopilot.SetRocket(currentRocket);
            if (ascentAutopilot.IsActive) { ascentAutopilot.Stop(); MsgDrawer.main.Log("Ascent autopilot stopped."); }
            else { ascentAutopilot.Start(); MsgDrawer.main.Log("Ascent autopilot started."); }
        }

        public void ToggleLanding()
        {
            if (currentRocket == null) { MsgDrawer.main.Log("No rocket controlled."); return; }
            StopOthers("landing");
            if (landingAutopilot == null) landingAutopilot = new LandingAutopilot(currentRocket);
            else landingAutopilot.SetRocket(currentRocket);
            if (landingAutopilot.IsActive) { landingAutopilot.Stop(); MsgDrawer.main.Log("Landing autopilot stopped."); }
            else { landingAutopilot.Start(); }
        }

        public void ToggleRendezvous()
        {
            if (currentRocket == null) { MsgDrawer.main.Log("No rocket controlled."); return; }
            StopOthers("rendezvous");
            if (rendezvousAutopilot == null) rendezvousAutopilot = new RendezvousAutopilot(currentRocket);
            else rendezvousAutopilot.SetRocket(currentRocket);
            if (rendezvousAutopilot.IsActive) { rendezvousAutopilot.Stop(); }
            else { rendezvousAutopilot.Start(); }
        }

        public void ToggleDocking()
        {
            if (currentRocket == null) { MsgDrawer.main.Log("No rocket controlled."); return; }
            StopOthers("docking");
            if (dockingAutopilot == null) dockingAutopilot = new DockingAutopilot(currentRocket);
            else dockingAutopilot.SetRocket(currentRocket);
            if (dockingAutopilot.IsActive) { dockingAutopilot.Stop(); }
            else { dockingAutopilot.Start(); }
        }

        public void SetRendezvousTarget(Rocket t)
        {
            if (rendezvousAutopilot == null) rendezvousAutopilot = new RendezvousAutopilot(currentRocket);
            rendezvousAutopilot.SetTarget(t);
        }

        public void SetDockingTarget(Rocket t)
        {
            if (dockingAutopilot == null) dockingAutopilot = new DockingAutopilot(currentRocket);
            dockingAutopilot.SetTarget(t);
        }

        private void StopOthers(string keep)
        {
            if (keep != "ascent" && ascentAutopilot != null && ascentAutopilot.IsActive) ascentAutopilot.Stop();
            if (keep != "landing" && landingAutopilot != null && landingAutopilot.IsActive) landingAutopilot.Stop();
            if (keep != "rendezvous" && rendezvousAutopilot != null && rendezvousAutopilot.IsActive) rendezvousAutopilot.Stop();
            if (keep != "docking" && dockingAutopilot != null && dockingAutopilot.IsActive) dockingAutopilot.Stop();
        }

        public bool IsAscentActive => ascentAutopilot != null && ascentAutopilot.IsActive;
        public bool IsLandingActive => landingAutopilot != null && landingAutopilot.IsActive;
        public bool IsRendezvousActive => rendezvousAutopilot != null && rendezvousAutopilot.IsActive;
        public bool IsDockingActive => dockingAutopilot != null && dockingAutopilot.IsActive;

        public float GetRecommendedLowOrbitAltitudeMeters()
        {
            if (currentRocket?.location?.planet?.Value == null) return 40000f;
            return Mathf.Max(5000f, (float)currentRocket.location.planet.Value.AtmosphereHeightPhysics + 5000f);
        }
    }
}
