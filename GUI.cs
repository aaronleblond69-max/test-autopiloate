using System.Globalization;
using SFS.UI;
using SFS.UI.ModGUI;
using SFS.World;
using TMPro;
using UITools;
using UnityEngine;
using Type = SFS.UI.ModGUI.Type;

namespace RevolutionlessAutopilot
{
    public static class GUI
    {
        // Main window
        private static GameObject mainHolder;
        private static ClosableWindow mainWindow;
        private static readonly int mainWindowID = Builder.GetRandomID();
        private const string mainWindowPosKey = "RevolutionlessAutopilot.MainWindow";

        // Ascent window
        private static GameObject ascentHolder;
        private static ClosableWindow ascentWindow;
        private static readonly int ascentWindowID = Builder.GetRandomID();
        private const string ascentWindowPosKey = "RevolutionlessAutopilot.AscentWindow";

        // Landing window
        private static GameObject landingHolder;
        private static ClosableWindow landingWindow;
        private static readonly int landingWindowID = Builder.GetRandomID();
        private const string landingWindowPosKey = "RevolutionlessAutopilot.LandingWindow";

        // Rendezvous window
        private static GameObject rendezvousHolder;
        private static ClosableWindow rendezvousWindow;
        private static readonly int rendezvousWindowID = Builder.GetRandomID();
        private const string rendezvousWindowPosKey = "RevolutionlessAutopilot.RendezvousWindow";

        // Docking window
        private static GameObject dockingHolder;
        private static ClosableWindow dockingWindow;
        private static readonly int dockingWindowID = Builder.GetRandomID();
        private const string dockingWindowPosKey = "RevolutionlessAutopilot.DockingWindow";

        private const float minTargetOrbitKm = 1f;
        private const float fallbackOrbitMeters = 40000f;
        private static readonly float[] adjustStepsKm = { 1f, 10f, 100f, 1000f };

        private static TextInput targetAltitudeInput;
        private static string pendingTargetText;

        private static bool ascentVisible, landingVisible, rendezvousVisible, dockingVisible;

        public static void ShowGUI()
        {
            // --- Main window ---
            mainHolder = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "AutopilotMainHolder");
            mainWindow = UIToolsBuilder.CreateClosableWindow(mainHolder.transform, mainWindowID,
                300, 240, Settings.data.mainWindowPosition.x, Settings.data.mainWindowPosition.y,
                true, false, 0.95f, "Autopilot", false);
            mainWindow.RegisterPermanentSaving(mainWindowPosKey);
            mainWindow.CreateLayoutGroup(Type.Vertical, TextAnchor.MiddleCenter, 8f);
            Builder.CreateButton(mainWindow, 280, 36, 0, 0, ToggleAscentWindow, "Ascent Autopilot");
            Builder.CreateButton(mainWindow, 280, 36, 0, 0, ToggleLandingWindow, "Landing Autopilot");
            Builder.CreateButton(mainWindow, 280, 36, 0, 0, ToggleRendezvousWindow, "Rendezvous Autopilot");
            Builder.CreateButton(mainWindow, 280, 36, 0, 0, ToggleDockingWindow, "Docking Autopilot");
            mainWindow.gameObject.GetComponent<DraggableWindowModule>().OnDropAction += SaveMainWindowPos;

            // --- Ascent window ---
            ascentHolder = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "AutopilotAscentHolder");
            ascentWindow = UIToolsBuilder.CreateClosableWindow(ascentHolder.transform, ascentWindowID,
                380, 260, Settings.data.ascentWindowPosition.x, Settings.data.ascentWindowPosition.y,
                true, false, 0.95f, "Ascent Autopilot", false);
            ascentWindow.RegisterPermanentSaving(ascentWindowPosKey);
            ascentWindow.CreateLayoutGroup(Type.Vertical, TextAnchor.MiddleCenter, 5f);
            ascentWindow.Active = false;
            BuildAscentWindow();
            ascentWindow.gameObject.GetComponent<DraggableWindowModule>().OnDropAction += SaveAscentWindowPos;

