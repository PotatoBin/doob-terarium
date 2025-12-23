using System;
using UnityEngine;

/// <summary>
/// Helper functions for keeping secrets and endpoints out of source code.
/// - Prefer environment variables to avoid hardcoding sensitive values.
/// - Provides simple masking for safe logging.
/// </summary>
public static class SecretsUtility
{
    public static string ResolveSetting(string inspectorValue, string envVarName, string fallbackValue, string settingName, bool warnWhenMissing = true)
    {
        if (!string.IsNullOrWhiteSpace(inspectorValue))
            return inspectorValue.Trim();

        if (!string.IsNullOrWhiteSpace(envVarName))
        {
            string envValue = Environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrWhiteSpace(envValue))
                return envValue.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fallbackValue))
            return fallbackValue.Trim();

        if (warnWhenMissing)
            Debug.LogWarning($"[{settingName}] No value configured. Set via Inspector or env var '{envVarName}'.");

        return string.Empty;
    }

    public static string Mask(string value, int visibleTail = 4)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= visibleTail)
            return value;

        int maskedLength = Math.Max(0, value.Length - visibleTail);
        return new string('*', maskedLength) + value.Substring(value.Length - visibleTail);
    }
}
