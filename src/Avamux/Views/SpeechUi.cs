using Avalonia.Controls;
using Avamux.Core.Ai;

namespace Avamux.Views;

/// <summary>
/// Speaking from the window. Kokoro is off until wanted: the first time something is to be spoken, this asks to turn
/// it on (or says what is missing when it is not set up), and does nothing if the answer is no.
/// </summary>
public static class SpeechUi
{
    private static SpeechService Speech => App.Services.Speech;

    /// <summary>True when Kokoro is on (switched on just now, if the user said so).</summary>
    public static async Task<bool> EnsureOnAsync(Control from)
    {
        if (Speech.Enabled && Speech.Configured) return true;
        var owner = Ui.Owner(from);
        if (!Speech.Configured)
        {
            var missing = string.Join("\n", Speech.Problems().Select(p => "• " + p));
            if (await Ui.ConfirmAsync(owner, "Kokoro is not set up",
                    "Speaking uses Kokoro, which is not set up yet:\n\n" + missing + "\n\nSet it up under Settings → AI Chat (the ai.tts settings).", "Open Settings"))
            {
                Ui.Main?.Navigate("settings");
                Ui.Main?.Page<SettingsPage>("settings")?.Find("ai.tts");
            }
            return false;
        }
        var idle = App.Services.Config.Settings.Ai.Tts.IdleMinutes;
        if (!await Ui.ConfirmAsync(owner, "Turn on Kokoro?",
                "Speaking uses Kokoro, which is off. Turn it on? It loads its model now" + (idle > 0 ? $" and unloads it after {idle:0.#} minutes without speaking" : "") +
                ". Switching it off under Settings → AI Chat stops it.", "Turn On"))
            return false;
        Ui.Try(from, () => App.Services.Config.Set("ai.tts.enabled", System.Text.Json.Nodes.JsonValue.Create(true)), "Could not switch Kokoro on");
        return Speech.Enabled;
    }

    /// <summary>Speaks text, or stops if it is what is being spoken (the same <paramref name="tag"/>).</summary>
    public static async Task ToggleAsync(Control from, string text, string tag)
    {
        if (Speech.State == SpeechState.Speaking && Speech.SpeakingTag == tag) { Speech.Stop(); return; }
        if (!await EnsureOnAsync(from)) return;
        var error = await Speech.SpeakAsync(text, tag);
        if (error is not null) Ui.Toast(from, "Could not speak", error);
    }
}
