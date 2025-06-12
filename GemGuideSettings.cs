using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.Shared.Attributes;
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

    public void Render(GemGuide plugin)
    {
        var activeProfile = plugin.GetActiveProfile();
        if (ImGuiHelpers.SearchCombobox("gemguide_profilecombo", ref _profileSearch, ref activeProfile, plugin.Profiles,
                (gp, p) => ImGuiHelpers.WhitespaceSeparatedContains(gp.Name, p), gp => gp.Name))
        {
            plugin.Settings.ActiveProfile = activeProfile.Name;
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
                    var gemSets = PobCodeImporter.GetGemSets(code);
                    return new ImportTaskResult(gemSets.parsedSets, gemSets.errors, new HashSet<SkillSet>());
                });
                _importInput = "";
            }

            if (PobCodeImporter.IsValidUrl(_importInput) && (_autoImport || ImGui.Button("Import PoB")))
            {
                var input = _importInput;
                DebugWindow.LogMsg($"Starting PoB import of {_importInput}");
                _importTask = Task.Run(() =>
                {
                    var gemSets = PobCodeImporter.GetGemSets(input);
                    return new ImportTaskResult(gemSets.parsedSets, gemSets.errors, new HashSet<SkillSet>());
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
                    plugin.AddAndSwitchToProfile(new NamedGemProfile(
                        plugin.GetFreeProfileName(string.IsNullOrWhiteSpace(importResult.ProfileName) ? "Unnamed" : importResult.ProfileName),
                        new GemProfile() { SkillSets = importResult.skillSets.Except(importResult.excludedSets).ToList() }));
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

internal record ImportTaskResult(List<SkillSet> skillSets, List<string> errors, HashSet<SkillSet> excludedSets)
{
    public string ProfileName = "";
}