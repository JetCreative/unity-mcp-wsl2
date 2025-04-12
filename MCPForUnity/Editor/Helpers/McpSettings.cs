using UnityEditor;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Centralized access to persistent MCP editor settings.
    /// </summary>
    public static class McpSettings
    {
        private const string RunViaWslKey = "MCPForUnity.RunServerViaWsl";
        private const string WslServerLinuxPathKey = "MCPForUnity.WslServerSrc";
        private const string RegistryOverrideKey = "MCPForUnity.RegistryOverride";

        /// <summary>
        /// Gets or sets whether the MCP server should be launched through WSL.
        /// </summary>
        public static bool RunServerViaWsl
        {
            get
            {
                try { return EditorPrefs.GetBool(RunViaWslKey, false); }
                catch { return false; }
            }
            set
            {
                try { EditorPrefs.SetBool(RunViaWslKey, value); }
                catch { }
            }
        }

        /// <summary>
        /// Persists the Linux path where the server is installed when WSL mode is used.
        /// </summary>
        public static void SetWslServerLinuxPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    EditorPrefs.DeleteKey(WslServerLinuxPathKey);
                }
                else
                {
                    EditorPrefs.SetString(WslServerLinuxPathKey, path);
                }
            }
            catch { }
        }

        /// <summary>
        /// Returns the stored Linux path for the WSL server, or null if not set.
        /// </summary>
        public static string GetWslServerLinuxPath()
        {
            try { return EditorPrefs.GetString(WslServerLinuxPathKey, null); }
            catch { return null; }
        }

        /// <summary>
        /// Records the Windows-visible registry directory override so we can relay it to WSL processes.
        /// </summary>
        public static void SetRegistryOverride(string windowsPath)
        {
            try
            {
                if (string.IsNullOrEmpty(windowsPath))
                {
                    EditorPrefs.DeleteKey(RegistryOverrideKey);
                }
                else
                {
                    EditorPrefs.SetString(RegistryOverrideKey, windowsPath);
                }
            }
            catch { }
        }

        /// <summary>
        /// Returns the persisted registry override path (Windows side), if any.
        /// </summary>
        public static string GetRegistryOverride()
        {
            try { return EditorPrefs.GetString(RegistryOverrideKey, null); }
            catch { return null; }
        }
    }
}
