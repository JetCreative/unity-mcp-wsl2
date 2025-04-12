using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Helper that keeps bridge metadata (registry files) accessible from WSL.
    /// Ensures ~/.unity-mcp inside WSL points at the Windows directory so the
    /// Python server can observe Unity status files.
    /// </summary>
    public static class InteropSyncHelper
    {
        private static DateTime _lastCheckUtc = DateTime.MinValue;
        private static bool _lastResult = true;
        private static string _lastMessage;

        /// <summary>
        /// Ensures that ~/.unity-mcp inside WSL resolves to the Windows registry directory.
        /// Returns true if the directory is already accessible or the linking succeeded.
        /// When false, warningMessage explains remediation steps for the user.
        /// </summary>
        public static bool TryEnsureRegistryLink(out string warningMessage)
        {
            warningMessage = null;

            if ((DateTime.UtcNow - _lastCheckUtc).TotalSeconds < 5)
            {
                warningMessage = _lastMessage;
                return _lastResult;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                McpSettings.SetRegistryOverride(null);
                Environment.SetEnvironmentVariable("UNITY_MCP_REGISTRY_DIR", null);
                CacheResult(true, null);
                return true;
            }

            if (!McpSettings.RunServerViaWsl)
            {
                McpSettings.SetRegistryOverride(null);
                Environment.SetEnvironmentVariable("UNITY_MCP_REGISTRY_DIR", null);
                CacheResult(true, null);
                return true;
            }

            if (!ServerInstaller.IsWslAvailableOnHost())
            {
                warningMessage = "WSL mode is enabled, but WSL is not available on this system.";
                CacheResult(false, warningMessage);
                return false;
            }

            string windowsRegistryDir = ServerInstaller.GetWindowsRegistryDirectory();
            try
            {
                Directory.CreateDirectory(windowsRegistryDir);
            }
            catch (Exception ex)
            {
                warningMessage = $"Failed to access Windows registry directory '{windowsRegistryDir}': {ex.Message}";
                CacheResult(false, warningMessage);
                return false;
            }

            if (!TryConvertWindowsPathToWsl(windowsRegistryDir, out string wslRegistryDir, out string conversionError))
            {
                warningMessage = conversionError;
                CacheResult(false, warningMessage);
                return false;
            }

            McpSettings.SetRegistryOverride(windowsRegistryDir);
            Environment.SetEnvironmentVariable("UNITY_MCP_REGISTRY_DIR", windowsRegistryDir);

            if (IsRegistryAccessibleInWsl())
            {
                CacheResult(true, null);
                return true;
            }

            if (TryCreateSymlink(wslRegistryDir, out string creationError))
            {
                bool success = IsRegistryAccessibleInWsl();
                CacheResult(success, success ? null : creationError);
                return success;
            }

            warningMessage = string.IsNullOrEmpty(creationError)
                ? $"Unable to expose Unity registry files inside WSL. Run:\n wsl.exe -- bash -lc \"ln -s '{wslRegistryDir}' ~/.unity-mcp\""
                : creationError;
            CacheResult(false, warningMessage);
            return false;
        }

        private static bool IsRegistryAccessibleInWsl()
        {
            int exitCode = RunWslCommand("if [ -d \"$HOME/.unity-mcp\" ] || [ -L \"$HOME/.unity-mcp\" ]; then exit 0; else exit 1; fi", 5000, out _, out _);
            return exitCode == 0;
        }

        private static bool TryCreateSymlink(string wslRegistryDir, out string error)
        {
            error = null;
            string escaped = EscapeForSingleQuotes(wslRegistryDir);
            string command = $"set -e; ln -s '{escaped}' \"$HOME/.unity-mcp\"";
            int exitCode = RunWslCommand(command, 10000, out _, out string stderr);
            if (exitCode == 0)
            {
                return true;
            }

            error = string.IsNullOrEmpty(stderr)
                ? "Failed to create ~/.unity-mcp link in WSL."
                : $"Failed to create ~/.unity-mcp link in WSL: {stderr.Trim()}";
            return false;
        }

        private static bool TryConvertWindowsPathToWsl(string windowsPath, out string wslPath, out string error)
        {
            wslPath = null;
            error = null;
            if (string.IsNullOrEmpty(windowsPath))
            {
                error = "Windows registry path is empty.";
                return false;
            }

            string escaped = windowsPath.Replace("\"", "\\\"");
            int exitCode = RunWslProcess($"-- wslpath -a \"{escaped}\"", 5000, out string stdout, out string stderr);
            if (exitCode != 0 || string.IsNullOrEmpty(stdout))
            {
                error = string.IsNullOrEmpty(stderr)
                    ? "wslpath failed to convert Windows registry path."
                    : $"wslpath error: {stderr.Trim()}";
                return false;
            }

            wslPath = stdout.Replace("\r", string.Empty).Trim();
            if (string.IsNullOrEmpty(wslPath))
            {
                error = "wslpath returned an empty path.";
                return false;
            }

            return true;
        }

        private static string EscapeForSingleQuotes(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("'", "'\\''");
        }

        private static int RunWslCommand(string command, int timeoutMs, out string stdout, out string stderr)
        {
            string escapedCommand = command.Replace("\"", "\\\"");
            return RunWslProcess($"-- bash -lc \"{escapedCommand}\"", timeoutMs, out stdout, out stderr);
        }

        private static int RunWslProcess(string arguments, int timeoutMs, out string stdout, out string stderr)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    stderr = "Failed to launch wsl.exe";
                    return -1;
                }

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    stderr = "wsl.exe timed out.";
                    return -1;
                }

                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return -1;
            }
        }

        private static void CacheResult(bool success, string warningMessage)
        {
            _lastCheckUtc = DateTime.UtcNow;
            _lastResult = success;
            _lastMessage = warningMessage;
        }
    }
}
