using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;

namespace GemGuide;

public class GemGuideSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [IgnoreMenu]
    public string ActiveProfile { get; set; } = "";

    public ToggleNode UseInventoryItems { get; set; } = new ToggleNode(true);
    public ToggleNode ShowGearLinks { get; set; } = new ToggleNode(true);
    public ToggleNode ShowGearSwitchesForSocketedLinks { get; set; } = new ToggleNode(true);
    public ToggleNode ReuseRemainingSocketsInLink { get; set; } = new ToggleNode(true);
    public ToggleNode ShowGuideWindow { get; set; } = new ToggleNode(true);
    public HotkeyNode ToggleGuideWindowHotkey { get; set; } = new HotkeyNode(Keys.G);
    public ToggleNode ShowEmptyGemSets { get; set; } = new ToggleNode(true);
    public ToggleNode ShowCompletedGemSets { get; set; } = new ToggleNode(true);
    public ToggleNode ShowEquippedButNotRequiredGems { get; set; } = new ToggleNode(true);
    public ToggleNode ShowGemAcquisition { get; set; } = new ToggleNode(true);
    public ToggleNode ShowPurchaseUpgrades { get; set; } = new ToggleNode(true);
    public ToggleNode ConsiderExistingUpgradesWhenEvaluationPurchaseUpgrades { get; set; } = new ToggleNode(true);
    public ColorNode PurchaseUpgradesFrameColor { get; set; } = new ColorNode(Color.Green.ToSharpDx());
    public ColorNode PurchaseRequiredGemsFrameColor { get; set; } = new ColorNode(Color.Cyan.ToSharpDx());
    public RangeNode<int> PurchaseUpgradesFrameThickness { get; set; } = new(5, 0, 10);
    public ButtonNode ReloadProfiles { get; set; } = new ButtonNode();

    public ProfileSettings ProfileSettings { get; set; } = new ProfileSettings();
}

[Submenu(RenderMethod = nameof(Render))]
public class ProfileSettings
{
    private string _profileSearch = "";
    private string _importInput = "";
    private bool _autoImport = true;
    private Task<ImportTaskResult> _importTask;

    private static readonly string[] CharacterClasses = ["Witch", "Shadow", "Ranger", "Duelist", "Marauder", "Templar", "Scion"];

