using System.IO;
using KSP.UI.Screens;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private ApplicationLauncherButton toolbarButton;
        private ApplicationLauncher toolbarOwner;
        private Texture2D toolbarIcon;
        private bool toolbarOpen;

        private void InitializeToolbar()
        {
            toolbarOpen = settingsStore.PanelOpen;
            GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnToolbarDestroyed);
            AddToolbarButton();
        }

        private void AddToolbarButton()
        {
            ApplicationLauncher launcher = ApplicationLauncher.Instance;
            if (!ApplicationLauncher.Ready || launcher == null || toolbarButton != null) return;
            if (toolbarIcon == null) toolbarIcon = LoadToolbarIcon();
            if (toolbarIcon == null) return;
            toolbarOwner = launcher;
            toolbarButton = launcher.AddModApplication(OpenToolbarWindow, CloseToolbarWindow, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW, toolbarIcon);
            if (toolbarButton != null && toolbarOpen) toolbarButton.SetTrue(false);
        }

        private static Texture2D LoadToolbarIcon()
        {
            using (Stream stream = typeof(KspLanding).Assembly.GetManifestResourceStream("KspRetroLanding.ToolbarIcon.png"))
            {
                if (stream == null)
                {
                    Debug.LogError("[RetroLanding] The embedded toolbar icon is missing.");
                    return null;
                }
                using (var data = new MemoryStream())
                {
                    stream.CopyTo(data);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Retro Landing" };
                    if (ImageConversion.LoadImage(texture, data.ToArray(), true)) return texture;
                    Destroy(texture);
                    Debug.LogError("[RetroLanding] Cannot decode the embedded toolbar icon.");
                    return null;
                }
            }
        }

        private void OpenToolbarWindow() { SetToolbarOpen(true); }
        private void CloseToolbarWindow() { SetToolbarOpen(false); }

        private void SetToolbarOpen(bool open)
        {
            if (toolbarButton == null || toolbarOpen == open) return;
            toolbarOpen = open;
            settingsStore.SavePanelOpen(open);
        }

        private void OnToolbarDestroyed()
        {
            toolbarOwner = null;
            toolbarButton = null;
        }

        private void ReleaseToolbar()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnToolbarDestroyed);
            ApplicationLauncher owner = toolbarOwner;
            ApplicationLauncherButton button = toolbarButton;
            OnToolbarDestroyed();
            if (owner != null && button != null) owner.RemoveModApplication(button);
            if (toolbarIcon != null) Destroy(toolbarIcon);
            toolbarIcon = null;
        }
    }
}
