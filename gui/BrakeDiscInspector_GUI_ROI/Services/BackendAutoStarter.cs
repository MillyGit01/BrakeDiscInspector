using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Services
{
    internal static class BackendAutoStarter
    {
        private static readonly SemaphoreSlim StartLock = new(1, 1);

        public static async Task EnsureStartedAsync(AppConfig.BackendConfig config, CancellationToken ct = default)
        {
            if (config == null || !config.AutoStart)
            {
                return;
            }

            await StartLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await IsHealthyAsync(config.BaseUrl, ct).ConfigureAwait(false))
                {
                    GuiLog.Info("[backend-autostart] backend already healthy; startup skipped.");
                    return;
                }

                var mode = string.IsNullOrWhiteSpace(config.AutoStartMode) ? "WslVenv" : config.AutoStartMode.Trim();
                if (!IsSupportedWslMode(mode))
                {
                    GuiLog.Warn($"[backend-autostart] unsupported mode '{config.AutoStartMode}'.");
                    return;
                }

                var repoRoot = ResolveRepoRoot(config.AutoStartWorkingDirectory);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    GuiLog.Warn("[backend-autostart] repository root not found; cannot start backend.");
                    return;
                }

                StartWslBackend(config, repoRoot, mode);

                var timeout = TimeSpan.FromSeconds(Math.Clamp(config.StartupTimeoutSeconds, 1, 300));
                if (await WaitUntilHealthyAsync(config.BaseUrl, timeout, ct).ConfigureAwait(false))
                {
                    GuiLog.Info("[backend-autostart] backend became healthy.");
                }
                else
                {
                    GuiLog.Warn($"[backend-autostart] backend did not answer /health within {timeout.TotalSeconds:0}s.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                GuiLog.Error("[backend-autostart] startup failed", ex);
            }
            finally
            {
                StartLock.Release();
            }
        }

        private static void StartWslBackend(AppConfig.BackendConfig config, string repoRoot, string mode)
        {
            var distro = string.IsNullOrWhiteSpace(config.WslDistro) ? "Ubuntu" : config.WslDistro.Trim();
            var envName = string.IsNullOrWhiteSpace(config.WslCondaEnvironment) ? "BrakeDisc" : config.WslCondaEnvironment.Trim();
            var venvPath = string.IsNullOrWhiteSpace(config.WslVenvPath) ? "/home/millylinux/venv" : config.WslVenvPath.Trim();
            var modelsDir = ResolveWslPath(config.AutoStartModelsDirectory);
            var host = string.IsNullOrWhiteSpace(config.AutoStartHost) ? "0.0.0.0" : config.AutoStartHost.Trim();
            var port = config.AutoStartPort > 0 ? config.AutoStartPort : TryGetPort(config.BaseUrl) ?? 8000;
            var wslRepoRoot = ToWslPath(repoRoot);

            var bashCommand =
                "set -e; " +
                "cd " + BashQuote(wslRepoRoot) + "; " +
                "source ~/.bashrc >/dev/null 2>&1 || true; " +
                BuildActivationCommand(mode, envName, venvPath) +
                BuildModelsDirectoryCommand(modelsDir) +
                "echo '[bdi] Backend starting from:' \"$(pwd)\"; " +
                "echo '[bdi] Python:' \"$(command -v python || true)\"; " +
                "if ! command -v python >/dev/null 2>&1; then echo '[bdi] python not found after environment activation.' >&2; exit 1; fi; " +
                "python -m uvicorn backend.app:app --host " + BashQuote(host) + " --port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var startInfo = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(distro);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("bash");
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(bashCommand);

            GuiLog.Info($"[backend-autostart] launching WSL distro='{distro}' mode='{mode}' condaEnv='{envName}' venv='{venvPath}' models='{modelsDir ?? "<default>"}' repo='{repoRoot}' port={port}.");
            Process.Start(startInfo);
        }

        private static bool IsSupportedWslMode(string mode)
            => string.Equals(mode, "WslVenv", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "WslConda", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "WslAuto", StringComparison.OrdinalIgnoreCase);

        private static string BuildActivationCommand(string mode, string envName, string venvPath)
        {
            if (string.Equals(mode, "WslVenv", StringComparison.OrdinalIgnoreCase))
            {
                return "if [ -f " + BashQuote(venvPath + "/bin/activate") + " ]; then " +
                       ". " + BashQuote(venvPath + "/bin/activate") + "; " +
                       "echo '[bdi] Python venv:' " + BashQuote(venvPath) + "; " +
                       "else echo '[bdi] Python venv not found: " + BashEscapeForSingleQuotedEcho(venvPath) + "' >&2; exit 1; fi; ";
            }

            var condaSetup =
                "if command -v conda >/dev/null 2>&1; then eval \"$(conda shell.bash hook)\"; " +
                "elif [ -f \"$HOME/miniconda3/etc/profile.d/conda.sh\" ]; then . \"$HOME/miniconda3/etc/profile.d/conda.sh\"; " +
                "elif [ -f \"$HOME/anaconda3/etc/profile.d/conda.sh\" ]; then . \"$HOME/anaconda3/etc/profile.d/conda.sh\"; fi; ";

            if (string.Equals(mode, "WslAuto", StringComparison.OrdinalIgnoreCase))
            {
                return "if [ -f " + BashQuote(venvPath + "/bin/activate") + " ]; then " +
                       ". " + BashQuote(venvPath + "/bin/activate") + "; " +
                       "echo '[bdi] Python venv:' " + BashQuote(venvPath) + "; " +
                       "else " + condaSetup +
                       "if command -v conda >/dev/null 2>&1; then conda activate " + BashQuote(envName) + "; echo '[bdi] Conda env:' \"$CONDA_DEFAULT_ENV\"; " +
                       "else echo '[bdi] neither venv nor conda is available.' >&2; exit 1; fi; fi; ";
            }

            return condaSetup +
                   "if command -v conda >/dev/null 2>&1; then conda activate " + BashQuote(envName) + "; echo '[bdi] Conda env:' \"$CONDA_DEFAULT_ENV\"; " +
                   "elif [ -f " + BashQuote(venvPath + "/bin/activate") + " ]; then echo '[bdi] conda not found; falling back to venv:' " + BashQuote(venvPath) + "; . " + BashQuote(venvPath + "/bin/activate") + "; " +
                   "else echo '[bdi] conda not found and fallback venv missing: " + BashEscapeForSingleQuotedEcho(venvPath) + "' >&2; exit 1; fi; ";
        }

        private static string BuildModelsDirectoryCommand(string? modelsDir)
        {
            if (string.IsNullOrWhiteSpace(modelsDir))
            {
                return string.Empty;
            }

            return "export BDI_MODELS_DIR=" + BashQuote(modelsDir) + "; " +
                   "echo '[bdi] BDI_MODELS_DIR:' \"$BDI_MODELS_DIR\"; ";
        }

        private static async Task<bool> WaitUntilHealthyAsync(string baseUrl, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await IsHealthyAsync(baseUrl, ct).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }

            return false;
        }

        private static async Task<bool> IsHealthyAsync(string baseUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return false;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var url = baseUrl.TrimEnd('/') + "/health";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static string? ResolveRepoRoot(string? configuredWorkingDirectory)
        {
            if (!string.IsNullOrWhiteSpace(configuredWorkingDirectory))
            {
                var expanded = Environment.ExpandEnvironmentVariables(configuredWorkingDirectory.Trim());
                if (expanded.StartsWith("/", StringComparison.Ordinal))
                {
                    return expanded;
                }

                if (Directory.Exists(expanded))
                {
                    return Path.GetFullPath(expanded);
                }
            }

            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "backend", "app.py")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static string ToWslPath(string path)
        {
            if (path.StartsWith("/", StringComparison.Ordinal))
            {
                return path;
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(root) && root.Length >= 2 && root[1] == ':')
            {
                var drive = char.ToLowerInvariant(root[0]);
                var rest = fullPath[root.Length..].Replace('\\', '/');
                return "/mnt/" + drive + "/" + rest;
            }

            return fullPath.Replace('\\', '/');
        }

        private static string? ResolveWslPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return ToWslPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }

        private static string BashQuote(string value)
            => "'" + (value ?? string.Empty).Replace("'", "'\"'\"'") + "'";

        private static string BashEscapeForSingleQuotedEcho(string value)
            => (value ?? string.Empty).Replace("'", "'\"'\"'");

        private static int? TryGetPort(string baseUrl)
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }

            return null;
        }
    }
}
