using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GordonWorker.Models;

namespace GordonWorker.Services
{
    public class ClaudeCliService
    {
        private readonly ILogger<ClaudeCliService> _logger;
        private static List<string> _cachedModels = new();
        private static DateTime _lastModelFetch = DateTime.MinValue;
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public ClaudeCliService(ILogger<ClaudeCliService> logger)
        {
            _logger = logger;
        }

        public async Task<string> AskClaudeAsync(string prompt, string? model = null, string? token = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var escapedPrompt = prompt.Replace("\"", "\\\"");
                var arguments = $"-p \"{escapedPrompt}\"";
                
                if (!string.IsNullOrEmpty(model))
                {
                    // Ensure we use the alias if it's a known family name to prevent deprecation issues
                    var cleanModel = model.ToLower();
                    if (cleanModel.Contains("sonnet")) cleanModel = "sonnet";
                    else if (cleanModel.Contains("haiku")) cleanModel = "haiku";
                    else if (cleanModel.Contains("opus")) cleanModel = "opus";
                    
                    arguments += $" --model {cleanModel}";
                }

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (!string.IsNullOrWhiteSpace(token))
                {
                    processStartInfo.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] = token;
                }

                using var process = new Process { StartInfo = processStartInfo };
                
                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancellationToken);

                var stdout = stdoutBuilder.ToString();
                var stderr = stderrBuilder.ToString();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("Claude CLI failed with exit code {ExitCode}. Error: {Error}. Output: {Output}", process.ExitCode, stderr, stdout);
                    
                    // If the error message mentions deprecation, we should probably warn specifically
                    if (stderr.Contains("deprecated") || stdout.Contains("deprecated"))
                    {
                        throw new Exception("The selected Claude model is deprecated. Gordon has been updated to automatically use the 'sonnet' alias to fix this. Please try again.");
                    }

                    throw new Exception($"Claude CLI failed: {stderr} {stdout}");
                }

                return CleanCliOutput(stdout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing Claude CLI.");
                throw;
            }
        }

        private string CleanCliOutput(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input ?? string.Empty;

            // Remove ANSI escape codes
            string cleaned = Regex.Replace(input, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");
            
            // Remove typical CLI "thinking" or "working" lines if they leaked into stdout
            cleaned = Regex.Replace(cleaned, @"^.*working.*$\n?", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            
            return cleaned.Trim();
        }
        
        public async Task<List<string>> GetAvailableModelsAsync(string? token = null, CancellationToken ct = default)
        {
            if (DateTime.UtcNow - _lastModelFetch < TimeSpan.FromHours(24) && _cachedModels.Any())
            {
                return _cachedModels;
            }

            await _lock.WaitAsync(ct);
            try
            {
                if (DateTime.UtcNow - _lastModelFetch < TimeSpan.FromHours(24) && _cachedModels.Any())
                {
                    return _cachedModels;
                }

                _logger.LogInformation("Polling Claude CLI for latest models...");
                
                // Use a prompt that is very likely to return valid identifiers
                var prompt = "List the exact model identifiers available for the --model flag, one per line. No conversational text, no markdown, just the strings (e.g. claude-3-5-sonnet-latest).";
                
                // We run without a model flag to avoid the deprecation trap
                var result = await AskClaudeAsync(prompt, null, token, ct);

                var models = result.Split(new[] { '\r', '\n', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m.Trim().ToLower())
                    .Where(m => m.StartsWith("claude-") || m == "sonnet" || m == "opus" || m == "haiku")
                    .Distinct()
                    .ToList();

                if (!models.Any())
                {
                    models = new List<string> { "sonnet", "haiku", "opus", "claude-3-5-sonnet-latest", "claude-3-5-haiku-latest" };
                }

                _cachedModels = models;
                _lastModelFetch = DateTime.UtcNow;
                return _cachedModels;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch models from Claude CLI. Using safe aliases.");
                return new List<string> { "sonnet", "haiku", "opus", "claude-3-5-sonnet-latest", "claude-3-5-haiku-latest" };
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}