    public void Render(GemGuide plugin)
    {
        var activeProfile = plugin.GetActiveProfile();
        if (ImGuiHelpers.SearchCombobox("gemguide_profilecombo", ref _profileSearch, ref activeProfile, plugin.Profiles,
                (gp, p) => ImGuiHelpers.WhitespaceSeparatedContains(gp.Name, p), gp => gp.Name))
        {
            plugin.Settings.ActiveProfile = activeProfile.Name;
        }

        ImGui.Text("Character class (for gem acquisition):");
        var currentClass = activeProfile.Profile.CharacterClass ?? "";
        var classIndex = Array.IndexOf(CharacterClasses, currentClass);
        if (classIndex < 0) classIndex = 0;
        if (ImGui.Combo("##gemguide_class", ref classIndex, CharacterClasses, CharacterClasses.Length))
        {
            activeProfile.Profile.CharacterClass = CharacterClasses[classIndex];
            plugin.SaveProfile(activeProfile);
        }

        if (_importTask == null)
        {
            ImGui.Checkbox("Import automatically", ref _autoImport);
            ImGui.InputText("##import", ref _importInput, 200000);
            if (new PobbinTreeImporter().IsMatch(_importInput) && (_autoImport || ImGui.Button("Import Pobbin")))
            {
                var input = _importInput;
                DebugWindow.LogMsg($"Start Pobbin import of {_importInput}");
                _importTask = Task.Run(async () =>
                {
                    var code = await new PobbinTreeImporter().GetPobCode(input, CancellationToken.None);
                    var (parsedSets, errors, className) = PobCodeImporter.GetGemSets(code);
                    return new ImportTaskResult(parsedSets, errors, new HashSet<SkillSet>(), className);
                });
                _importInput = "";
            }

            if (PobCodeImporter.IsValidUrl(_importInput) && (_autoImport || ImGui.Button("Import PoB")))
            {
                var input = _importInput;
                DebugWindow.LogMsg($"Starting PoB import of {_importInput}");
                _importTask = Task.Run(() =>
                {
                    var (parsedSets, errors, className) = PobCodeImporter.GetGemSets(input);
                    return new ImportTaskResult(parsedSets, errors, new HashSet<SkillSet>(), className);
                });
                _importInput = "";
            }
        }

        switch (_importTask)
        {
            case { IsCompleted: false }:
                ImGui.Text("Importing...");
                if (ImGui.Button("Cancel##pending"))
                {
                    _importTask = null;
                }
                break;
            case { IsCompletedSuccessfully: true }:
                if (ImGui.Button("Cancel##success"))
                {
                    _importTask = null;
                    break;
                }
                var importResult = _importTask.Result;
                if (importResult.errors.Any())
                {
                    ImGui.Text($"Got {importResult.errors.Count} errors during import:");
                    ImGui.Indent();
                    foreach (var error in importResult.errors)
                    {
                        ImGui.Bullet();
                        ImGui.SameLine();
                        ImGui.TextUnformatted(error);
                    }

                    ImGui.Unindent();
                }

                ImGui.Text($"Fetched a build with {importResult.skillSets.Count} skill sets:");
                for (int i = 0; i < importResult.skillSets.Count; i++)
                {
                    var skillSet = importResult.skillSets[i];
                    var included = !importResult.excludedSets.Contains(skillSet);
                    if (ImGui.Checkbox($"{skillSet.Name}##{i}", ref included))
                    {
                        if (included)
                        {
                            importResult.excludedSets.Remove(skillSet);
                        }
                        else
                        {
                            importResult.excludedSets.Add(skillSet);
                        }
                    }
                }

                ImGui.InputText("New profile name", ref importResult.ProfileName, 200);

                if (ImGui.Button("Add"))
                {
                    var profile = new GemProfile
                    {
                        SkillSets = importResult.skillSets.Except(importResult.excludedSets).ToList(),
                        CharacterClass = importResult.characterClass
                    };
                    plugin.AddAndSwitchToProfile(new NamedGemProfile(
                        plugin.GetFreeProfileName(string.IsNullOrWhiteSpace(importResult.ProfileName) ? "Unnamed" : importResult.ProfileName),
                        profile));
                    _importTask = null;
                }

                break;
            case { IsFaulted: true }:
                if (ImGui.Selectable($"Import failed: {_importTask.Exception}"))
                {
                    ImGui.SetClipboardText(_importTask.Exception.ToString());
                }

                if (ImGui.Button("Dismiss"))
                {
                    _importTask = null;
                }

                break;
        }

        foreach (var gemProfile in plugin.Profiles.ToList())
        {
            if (ImGui.TreeNode($"Profile '{gemProfile.Name}'"))
            {
                if (ImGui.Button("Delete profile"))
                {
                    if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                    {
                        plugin.DeleteProfile(gemProfile);
                        ImGui.TreePop();
                        break;
                    }
                }
                else if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Hold Shift");
                }

                for (var skillSetIndex = 0; skillSetIndex < gemProfile.Profile.SkillSets.Count; skillSetIndex++)
                {
                    var skillSet = gemProfile.Profile.SkillSets[skillSetIndex];
                    ImGui.PushID(skillSet.Name);
                    if (ImGui.Button("Delete"))
                    {
                        if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                        {
                            gemProfile.Profile.SkillSets.Remove(skillSet);
                            plugin.SaveProfile(gemProfile);
                        }
                    }
                    else if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Hold Shift");
                    }

                    ImGui.SameLine();
                    ImGui.BeginDisabled(skillSetIndex == 0);
                    if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                    {
                        (gemProfile.Profile.SkillSets[skillSetIndex - 1], gemProfile.Profile.SkillSets[skillSetIndex]) =
                            (gemProfile.Profile.SkillSets[skillSetIndex], gemProfile.Profile.SkillSets[skillSetIndex - 1]);
                        plugin.SaveProfile(gemProfile);
                    }

                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.BeginDisabled(skillSetIndex == gemProfile.Profile.SkillSets.Count - 1);
                    if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                    {
                        (gemProfile.Profile.SkillSets[skillSetIndex + 1], gemProfile.Profile.SkillSets[skillSetIndex]) =
                            (gemProfile.Profile.SkillSets[skillSetIndex], gemProfile.Profile.SkillSets[skillSetIndex + 1]);
                        plugin.SaveProfile(gemProfile);
                    }

                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextUnformatted(skillSet.Name);
                    ImGui.PopID();
                }


                ImGui.TreePop();
            }
        }
    }
}

internal record ImportTaskResult(List<SkillSet> skillSets, List<string> errors, HashSet<SkillSet> excludedSets, string characterClass = null)
{
    public string ProfileName = "";
}