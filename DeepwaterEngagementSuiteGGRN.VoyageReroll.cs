using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Reroll advice.
///
/// Rerolling replaces all twelve border modifiers at once for Dead Man's Sulphur, at a cost that
/// doubles each time. Sulphur trades for divines, so the cost side is exact. The gain side is not:
/// what a board is worth in currency is still unknown, so instead of inventing a conversion this
/// compares the board against the distribution of boards actually seen, and says where this one
/// sits. A board in the bottom of that distribution is worth rerolling; one at the top is not.
/// </summary>
public partial class DeepwaterEngagementSuiteGGRN
{
    private sealed class BoardScoreRecord
    {
        public string Timestamp { get; set; }
        public string Signature { get; set; }
        public double Score { get; set; }
    }

    private List<BoardScoreRecord> _boardScores;
    private string _boardScoresPath;
    private string _lastBorderSignature;
    private string _lastScoredSignature;
    private int _rerollsThisBoard;

    private string BoardScoresPath =>
        _boardScoresPath ??= Path.Combine(ConfigDirectory, "board-scores.json");

    private List<BoardScoreRecord> BoardScores
    {
        get
        {
            if (_boardScores != null)
                return _boardScores;

            try
            {
                _boardScores = File.Exists(BoardScoresPath)
                    ? JsonConvert.DeserializeObject<List<BoardScoreRecord>>(File.ReadAllText(BoardScoresPath)) ?? []
                    : [];
            }
            catch
            {
                _boardScores = [];
            }

            return _boardScores;
        }
    }

    /// <summary>
    /// Counts rerolls of the current board. The window does not report how many have been paid for,
    /// so this counts border-mod changes seen while the same charts are on the board.
    /// </summary>
    private void TrackRerolls(VoyageWindow tree)
    {
        string signature;
        try
        {
            signature = string.Join(",", tree.Data.BorderMods.Select(m => m.RawName));
        }
        catch
        {
            return;
        }

        if (signature == _lastBorderSignature)
            return;

        if (_lastBorderSignature != null)
            _rerollsThisBoard++;

        _lastBorderSignature = signature;
    }

    /// <summary>Records the score of a freshly solved board, once per distinct border layout.</summary>
    private void RecordBoardScore(double score)
    {
        var signature = _lastBorderSignature;
        if (signature == null || signature == _lastScoredSignature)
            return;

        _lastScoredSignature = signature;
        BoardScores.Add(new BoardScoreRecord
        {
            Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Signature = signature,
            Score = score,
        });

        if (BoardScores.Count > 500)
            BoardScores.RemoveRange(0, BoardScores.Count - 500);

        try
        {
            File.WriteAllText(BoardScoresPath, JsonConvert.SerializeObject(BoardScores, Formatting.Indented));
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"DWS: could not save board scores: {ex.Message}");
        }
    }

    private double NextRerollCostSulphur()
    {
        var settings = Settings.VoyageSettings;
        return settings.RerollBaseCostSulphur.Value *
               Math.Pow(settings.RerollCostMultiplier.Value, _rerollsThisBoard);
    }

    private void DrawRerollPanel(double currentScore)
    {
        if (!Settings.VoyageSettings.ShowRerollAdvice.Value)
            return;

        ImGui.Spacing();
        if (!ImGui.TreeNodeEx("Reroll advice", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var settings = Settings.VoyageSettings;
        var costSulphur = NextRerollCostSulphur();
        var costDivines = settings.SulphurPerDivine.Value > 0
            ? costSulphur / settings.SulphurPerDivine.Value
            : 0;

        ImGui.Text($"Rerolls seen on this board: {_rerollsThisBoard}");
        ImGui.Text($"Next reroll: {costSulphur:N0} sulphur ~ {costDivines:F3} div");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset count"))
            _rerollsThisBoard = 0;

        var history = BoardScores.Select(x => x.Score).Where(x => x > 0).ToList();
        if (history.Count < 5)
        {
            ImGui.TextDisabled($"Only {history.Count} boards recorded. Need more solved boards before " +
                               "this board can be placed in a distribution - keep solving and rerolling.");
            ImGui.TreePop();
            return;
        }

        history.Sort();
        var below = history.Count(x => x < currentScore);
        var percentile = 100.0 * below / history.Count;
        var median = history[history.Count / 2];
        var mean = history.Average();

        // What an average reroll is worth, measured against boards actually seen: the mean gain
        // when the new board is better, zero when it is not (a reroll is never forced on you).
        var expectedGain = history.Average(x => Math.Max(0, x - currentScore));

        ImGui.Text($"This board: {currentScore:F0}  |  {history.Count} boards recorded, median {median:F0}, mean {mean:F0}");
        ImGui.Text($"Percentile: {percentile:F0}%  (best seen {history[^1]:F0}, worst {history[0]:F0})");
        ImGui.Text($"Average reroll gain vs this board: +{expectedGain:F0} score ({(currentScore > 0 ? 100 * expectedGain / currentScore : 0):F1}%)");

        var threshold = settings.RerollBelowPercentile.Value;
        if (percentile < threshold)
        {
            ImGui.TextColored(Color.Lime.ToImguiVec4(),
                $"REROLL - this board is in the bottom {threshold:F0}% of what you have seen, " +
                $"and the reroll costs {costDivines:F3} div.");
        }
        else
        {
            ImGui.TextColored(Color.Orange.ToImguiVec4(),
                $"KEEP - this board beats {percentile:F0}% of the boards you have seen.");
        }

        ImGui.TextDisabled("Gain is in score units, not currency: what a point of score is worth in " +
                           "divines is not yet calibrated, so this ranks boards rather than pricing them.");
        ImGui.TreePop();
    }
}
