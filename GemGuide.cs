using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using ItemFilterLibrary;
using Color = System.Drawing.Color;

namespace GemGuide;

public class GemGuide : BaseSettingsPlugin<GemGuideSettings>
{
    internal List<NamedGemProfile> Profiles = [];

    internal void AddAndSwitchToProfile(NamedGemProfile profile)
    {
        Profiles.Add(profile);
        Settings.ActiveProfile = profile.Name;
        SaveProfile(profile);
    }

    internal void DeleteProfile(NamedGemProfile profile)
    {
        Profiles.Remove(profile);
        File.Delete(Path.Join(GetProfilesDirectory().FullName, $"{profile.Name}.json"));
    }

    internal string GetFreeProfileName(string prefix)
    {
        return Enumerable.Range(0, 999999).Select(x => x == 0 ? prefix : $"{prefix}_{x}").Except(Profiles.Select(x => x.Name)).FirstOrDefault();
    }

    internal NamedGemProfile GetActiveProfile()
    {
        if (Profiles.Count == 0)
        {
            Profiles.Add(new NamedGemProfile("Default", new GemProfile { SkillSets = [] }));
            Settings.ActiveProfile = Profiles[0].Name;
            return Profiles[0];
        }

        if (Profiles.FirstOrDefault(x => x.Name == Settings.ActiveProfile) is { } profile)
        {
            return profile;
        }

        Settings.ActiveProfile = Profiles[0].Name;
        return Profiles[0];
    }

    private List<NamedGemProfile> ReloadProfiles()
    {
        var directory = GetProfilesDirectory().FullName;
        var files = Directory.EnumerateFiles(directory, "*.json");
        return files.Select(f =>
        {
            try
            {
                return new NamedGemProfile(Path.GetFileNameWithoutExtension(f), JsonSerializer.Deserialize<GemProfile>(File.ReadAllText(f)));
            }
            catch (Exception ex)
            {
                LogError($"Failed to load profile {f}: {ex}");
                return null;
            }
        }).Where(x => x != null).ToList();
    }

    private DirectoryInfo GetProfilesDirectory()
    {
        return Directory.CreateDirectory(Path.Join(ConfigDirectory, "Profiles"));
    }

    internal void SaveProfile(NamedGemProfile profile)
    {
        File.WriteAllText(Path.Join(GetProfilesDirectory().FullName, $"{profile.Name}.json"), JsonSerializer.Serialize(profile.Profile));
    }

    public override bool Initialise()
    {
        Settings.ReloadProfiles.OnPressed += () => Profiles = ReloadProfiles();
        Profiles = ReloadProfiles();
        return true;
    }

    public override Job Tick()
    {
        return null;
    }

    private record TranslatedGem(string Id, string Name, string BaseName, bool IsSupport, SkillGemDatSocketType Socket);

    private readonly Dictionary<string, TranslatedGem> _gems = [];

    private TranslatedGem TranslateSkill(string id)
    {
        if (_gems.TryGetValue(id, out var result))
        {
            return result;
        }

        var gemEffect = GameController.Files.GemEffects.GetById(id);
        if (gemEffect == null)
        {
            return new TranslatedGem(id, id, id, true, SkillGemDatSocketType.White);
        }

        var gem = gemEffect.Gem;
        if (gem == null)
        {
            return new TranslatedGem(id, id, id, true, SkillGemDatSocketType.White);
        }

        var bit = gem.ItemType;
        return _gems[id] = new TranslatedGem(id, bit.BaseName switch
        {
            { } s when s.EndsWith(" Support") => s[..^" Support".Length],
            var s => s ?? id,
        }, bit.BaseName ?? id, gem.IsSupportGem, gem.SocketType);
    }