            // --- Landing window ---
            landingHolder = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "AutopilotLandingHolder");
            landingWindow = UIToolsBuilder.CreateClosableWindow(landingHolder.transform, landingWindowID,
                300, 140, -400, 0, true, false, 0.95f, "Landing Autopilot", false);
            landingWindow.RegisterPermanentSaving(landingWindowPosKey);
            landingWindow.CreateLayoutGroup(Type.Vertical, TextAnchor.MiddleCenter, 8f);
            landingWindow.Active = false;
            Builder.CreateLabel(landingWindow, 280, 30, 0, 0, "Auto-deorbit & propulsive landing");
            Builder.CreateButton(landingWindow, 260, 40, 0, 0, () => AutopilotUpdater.Instance?.ToggleLanding(), "Start / Stop Landing");

            // --- Rendezvous window ---
            rendezvousHolder = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "AutopilotRendezvousHolder");
            rendezvousWindow = UIToolsBuilder.CreateClosableWindow(rendezvousHolder.transform, rendezvousWindowID,
                320, 180, -400, -150, true, false, 0.95f, "Rendezvous Autopilot", false);
            rendezvousWindow.RegisterPermanentSaving(rendezvousWindowPosKey);
            rendezvousWindow.CreateLayoutGroup(Type.Vertical, TextAnchor.MiddleCenter, 8f);
            rendezvousWindow.Active = false;
            Builder.CreateLabel(rendezvousWindow, 300, 30, 0, 0, "Target: nearest rocket in orbit");
            Builder.CreateButton(rendezvousWindow, 280, 36, 0, 0, SelectNearestTarget, "Select Nearest Target");
            Builder.CreateButton(rendezvousWindow, 280, 40, 0, 0, () => AutopilotUpdater.Instance?.ToggleRendezvous(), "Start / Stop Rendezvous");

            // --- Docking window ---
            dockingHolder = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "AutopilotDockingHolder");
            dockingWindow = UIToolsBuilder.CreateClosableWindow(dockingHolder.transform, dockingWindowID,
                320, 180, -400, -310, true, false, 0.95f, "Docking Autopilot", false);
            dockingWindow.RegisterPermanentSaving(dockingWindowPosKey);
            dockingWindow.CreateLayoutGroup(Type.Vertical, TextAnchor.MiddleCenter, 8f);
            dockingWindow.Active = false;
            Builder.CreateLabel(dockingWindow, 300, 30, 0, 0, "Fine approach & docking (< 200 m)");
            Builder.CreateButton(dockingWindow, 280, 36, 0, 0, SelectNearestDockingTarget, "Select Nearest Target");
            Builder.CreateButton(dockingWindow, 280, 40, 0, 0, () => AutopilotUpdater.Instance?.ToggleDocking(), "Start / Stop Docking");
        }

        public static void HideGUI()
        {
            if (mainHolder != null) Object.Destroy(mainHolder);
            if (ascentHolder != null) Object.Destroy(ascentHolder);
            if (landingHolder != null) Object.Destroy(landingHolder);
            if (rendezvousHolder != null) Object.Destroy(rendezvousHolder);
            if (dockingHolder != null) Object.Destroy(dockingHolder);
        }

        private static void ToggleAscentWindow()    { ascentVisible    = !ascentVisible;    ascentWindow.Active    = ascentVisible; }
        private static void ToggleLandingWindow()   { landingVisible   = !landingVisible;   landingWindow.Active   = landingVisible; }
        private static void ToggleRendezvousWindow(){ rendezvousVisible= !rendezvousVisible;rendezvousWindow.Active= rendezvousVisible; }
        private static void ToggleDockingWindow()   { dockingVisible   = !dockingVisible;   dockingWindow.Active   = dockingVisible; }

        private static void BuildAscentWindow()
        {
            var inputRow = Builder.CreateContainer(ascentWindow);
            inputRow.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleLeft, 5f);
            Builder.CreateLabel(inputRow, 160, 30, 0, 0, "Target orbit (km)");
            pendingTargetText = FormatKm(Settings.data.targetOrbitAltitude);
            targetAltitudeInput = Builder.CreateTextInput(inputRow, 160, 40, 0, 0, pendingTargetText);
            targetAltitudeInput.field.characterValidation = TMP_InputField.CharacterValidation.Decimal;
            targetAltitudeInput.field.onValueChanged.AddListener(OnValueChanged);
            targetAltitudeInput.field.onEndEdit.AddListener(OnEndEdit);

            // BUG FIX: added minus buttons alongside plus buttons
            var plusRow = Builder.CreateContainer(ascentWindow);
            plusRow.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, 3f);
            Builder.CreateLabel(plusRow, 25, 30, 0, 0, "+");
            foreach (float s in adjustStepsKm)
            { float cap = s; Builder.CreateButton(plusRow, 60, 32, 0, 0, () => AdjustKm(cap),  $"{cap:0}"); }

            var minusRow = Builder.CreateContainer(ascentWindow);
            minusRow.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, 3f);
            Builder.CreateLabel(minusRow, 25, 30, 0, 0, "-");
            foreach (float s in adjustStepsKm)
            { float cap = s; Builder.CreateButton(minusRow, 60, 32, 0, 0, () => AdjustKm(-cap), $"{cap:0}"); }

            Builder.CreateButton(ascentWindow, 220, 34, 0, 0, ResetAlt, "Reset (Atmo + 5 km)");
            Builder.CreateButton(ascentWindow, 220, 40, 0, 0, ToggleAscent, "Start / Stop Ascent");
        }

        private static void OnValueChanged(string v)
        {
            pendingTargetText = v;
            if (TryParseKm(v, out float km)) { Settings.data.targetOrbitAltitude = km * 1000f; Settings.Save(); }
        }

        private static void OnEndEdit(string v) { if (!TryCommit(false) && targetAltitudeInput != null) targetAltitudeInput.Text = FormatKm(Settings.data.targetOrbitAltitude); }

        private static void ToggleAscent() { if (!TryCommit(true)) return; AutopilotUpdater.Instance?.ToggleAscent(); }

        private static bool TryCommit(bool showErrors)
        {
            if (targetAltitudeInput == null) return true;
            string raw = !string.IsNullOrWhiteSpace(pendingTargetText) ? pendingTargetText : targetAltitudeInput.field?.text ?? targetAltitudeInput.Text;
            if (!TryParseKm(raw, out float km)) { if (showErrors) MsgDrawer.main.Log("Enter a valid orbit altitude."); return false; }
            Settings.data.targetOrbitAltitude = km * 1000f;
            Settings.Save();
            pendingTargetText = FormatKm(Settings.data.targetOrbitAltitude);
            if (targetAltitudeInput != null) targetAltitudeInput.Text = pendingTargetText;
            return true;
        }

        private static void SetKm(float km)
        {
            Settings.data.targetOrbitAltitude = Mathf.Max(minTargetOrbitKm, km) * 1000f;
            Settings.Save();
            pendingTargetText = FormatKm(Settings.data.targetOrbitAltitude);
            if (targetAltitudeInput != null) targetAltitudeInput.Text = pendingTargetText;
        }

        private static void AdjustKm(float delta)
        {
            float cur = Settings.data.targetOrbitAltitude / 1000f;
            if (TryParseKm(pendingTargetText, out float p)) cur = p;
            SetKm(cur + delta);
        }

        private static void ResetAlt()
        {
            float rec = AutopilotUpdater.Instance?.GetRecommendedLowOrbitAltitudeMeters() ?? fallbackOrbitMeters;
            SetKm(rec / 1000f);
        }

        private static void SelectNearestTarget()
        {
            var rocket = GetPlayerRocket();
            if (rocket == null) { MsgDrawer.main.Log("No rocket."); return; }
            var nearest = FindNearest(rocket);
            if (nearest == null) { MsgDrawer.main.Log("No other rocket found."); return; }
            AutopilotUpdater.Instance?.SetRendezvousTarget(nearest);
            MsgDrawer.main.Log($"Rendezvous target selected.");
        }

        private static void SelectNearestDockingTarget()
        {
            var rocket = GetPlayerRocket();
            if (rocket == null) { MsgDrawer.main.Log("No rocket."); return; }
            var nearest = FindNearest(rocket);
            if (nearest == null) { MsgDrawer.main.Log("No other rocket found."); return; }
            AutopilotUpdater.Instance?.SetDockingTarget(nearest);
            MsgDrawer.main.Log($"Docking target selected.");
        }

        private static Rocket GetPlayerRocket() =>
            PlayerController.main?.player?.Value is Rocket r ? r : null;

        private static Rocket FindNearest(Rocket self)
        {
            Rocket nearest = null;
            double minDist = double.MaxValue;
            foreach (var obj in WorldManager.main?.rockets ?? System.Array.Empty<Rocket>())
            {
                if (obj == self) continue;
                double d = (obj.location.position.Value - self.location.position.Value).magnitude;
                if (d < minDist) { minDist = d; nearest = obj; }
            }
            return nearest;
        }

        private static bool TryParseKm(string v, out float km)
        {
            km = 0f;
            if (string.IsNullOrWhiteSpace(v)) return false;
            var s = NumberStyles.Float | NumberStyles.AllowThousands;
            if (!float.TryParse(v, s, CultureInfo.CurrentCulture, out km) &&
                !float.TryParse(v, s, CultureInfo.InvariantCulture, out km) &&
                !float.TryParse(v.Replace(',', '.'), s, CultureInfo.InvariantCulture, out km)) return false;
            km = Mathf.Max(minTargetOrbitKm, km);
            return true;
        }

        private static string FormatKm(float meters) => (meters / 1000f).ToString("0.0", CultureInfo.InvariantCulture);

        private static void SaveMainWindowPos() { Settings.data.mainWindowPosition = Vector2Int.RoundToInt(mainWindow.Position); Settings.Save(); }
        private static void SaveAscentWindowPos() { Settings.data.ascentWindowPosition = Vector2Int.RoundToInt(ascentWindow.Position); Settings.Save(); }
    }
}
