using System;
using System.IO;

namespace StarBridge.Desktop;

internal static class OnboardingState
{
    private const string CurrentGuideVersion = "starbridge-onboarding-v1";
    private static readonly string CompletionPath = Path.Combine(
        DesktopAppConfig.ConfigDirectory,
        "onboarding.complete");

    public static bool IsCompleted()
    {
        try
        {
            return File.Exists(CompletionPath) &&
                   string.Equals(
                       File.ReadAllText(CompletionPath).Trim(),
                       CurrentGuideVersion,
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void MarkCompleted()
    {
        try
        {
            Directory.CreateDirectory(DesktopAppConfig.ConfigDirectory);
            File.WriteAllText(CompletionPath, CurrentGuideVersion);
        }
        catch
        {
            // The guide is optional; failure to save the flag must not block startup.
        }
    }

    public static bool IsHintCompleted(string hintId)
    {
        try
        {
            return File.Exists(GetHintPath(hintId));
        }
        catch
        {
            return false;
        }
    }

    public static void MarkHintCompleted(string hintId)
    {
        try
        {
            Directory.CreateDirectory(DesktopAppConfig.ConfigDirectory);
            File.WriteAllText(GetHintPath(hintId), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Context hints are optional and should never block interaction.
        }
    }

    private static string GetHintPath(string hintId)
    {
        var safeHintId = hintId.Replace('.', '-').Replace('/', '-').Replace('\\', '-');
        return Path.Combine(DesktopAppConfig.ConfigDirectory, $"guide-{safeHintId}.complete");
    }
}