    public override void Render()
    {
        var (profile, activeSetId, activeSet) = GetActiveSet();
        if (activeSetId == null)
        {
            return;
        }

        Dictionary<GemSet, (Entity item, List<(string Id, SocketColor socketColor)> sockets)> matchDict = null;
        Dictionary<GemSet, (Entity item, List<(string Id, SocketColor socketColor)> link)> moveGemMatches = null;
        if (Settings.ShowGuideWindow)
        {
            if (ImGui.Begin("Gems"))
            {
                if (SwitchActiveSkillSet(activeSetId, profile, activeSet))
                {
                    ImGui.End();
                    return;
                }

                (matchDict, moveGemMatches) = GetGemData();

                var ownedGems = ItemData.StaticPlayerData.OwnedGems.GroupBy(x => x.BaseName).ToDictionary(x => x.Key, x => x.Count());
                foreach (var gemSet in activeSet.GemSets)
                {
                    GemSetText(gemSet, false);
                    var slottedGems = new HashSet<Gem>();
                    var freeSlots = new Dictionary<SkillGemDatSocketType, int>();
                    if (matchDict.TryGetValue(gemSet, out var slottedIn))
                    {
                        ImGui.SameLine();
                        var itemName = GetItemName(slottedIn.item);
                        slottedGems = gemSet.Gems.IntersectBy(slottedIn.sockets.Select(x => x.Id).Where(x => x != null), g => g.VariantId).ToHashSet();
                        freeSlots = slottedIn.sockets.Where(x => x.Id == null || !gemSet.Gems.Select(g => g.VariantId).Contains(x.Id)).GroupBy(x => x.socketColor)
                            .ToDictionary(x => (SkillGemDatSocketType)x.Key, x => x.Count());
                        ImGui.Text($"(in {itemName}");
                        if (Settings.ShowGearLinks)
                        {
                            ImGui.SameLine(0, 0);
                            DrawSocketLink(slottedIn.sockets);
                        }

                        ImGui.SameLine(0, 0);
                        ImGui.Text(")");
                    }

                    if (moveGemMatches.TryGetValue(gemSet, out var moveTo))
                    {
                        var itemName = GetItemName(moveTo.item);
                        if (slottedIn == default)
                        {
                            freeSlots = moveTo.link.GroupBy(x => x.Item2)
                                .ToDictionary(x => (SkillGemDatSocketType)x.Key, x => x.Count());
                        }

                        if (slottedIn == default || Settings.ShowGearSwitchesForSocketedLinks)
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(Color.Yellow.ToImguiVec4(), $"(equippable in {itemName}");
                            if (Settings.ShowGearLinks)
                            {
                                ImGui.SameLine(0, 0);
                                DrawSocketLink(moveTo.link);
                            }

                            ImGui.SameLine(0, 0);
                            ImGui.TextColored(Color.Yellow.ToImguiVec4(), ")");
                        }
                    }

                    ImGui.Indent();
                    var perColorIndex = new ConcurrentDictionary<SkillGemDatSocketType, int>();
                    var first = true;
                    foreach (var gem in gemSet.Gems)
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            ImGui.SameLine();
                            ImGui.Text("-");
                            ImGui.SameLine();
                        }

                        var translateSkill = TranslateSkill(gem.VariantId);
                        var gemSocketType = translateSkill.Socket;
                        {
                            using var s = ImGuiHelpers.UseStyleColor(ImGuiCol.Text,
                                slottedGems.Contains(gem)
                                    ? Color.LightGreen.ToImgui()
                                    : gemSocketType == SkillGemDatSocketType.White || //let's just ignore the white gems, fuck those
                                      perColorIndex.AddOrUpdate(gemSocketType, _ => 0, (_, i) => i + 1) < freeSlots.GetValueOrDefault(gemSocketType) ||
                                      perColorIndex.AddOrUpdate(SkillGemDatSocketType.White, _ => 0, (_, i) => i + 1) <
                                      freeSlots.GetValueOrDefault(SkillGemDatSocketType.White) // if white sockets are available, fall back to those
                                        ? Color.Yellow.ToImgui()
                                        : Color.Pink.ToImgui());
                            ImGui.Text(ownedGems.ContainsKey(translateSkill.BaseName) ? $"[{translateSkill.Name}]" : translateSkill.Name);
                        }
                        ImGui.SameLine(0, 0);
                        {
                            using var s = ImGuiHelpers.UseStyleColor(ImGuiCol.Text,
                                GetGemTextColor(gemSocketType).ToImgui());
                            ImGui.Text(GetGemText(gemSocketType));
                        }
                    }

                    ImGui.Unindent();
                }
            }

            ImGui.End();
        }

        if (Settings.ShowPurchaseUpgrades &&
            (
                    GameController.IngameState.IngameUi.PurchaseWindow,
                    GameController.IngameState.IngameUi.PurchaseWindowHideout
                ) switch
                {
                    ({ IsVisible: true } p1, _) => p1,
                    (_, { IsVisible: true } p2) => p2,
                    _ => null
                } is { } purchaseWindow)
        {
            if (matchDict == null || moveGemMatches == null)
            {
                (matchDict, moveGemMatches) = GetGemData();
            }

            var visibleStashVisibleInventoryItems = purchaseWindow.TabContainer.VisibleStash.VisibleInventoryItems
                .Select(x => (x, GetItemLinks(x.Item).Select(il => il.gems)))
                .Where(x => x.Item2.Any())
                .ToDictionary(x => x.x, x => (new List<GemSet>(), x.Item2));
            foreach (var gemSet in activeSet.GemSets)
            {
                var activeRequirement = GetActiveGemRequirement(gemSet.Gems);
                var supportRequirement = GetSupportGemRequirement(gemSet.Gems);
                var existingMatch = matchDict.TryGetValue(gemSet, out var eqMatch)
                    ? eqMatch
                    : Settings.ConsiderExistingUpgradesWhenEvaluationPurchaseUpgrades
                        ? moveGemMatches.GetValueOrDefault(gemSet)
                        : default;
                var existingScore = GetNumberOfUnsocketableGems(activeRequirement, supportRequirement,
                    existingMatch == default ? default : GetSocketLinkGemSockets(existingMatch));
                if (existingScore == (0, 0))
                {
                    continue;
                }

                foreach (var (item, (list, links)) in visibleStashVisibleInventoryItems)
                {
                    foreach (var link in links)
                    {
                        if (GetNumberOfUnsocketableGems(activeRequirement, supportRequirement, GetSocketLinkGemSockets((item.Item, link))).CompareTo(existingScore) < 0)
                        {
                            list.Add(gemSet);
                            break;
                        }
                    }
                }
            }

            foreach (var (item, (setList, _)) in visibleStashVisibleInventoryItems.Where(x => x.Value.Item1.Any()))
            {
                var hoveredItem = GetHoveredItem();
                var frameColor = Settings.PurchaseUpgradesFrameColor.Value;
                if (hoveredItem != null && !item.Equals(hoveredItem) && (hoveredItem.Tooltip?.GetClientRectCache.Intersects(item.GetClientRectCache) ?? false))
                {
                    frameColor.A = (byte)(frameColor.A * (45.0 / 255));
                }
                Graphics.DrawFrame(item.GetClientRectCache.Inflated(-10, -10), frameColor, 10, Settings.PurchaseUpgradesFrameThickness.Value, 0);
                if (item.Equals(hoveredItem))
                {
                    if (ImGui.BeginTooltip())
                    {
                        foreach (var gemSet in setList)
                        {
                            ImGui.TextColored(Color.LightGreen.ToImguiVec4(), "Upgrade for");
                            ImGui.SameLine();
                            GemSetText(gemSet, true);
                        }

                        ImGui.EndTooltip();
                    }
                }
            }
        }
    }

    private void GemSetText(GemSet gemSet, bool colored)
    {
        if (colored)
        {
            var first = true;
            foreach (var gem in gemSet.Gems)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    ImGui.SameLine(0,0);
                    ImGui.Text("-");
                    ImGui.SameLine(0,0);
                }

                var translateSkill = TranslateSkill(gem.VariantId);
                using var s = ImGuiHelpers.UseStyleColor(ImGuiCol.Text,
                    GetGemTextColor(translateSkill.Socket).ToImgui());
                ImGui.Text(translateSkill.Name);
            }
        }
        else
        {
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(gemSet.Label)
                ? string.Join('-', gemSet.Gems.Select(g => TranslateSkill(g.VariantId)).Where(x => !x.IsSupport).Select(x => x.Name))
                : gemSet.Label);
        }
    }

    private Element GetHoveredItem()
    {
        return GameController.IngameState.UIHover is { Address: not 0, Entity.IsValid: true } hover ? hover : null;
    }


    private static void DrawSocketLink(List<(string Id, SocketColor socketColor)> sockets)
    {
        var firstInItemLink = true;
        foreach (var linkSocket in sockets)
        {
            if (firstInItemLink)
            {
                firstInItemLink = false;
                ImGui.Text(" ");
                ImGui.SameLine(0, 0);
            }
            else
            {
                ImGui.SameLine(0, 0);
                ImGui.Text("-");
                ImGui.SameLine(0, 0);
            }

            using (ImGuiHelpers.UseStyleColor(ImGuiCol.Text, GetGemTextColor((SkillGemDatSocketType)linkSocket.socketColor).ToImgui()))
            {
                ImGui.Text(GetGemText((SkillGemDatSocketType)linkSocket.socketColor));
            }
        }
    }

    private static string GetGemText(SkillGemDatSocketType gemSocketType)
    {
        return gemSocketType switch
        {
            SkillGemDatSocketType.Red => "{R}",
            SkillGemDatSocketType.Green => "[G]",
            SkillGemDatSocketType.Blue => "<B>",
            SkillGemDatSocketType.White => "(W)",
            var v => $"({v}??)",
        };
    }

    private static Color GetGemTextColor(SkillGemDatSocketType gemSocketType)
    {
        return gemSocketType switch
        {
            SkillGemDatSocketType.Red => Color.Red,
            SkillGemDatSocketType.Green => Color.Green,
            SkillGemDatSocketType.Blue => Color.LightSkyBlue,
            SkillGemDatSocketType.White => Color.White,
            _ => Color.Violet,
        };
    }

    private static bool SwitchActiveSkillSet(SkillSetSelection activeSetId, NamedGemProfile profile, SkillSet activeSet)
    {
        var switched = false;
        ImGui.BeginDisabled(activeSetId.SetId == 0);
        if (ImGui.Button("<"))
        {
            profile.Profile.ActiveSet = new SkillSetSelection(activeSetId.SetId - 1);
            switched = true;
        }

        ImGui.SameLine();

        ImGui.EndDisabled();
        ImGui.TextUnformatted(activeSet.Name);
        ImGui.SameLine();
        ImGui.BeginDisabled(activeSetId.SetId == profile.Profile.SkillSets.Count - 1);
        if (ImGui.Button(">"))
        {
            profile.Profile.ActiveSet = new SkillSetSelection(activeSetId.SetId + 1);
            switched = true;
        }

        ImGui.EndDisabled();
        return switched;
    }

    private (NamedGemProfile profile, SkillSetSelection activeSetId, SkillSet activeSet) GetActiveSet()
    {
        var profile = GetActiveProfile();
        if (profile.Profile.SkillSets.Count == 0)
        {
            return (profile, null, null);
        }

        var activeSetId = profile.Profile.ActiveSet ??= new SkillSetSelection(0);
        if (activeSetId.SetId < 0 || activeSetId.SetId >= profile.Profile.SkillSets.Count)
        {
            activeSetId = profile.Profile.ActiveSet = new SkillSetSelection(0);
        }

        var activeSet = profile.Profile.SkillSets[activeSetId.SetId];
        return (profile, activeSetId, activeSet);
    }

    private (
        Dictionary<GemSet, (Entity item, List<(string Id, SocketColor socketColor)> sockets)> matchDict,
        Dictionary<GemSet, (Entity item, List<(string Id, SocketColor socketColor)> link)> moveGemMatches
        ) GetGemData()
    {
        var activeSet = GetActiveSet().activeSet;
        if (activeSet == null)
        {
            return default;
        }

        InventorySlotE[] slots =
        [
            InventorySlotE.BodyArmour1,
            InventorySlotE.Weapon1,
            InventorySlotE.Offhand1,
            InventorySlotE.Helm1,
            InventorySlotE.Gloves1,
            InventorySlotE.Boots1,
            InventorySlotE.Amulet1,
            InventorySlotE.Ring1,
            InventorySlotE.Ring2,
            InventorySlotE.Belt1,
            ..Settings.UseInventoryItems ? new[] { InventorySlotE.MainInventory1 } : [],
        ];
        var items = slots
            .Join(GameController.IngameState.ServerData.PlayerInventories.Select(x => x.Inventory), s => s, s => s.InventSlot, (s, i) => i)
            .SelectMany(x => x.Items)
            .ToList();
        var itemLinks = GetItemLinks(items);
        var (matches, unpairedSets) = GetMatches(activeSet, itemLinks);
        var matchDict = matches.ToDictionary(x => x.set, x => (x.item, x.sockets));
        var moveGemMatches = GetMoveMatches(activeSet.GemSets, matchDict, itemLinks).ToDictionary(x => x.set, x => (x.item, x.link));
        return (matchDict, moveGemMatches);
    }

    private static string GetItemName(Entity item)
    {
        var baseName = item.TryGetComponent<Base>(out var itemBase)
            ? itemBase.Name
            : null;
        var uniqueName = item.TryGetComponent<Mods>(out var itemMods)
            ? itemMods.UniqueName
            : null;
        var itemName = string.IsNullOrWhiteSpace(uniqueName) ? baseName ?? "" : string.IsNullOrWhiteSpace(baseName) ? uniqueName : $"{uniqueName}, {baseName}";
        return itemName;
    }

    private static List<(Entity item, List<(string Id, SocketColor SocketColor)> gems)> GetItemLinks(List<Entity> items)
    {
        return items.SelectMany(x => GetItemLinks(x)).ToList();
    }

    private static IEnumerable<(Entity item, List<(string Id, SocketColor SocketColor)> gems)> GetItemLinks(Entity x)
    {
        return x.TryGetComponent<Sockets>(out var sockets)
            ? sockets.SocketInfoByLinkGroup.Select(g =>
                (item: x, gems: g.Select(gg => (gg.SocketedGemEntity?.GetComponent<SkillGem>()?.GemEffect.Id, gg.SocketColor)).ToList()))
            : [];
    }

    private List<(Entity item, List<(string, SocketColor)> link, GemSet set)> GetMoveMatches(
        List<GemSet> allSets,
        Dictionary<GemSet, (Entity item, List<(string Id, SocketColor socketColor)> sockets)> matchDict,
        List<(Entity item, List<(string Id, SocketColor SocketColor)> gems)> itemLinks)
    {
        var moveGemMatches = new List<(Entity, List<(string, SocketColor)>, GemSet)>();
        foreach (var gemSet in allSets)
        {
            var activeGemRequirement = GetActiveGemRequirement(gemSet.Gems);
            var supportGemRequirement = GetSupportGemRequirement(gemSet.Gems);
            var bestFit = itemLinks.Select(il => (il, sockets: GetSocketLinkGemSockets(il)))
                .Select(il => (il.il, il.sockets, unsocketable: GetNumberOfUnsocketableGems(activeGemRequirement, supportGemRequirement, il.sockets)))
                .Where(il => il.unsocketable.Item1 == 0)
                .DefaultIfEmpty()
                .MinBy(il => il == default ? (0, 0) : (il.unsocketable.Item2, il.il.gems.Count));
            if (bestFit == default)
            {
                continue;
            }

            if (matchDict.TryGetValue(gemSet, out var currentMatch))
            {
                var currentScore = GetNumberOfUnsocketableGems(activeGemRequirement, supportGemRequirement, GetSocketLinkGemSockets((currentMatch.item, currentMatch.sockets)));
                if (currentScore.CompareTo(bestFit.unsocketable) <= 0)
                {
                    continue;
                }
            }

            itemLinks.Remove(bestFit.il);
            if (bestFit.unsocketable == (0, 0) && bestFit.il.gems.Count > gemSet.Gems.Count)
            {
                TryReaddPartialLink(itemLinks, gemSet, bestFit.il);
            }

            moveGemMatches.Add((bestFit.il.item, bestFit.il.gems, gemSet));
        }

        return moveGemMatches;
    }

    private void TryReaddPartialLink(List<(Entity item, List<(string Id, SocketColor SocketColor)> gems)> itemLinks, GemSet gemSet,
        (Entity item, List<(string Id, SocketColor SocketColor)> gems) removedLink)
    {
        if (!Settings.ReuseRemainingSocketsInLink)
        {
            return;
        }

        bool failed = false;
        var remainingLink = (removedLink.item, removedLink.gems.ToList());
        foreach (var gemSetGem in gemSet.Gems.OrderBy(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.White)) //white gems last
        {
            var skillGemDatSocketType = TranslateSkill(gemSetGem.VariantId).Socket;
            if (remainingLink.Item2.Select((x, i) => (x, i + 1)).OrderBy(x => x.x.SocketColor == SocketColor.White) //white sockets last
                    .FirstOrDefault(x => x.x.SocketColor == (SocketColor)skillGemDatSocketType || x.x.SocketColor == SocketColor.White ||
                                         skillGemDatSocketType == SkillGemDatSocketType.White) is { Item2: > 0, x: var socketToRemove })
            {
                if (!remainingLink.Item2.Remove(socketToRemove))
                {
                    failed = true;
                    break;
                }
            }
            else
            {
                failed = true;
                break;
            }
        }

        if (!failed && remainingLink.Item2.Count > 0)
        {
            itemLinks.Add(remainingLink);
        }
    }

    private static (int, int, int, int) GetSocketLinkGemSockets((Entity item, List<(string Id, SocketColor SocketColor)> gems) il)
    {
        return (
            il.gems.Count(ilg => ilg.SocketColor == SocketColor.Red),
            il.gems.Count(ilg => ilg.SocketColor == SocketColor.Green),
            il.gems.Count(ilg => ilg.SocketColor == SocketColor.Blue),
            il.gems.Count(ilg => ilg.SocketColor == SocketColor.White)
        );
    }

    private (int, int, int, int) GetActiveGemRequirement(List<Gem> gems)
    {
        var activeGems = gems.Where(x => !TranslateSkill(x.VariantId).IsSupport).ToList();
        return (
            activeGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.Red),
            activeGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.Green),
            activeGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.Blue),
            activeGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.White));
    }

    private (int, int, int, int) GetSupportGemRequirement(List<Gem> gems)
    {
        var supportGems = gems.Where(x => TranslateSkill(x.VariantId).IsSupport).ToList();
        return (
            supportGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.Red),
            supportGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.Green),
            supportGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.Blue),
            supportGems.Count(x => TranslateSkill(x.VariantId).Socket == SkillGemDatSocketType.White));
    }

    private static (int, int) GetNumberOfUnsocketableGems(
        (int red, int green, int blue, int white) activeGems,
        (int red, int green, int blue, int white) supportGems,
        (int red, int green, int blue, int white) sockets)
    {
        var socketsAfterActive = (
            red: sockets.red - activeGems.red,
            green: sockets.green - activeGems.green,
            blue: sockets.blue - activeGems.blue,
            white: sockets.white
        );
        var activeToWhite = Math.Max(0, -socketsAfterActive.red) + Math.Max(0, -socketsAfterActive.green) + Math.Max(0, -socketsAfterActive.blue);
        if (socketsAfterActive.white < activeToWhite)
        {
            return (activeToWhite - socketsAfterActive.white, 0);
        }

        socketsAfterActive.white -= activeToWhite;
        socketsAfterActive.red = Math.Max(0, socketsAfterActive.red);
        socketsAfterActive.green = Math.Max(0, socketsAfterActive.green);
        socketsAfterActive.blue = Math.Max(0, socketsAfterActive.blue);
        if (activeGems.white > 0)
        {
            var totalFreeAfterNonWhiteActive = socketsAfterActive.red + socketsAfterActive.green + socketsAfterActive.blue + socketsAfterActive.white;
            if (totalFreeAfterNonWhiteActive < activeGems.white)
            {
                return (activeGems.white - totalFreeAfterNonWhiteActive, 0);
            }
        }

        var socketsAfterSupport = (
            red: socketsAfterActive.red - supportGems.red,
            green: socketsAfterActive.green - supportGems.green,
            blue: socketsAfterActive.blue - supportGems.blue,
            white: socketsAfterActive.white
        );

        var supportToWhite = Math.Max(0, -socketsAfterSupport.red) + Math.Max(0, -socketsAfterSupport.green) + Math.Max(0, -socketsAfterSupport.blue);
        if (socketsAfterSupport.white < supportToWhite)
        {
            return (0, supportToWhite - socketsAfterSupport.white);
        }

        socketsAfterSupport.white -= supportToWhite;
        socketsAfterSupport.red = Math.Max(0, socketsAfterSupport.red);
        socketsAfterSupport.green = Math.Max(0, socketsAfterSupport.green);
        socketsAfterSupport.blue = Math.Max(0, socketsAfterSupport.blue);

        if (activeGems.white + supportGems.white > 0)
        {
            var totalFreeAfterNonWhite = socketsAfterSupport.red + socketsAfterSupport.green + socketsAfterSupport.blue + socketsAfterSupport.white;
            if (totalFreeAfterNonWhite < activeGems.white + supportGems.white)
            {
                return (0, activeGems.white + supportGems.white - totalFreeAfterNonWhite);
            }
        }

        return (0, 0);
    }

    private (List<(Entity item, List<(string Id, SocketColor socketColor)> sockets, GemSet set)> matches, List<GemSet> unpairedSets) GetMatches(SkillSet activeSet,
        List<(Entity x, List<(string Id, SocketColor SocketColor)> sockets)> itemLinks)
    {
        var matches = new List<(Entity, List<(string, SocketColor)>, GemSet)>();
        var unpairedSets = activeSet.GemSets.ToList();
        //pass 1, all active gems, max by matching support count
        foreach (var gemSet in unpairedSets.ToList())
        {
            var maxMatch = itemLinks
                .Where(il => !gemSet.Gems.Where(x => !TranslateSkill(x.VariantId).IsSupport).Select(g => g.VariantId).Except(il.sockets.Select(ilg => ilg.Id)).Any())
                .DefaultIfEmpty()
                .Select(il => (il, missingGemCount: il == default
                    ? 1000
                    : gemSet.Gems.Where(x => TranslateSkill(x.VariantId).IsSupport).Select(g => g.VariantId).Except(il.sockets.Select(ilg => ilg.Id)).Count()))
                .MinBy(il => il.missingGemCount);
            if (maxMatch.il == default)
            {
                continue;
            }

            itemLinks.Remove(maxMatch.il);
            if (maxMatch.missingGemCount == 0 && gemSet.Gems.Count < maxMatch.il.sockets.Count)
            {
                TryReaddPartialLink(itemLinks, gemSet, maxMatch.il);
            }

            unpairedSets.Remove(gemSet);
            matches.Add((maxMatch.il.x, maxMatch.il.sockets, gemSet));
        }

        //pass 2, any active gems, max by (activeGems, supportGems)
        foreach (var gemSet in unpairedSets.ToList())
        {
            var maxMatch = itemLinks
                .Where(il => gemSet.Gems.Where(x => !TranslateSkill(x.VariantId).IsSupport).Select(g => g.VariantId).Intersect(il.sockets.Select(ilg => ilg.Id)).Any())
                .DefaultIfEmpty()
                .MaxBy(il => il == default
                    ? (0, 0)
                    : (
                        gemSet.Gems.Where(x => !TranslateSkill(x.VariantId).IsSupport).Select(g => g.VariantId).Intersect(il.sockets.Select(ilg => ilg.Id)).Count(),
                        gemSet.Gems.Where(x => TranslateSkill(x.VariantId).IsSupport).Select(g => g.VariantId).Intersect(il.sockets.Select(ilg => ilg.Id)).Count())
                );
            if (maxMatch == default)
            {
                continue;
            }

            itemLinks.Remove(maxMatch);
            unpairedSets.Remove(gemSet);
            matches.Add((maxMatch.x, maxMatch.sockets, gemSet));
        }

        return (matches, unpairedSets);
    }
}