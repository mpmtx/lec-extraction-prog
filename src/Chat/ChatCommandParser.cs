using System;
using System.Globalization;
using System.Linq;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] Turns a line typed at the chat prompt into a <see cref="ChatCommand"/>. Pure: no
/// console, no session state, no config - which is the entire point.
///
/// <para><b>Why this exists.</b> Each command used to be a private method that recognised itself with
/// <c>StartsWith("set thinking-budget ")</c> and then sliced the argument with a <i>hand-counted
/// index</i>: <c>normalizedInput[18..]</c>. The prefix is 20 characters, so the slice started two
/// characters early and the argument parsed was <c>"t 4096"</c> - which never parses as an integer,
/// so <c>set thinking-budget</c> printed "Ungültiger Wert" for every input it was ever given.
/// <c>set thinking-level</c> had the same defect (<c>[17..]</c> against a 19-character prefix,
/// yielding <c>"l HIGH"</c>). Two of the nine documented commands had never worked, and could not be
/// noticed by reading the code because the number 18 looks as plausible as 20.</para>
///
/// <para>Every argument here is taken with <c>prefix.Length</c>, so the offset cannot disagree with
/// the prefix it belongs to. That is the actual fix; the tests are what keep it fixed.</para>
/// [Human] Zerlegt eine Chat-Eingabe in einen Befehl. Rein funktional und testbar - vorher waren
/// zwei Befehle dauerhaft kaputt, weil die Argument-Position von Hand gezählt war.
/// </summary>
public static partial class ChatCommandParser {
    private static readonly string[] ValidThinkingLevels = ["MINIMAL", "LOW", "MEDIUM", "HIGH", "XHIGH"];

    public static ChatCommand Parse(string? input) {
        if (string.IsNullOrWhiteSpace(input)) {
            return ChatCommand.NotACommand;
        }

        // A leading "/" is optional throughout the REPL.
        string text = input.Trim().TrimStart('/');

        if (Matches(text, "help") || Matches(text, "commands") || Matches(text, "show commands")) {
            return new ChatCommand(ChatCommandKind.Help);
        }

        if (Matches(text, "clear") || Matches(text, "reset")) {
            return new ChatCommand(ChatCommandKind.Clear);
        }

        if (Matches(text, "exit") || Matches(text, "quit")) {
            return new ChatCommand(ChatCommandKind.Exit);
        }

        if (TryTakeArgument(text, "set temp ", out string temp)) {
            return float.TryParse(temp, NumberStyles.Any, CultureInfo.InvariantCulture, out float value) && value >= 0.0f && value <= 2.0f
                ? new ChatCommand(ChatCommandKind.SetTemperature) { Number = value }
                : new ChatCommand(ChatCommandKind.SetTemperature) { Error = $"Ungültiger Temperaturwert '{temp}'. Bitte eine Zahl zwischen 0.0 und 2.0 angeben." };
        }

        if (TryTakeArgument(text, "set tokens ", out string tokens)) {
            return int.TryParse(tokens, out int value) && value >= 1
                ? new ChatCommand(ChatCommandKind.SetMaxTokens) { Integer = value }
                : new ChatCommand(ChatCommandKind.SetMaxTokens) { Error = $"Ungültiger Token-Wert '{tokens}'. Bitte eine positive ganze Zahl angeben." };
        }

        if (TryTakeArgument(text, "set thinking-budget ", out string budget)) {
            return int.TryParse(budget, out int value) && value >= 0
                ? new ChatCommand(ChatCommandKind.SetThinkingBudget) { Integer = value }
                : new ChatCommand(ChatCommandKind.SetThinkingBudget) { Error = $"Ungültiger Wert für ThinkingBudget '{budget}'. Bitte eine positive ganze Zahl angeben." };
        }

        if (TryTakeArgument(text, "set thinking-level ", out string level)) {
            string upper = level.ToUpperInvariant();
            return ValidThinkingLevels.Contains(upper)
                ? new ChatCommand(ChatCommandKind.SetThinkingLevel) { Text = upper }
                : new ChatCommand(ChatCommandKind.SetThinkingLevel) { Error = $"Ungültiger Wert für ThinkingLevel '{upper}'. Gültige Werte sind: MINIMAL, LOW, MEDIUM, HIGH, XHIGH." };
        }

        if (TryTakeArgument(text, "set grounding ", out string grounding)) {
            string value = grounding.ToLowerInvariant();
            if (value is "on" or "true" or "ja" or "yes" or "1") {
                return new ChatCommand(ChatCommandKind.SetGrounding) { Flag = true };
            }
            if (value is "off" or "false" or "nein" or "no" or "0") {
                return new ChatCommand(ChatCommandKind.SetGrounding) { Flag = false };
            }
            return new ChatCommand(ChatCommandKind.SetGrounding) { Error = "Ungültiger Wert für grounding. Bitte 'on' oder 'off' ausgeben." };
        }

        // "set model" with no argument is valid - it opens the picker.
        if (text.StartsWith("set model", StringComparison.OrdinalIgnoreCase)) {
            return new ChatCommand(ChatCommandKind.SetModel) { Text = text["set model".Length..].Trim() };
        }

        if (ChangeKeyRegex().IsMatch(text)) {
            var match = ChangeKeyArgumentRegex().Match(text);
            return match.Success && int.TryParse(match.Groups[1].Value, out int profile) && profile is >= 0 and <= 3
                ? new ChatCommand(ChatCommandKind.ChangeApiKeyProfile) { Integer = profile }
                : new ChatCommand(ChatCommandKind.ChangeApiKeyProfile) { Error = "Bitte eine gültige Profilnummer (0, 1, 2 oder 3) angeben." };
        }

        if (TryTakeArgument(text, "attach ", out string attachment)) {
            return new ChatCommand(ChatCommandKind.Attach) { Text = attachment };
        }

        return ChatCommand.NotACommand;
    }

    private static bool Matches(string text, string command) =>
        text.Equals(command, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// [AI Context] Recognises <paramref name="prefix"/> and returns everything after it. The offset
    /// comes from the prefix itself, so it cannot drift out of sync with it - the defect that left
    /// two commands permanently broken.
    /// [Human] Nimmt das Argument hinter dem Befehl; die Position wird aus dem Befehl selbst
    /// berechnet, nie von Hand gezählt.
    /// </summary>
    private static bool TryTakeArgument(string text, string prefix, out string argument) {
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
            argument = "";
            return false;
        }
        argument = text[prefix.Length..].Trim();
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^change[- ]?key\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChangeKeyRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"change[- ]?key\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChangeKeyArgumentRegex();
}
