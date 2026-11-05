using System.Buffers.Binary;
using SiegeFX.Core.Actors;
using SiegeFX.Core.Save;

namespace SiegeFX.OriginalSaves;

/// <summary>A SiegeFX save made from an original-game save, and what the
/// conversion left out or had to guess (one line each).</summary>
public sealed record OriginalSaveImportResult(SaveFile Save, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a save written by the original game into a SiegeFX save that the
/// Load Game window lists and loads like any other. Carries the party (each
/// member's skills and XP, attributes, gold, orders, active slot, inventory
/// and worn gear), the hero's name, gender and spot (the terrain node and the
/// offset inside it, which the game resolves once the region is up), quests,
/// the game auditor's yes/no flags, and the map placements the original will
/// not create again (killed, smashed, used up, or carried off).
///
/// Not carried yet: appearance beyond gender, item enchantments (the party
/// file names the base template only), doors, chests and other per-object
/// state, and loot on the ground. A <c>.dsparty</c> has no world, so its
/// character starts the campaign at the beginning.
/// </summary>
public static class OriginalSaveImport
{
    /// <summary>SiegeFX's hero actor (see the load path in RenderHost).</summary>
    public const uint HeroScid = 0xFFFFFF00;
    /// <summary>Companions with no map placement of their own (a bought pack
    /// mule) get a SCID from this range, below the generator and summon ranges.</summary>
    public const uint ImportedCompanionScidBase = 0xFC000000;
    /// <summary>Where a <c>.dsparty</c> character starts.</summary>
    public const string CampaignStartRegion = "/world/maps/map_world/regions/fh_r1";
    /// <summary>Above any life or mana cap: the load clamps it to the
    /// member's maximum, so imported members start healthy.</summary>
    const float Full = 1e9f;

    public static OriginalSaveImportResult Import(OriginalSave save, DateTime now) =>
        Import(save.Info, save.Party, save.Kind == OriginalSaveKind.Party ? null : save.World,
               save.Thumbnail, save.Path, now);

    public static OriginalSaveImportResult Import(OriginalSaveInfo info, OriginalParty party, XferTree? world,
                                                  byte[]? thumbnailBmp, string sourcePath, DateTime now)
    {
        if (info.IsExpansion || info.MapName.Equals("map_expansion", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Legends of Aranna saves can't be imported: SiegeFX plays the Kingdom of Ehb campaign only.");
        var hero = party.Hero ?? throw new InvalidDataException("the party has no hero member");
        var notes = new List<string>();

        bool hasWorld = world is not null;
        string region = hasWorld && info.MapName.Length > 0 && info.Region.Length > 0
            ? $"/world/maps/{info.MapName}/regions/{info.Region}"
            : CampaignStartRegion;
        if (!hasWorld)
            notes.Add($"no world in this file: the character starts the campaign at {CampaignStartRegion}");

        var file = new SaveFile
        {
            SavedAt = now,
            DisplayName = "Original: " + (info.ScreenName.Length > 0 ? info.ScreenName : hero.ScreenName),
            RegionPath = region,
            PlayerRegion = region,
            HeroName = info.HeroName.Length > 0 ? info.HeroName : hero.ScreenName,
            MapName = info.MapScreenName,
            ElapsedSeconds = info.TimeElapsedSeconds,
            Thumbnail = thumbnailBmp is null ? null : BmpToThumbnail(thumbnailBmp),
            ImportedFrom = sourcePath,
        };
        if (thumbnailBmp is not null && file.Thumbnail is null)
            notes.Add("thumbnail: not a bitmap this importer reads (24- or 32-bit uncompressed); left out");

        var objects = world is null ? new Dictionary<uint, XferNode>() : OriginalWorldFacts.ObjectsByScid(world);

        // ── The hero ───────────────────────────────────────────────────────
        var p = new PlayerSnapshot
        {
            Scid = HeroScid,
            HeroName = file.HeroName,
            Facing = new Vec3(0f, 0f, 1f),
            Gold = party.Gold > 0 ? party.Gold : hero.Gold,
            Variant = new HeroVariantSnapshot
            {
                Gender = hero.TemplateName.Contains("girl", StringComparison.OrdinalIgnoreCase) ? "girl" : "boy",
                FaceIdx = 0, StyleIdx = 0, ShirtIdx = 0, PantsIdx = 0,
            },
            MoveOrder = hero.Orders.MoveOrder,
            AtkOrder = hero.Orders.AtkOrder,
            TgtOrder = hero.Orders.TgtOrder,
            ActiveSlot = ActiveSlot(hero.SelectedActiveLocation),
        };
        ApplyProgress(hero, out var totalXp, out var level, out var skillXp);
        p.TotalXp = totalXp;
        p.Level = level;
        p.SkillXp = skillXp;
        p.Strength = (float)Skill(hero, "strength");
        p.Dexterity = (float)Skill(hero, "dexterity");
        p.Intelligence = (float)Skill(hero, "intelligence");
        if (p.Strength <= 0 || p.Dexterity <= 0 || p.Intelligence <= 0)
            notes.Add("hero: an attribute is missing from the party file; the game keeps its starting value");
        if (hero.Skins.Count > 0)
            notes.Add($"hero: appearance not carried, only the gender ({p.Variant.Gender}); original skins: {string.Join(" / ", hero.Skins)}");
        var spells = new SpellbookSnapshot();
        p.Inventory.AddRange(Items(hero, $"hero '{hero.ScreenName}'", notes, spells, out _));
        if (spells.PrimarySpell is not null || spells.SecondarySpell is not null || spells.Placed.Count > 0)
            p.Spellbook = spells;
        if (info.Position is { } pos && hasWorld)
        {
            p.SpawnNode = pos.Node;
            p.SpawnOffset = new Vec3(pos.X, pos.Y, pos.Z);
        }
        file.Player = p;

        // ── Companions (and the pack mule) ──────────────────────────────────
        var usedScids = new HashSet<uint>();
        for (int i = 0, partyIndex = 1; i < party.Members.Count; i++)
        {
            if (i == party.HeroIndex) continue;
            var m = party.Members[i];
            uint scid = CompanionScid(m.TemplateName, objects, usedScids);
            if (scid == 0)
            {
                scid = ImportedCompanionScidBase + (uint)partyIndex;
                notes.Add($"companion '{m.ScreenName}' ({m.TemplateName}): no map placement found; joins as a new actor");
            }
            usedScids.Add(scid);
            ApplyProgress(m, out var mXp, out _, out var mSkillXp);
            var cs = new CompanionSnapshot
            {
                Scid = scid,
                TemplateName = m.TemplateName,
                PartyIndex = partyIndex++,
                CurrentLife = Full,
                CurrentMana = Full,
                TotalXp = mXp,
                SkillXp = mSkillXp,
                MoveOrder = m.Orders.MoveOrder,
                AtkOrder = m.Orders.AtkOrder,
                TgtOrder = m.Orders.TgtOrder,
                ActiveSlot = ActiveSlot(m.SelectedActiveLocation),
            };
            var mSpells = new SpellbookSnapshot();
            var rows = Items(m, $"companion '{m.ScreenName}'", notes, mSpells, out var worn);
            // A companion's worn gear rides in Equipment, its pack in Inventory.
            cs.Inventory.AddRange(rows.Where(r => !r.Slot.StartsWith("equipped:", StringComparison.Ordinal)));
            foreach (var kv in worn) cs.Equipment[kv.Key] = kv.Value;
            cs.PrimarySpell = mSpells.PrimarySpell ?? "";
            cs.SecondarySpell = mSpells.SecondarySpell ?? "";
            cs.PlacedSpells = mSpells.Placed.Select(s => s ?? "").ToList();
            file.Party.Add(cs);
        }

        // ── Progress from the world ─────────────────────────────────────────
        if (world is not null)
        {
            foreach (var q in OriginalWorldFacts.Quests(world))
            {
                if (!q.Active && !q.Completed) continue;
                p.Quests.Add(new QuestSnapshot { Key = q.Name, State = q.Completed ? QuestState.Completed : QuestState.Active });
            }

            var ws = new WorldStateSnapshot();
            int counters = 0;
            foreach (var f in OriginalWorldFacts.Flags(world))
            {
                if (f.Value is "true" or "false") ws.Bools[f.Key] = f.Value == "true";
                else counters++;
            }
            if (counters > 0)
                notes.Add($"world: {counters} auditor counter(s) not carried (kill counts, to-hit bonuses); SiegeFX has no matching state");
            file.World = ws;

            // Placements the original will not create again: dead actors stay
            // dead, and a picked-up or used-up map item stays gone.
            var retired = OriginalWorldFacts.RetiredScids(world);
            foreach (var scid in retired.OrderBy(s => s))
                file.Actors.Add(new ActorSnapshot { Scid = scid, TemplateName = Template(objects, scid), IsDead = true });
            var gone = new SortedSet<uint>(retired);
            foreach (var (scid, node) in objects)
                if (node.Value("m_isinsideinventory")?.AsBool == true) gone.Add(scid);
            p.ConsumedInventoryScids = gone.ToList();
            notes.Add($"world: {p.Quests.Count} quest(s), {ws.Bools.Count} flag(s), {retired.Count} retired placement(s), " +
                      $"{gone.Count - retired.Count} carried map item(s)");
            notes.Add("world: doors, chests, levers and loot on the ground are not carried yet; they load as the map places them");
        }
        return new OriginalSaveImportResult(file, notes);
    }

    /// <summary>The member's XP: the overall ("uber") pool, the level it shows,
    /// and the four skill pools in SiegeFX's order (Melee, Ranged, NatureMagic,
    /// CombatMagic).</summary>
    static void ApplyProgress(OriginalPartyMember m, out long totalXp, out int level, out List<long> skillXp)
    {
        totalXp = (long)Math.Round(Xp(m, "uber"));
        level = Math.Max(1, (int)Math.Floor(m.Level));
        skillXp =
        [
            (long)Math.Round(Xp(m, "melee")),
            (long)Math.Round(Xp(m, "ranged")),
            (long)Math.Round(Xp(m, "nature_magic")),
            (long)Math.Round(Xp(m, "combat_magic")),
        ];
    }

    static double Skill(OriginalPartyMember m, string name) => m.Skills.TryGetValue(name, out var s) ? s.Level : 0;
    static double Xp(OriginalPartyMember m, string name) => m.Skills.TryGetValue(name, out var s) ? s.Xp : 0;

    /// <summary>il_active_* → SiegeFX's active slot (0 melee, 1 ranged,
    /// 2 Active Spell 1, 3 Active Spell 2); -1 keeps the game's default.</summary>
    public static int ActiveSlot(string location) => location switch
    {
        "il_active_melee_weapon" => 0,
        "il_active_ranged_weapon" => 1,
        "il_active_primary_spell" => 2,
        "il_active_secondary_spell" => 3,
        _ => -1,
    };

    /// <summary>One member's items as SiegeFX inventory rows: pack items with
    /// no slot, worn gear as <c>equipped:&lt;slot&gt;</c>, the active melee and
    /// ranged weapons in the weapon-hand and ranged slots. Spells go to
    /// <paramref name="book"/>. <paramref name="worn"/> holds the worn gear by
    /// <c>es_*</c> key, the shape a companion's save row uses.</summary>
    static List<LootEntrySnapshot> Items(OriginalPartyMember m, string who, List<string> notes,
                                                SpellbookSnapshot book, out Dictionary<string, string> worn)
    {
        var rows = new List<LootEntrySnapshot>();
        worn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in m.Inventory)
        {
            if (it.TemplateName.Length == 0) continue;
            string loc = it.InvLocation;
            if (loc.StartsWith("il_spell_", StringComparison.OrdinalIgnoreCase)) { book.Placed.Add(it.TemplateName); continue; }
            if (loc == "il_active_primary_spell") { book.PrimarySpell = it.TemplateName; continue; }
            if (loc == "il_active_secondary_spell") { book.SecondarySpell = it.TemplateName; continue; }
            string? slot = it.EquipSlot.Length > 0 ? it.EquipSlot
                : loc == "il_active_melee_weapon" ? "es_weapon_hand"
                : loc == "il_active_ranged_weapon" ? "es_ranged_weapon"
                : null;
            if (slot is not null)
            {
                slot = slot.ToLowerInvariant() switch
                {
                    "es_helm" => "es_head",
                    "es_gloves" => "es_forearms",
                    var s when s.StartsWith("es_ring", StringComparison.Ordinal) && worn.ContainsKey("es_ring") => s,
                    var s when s.StartsWith("es_ring", StringComparison.Ordinal) => "es_ring",
                    var s => s,
                };
                if (worn.ContainsKey(slot))
                {
                    notes.Add($"{who}: a second item for {slot} ({it.TemplateName}) goes to the pack");
                    rows.Add(new LootEntrySnapshot { Slot = "", Reference = it.TemplateName });
                    continue;
                }
                worn[slot] = it.TemplateName;
                rows.Add(new LootEntrySnapshot { Slot = "equipped:" + slot[3..], Reference = it.TemplateName });
                continue;
            }
            if (loc.Length > 0 && loc != "il_main")
                notes.Add($"{who}: {it.TemplateName} sat in {loc}, which the importer doesn't know; it goes to the pack");
            rows.Add(new LootEntrySnapshot { Slot = "", Reference = it.TemplateName });
        }
        return rows;
    }

    /// <summary>The map SCID of the companion's own placement: the one saved
    /// object made from its template, when there is exactly one.</summary>
    static uint CompanionScid(string template, IReadOnlyDictionary<uint, XferNode> objects, HashSet<uint> used)
    {
        uint found = 0;
        foreach (var (scid, node) in objects)
        {
            if (used.Contains(scid)) continue;
            if (!string.Equals(node.Value("m_datatemplate")?.AsText, template, StringComparison.OrdinalIgnoreCase)) continue;
            if (found != 0) return 0;   // two candidates: can't tell which one joined
            found = scid;
        }
        return found;
    }

    static string Template(IReadOnlyDictionary<uint, XferNode> objects, uint scid) =>
        objects.TryGetValue(scid, out var n) ? n.Value("m_datatemplate")?.AsText ?? "" : "";

    /// <summary>A Windows bitmap (24- or 32-bit, uncompressed, either row
    /// order) as SiegeFX's save thumbnail (<see cref="ThumbnailCodec"/>), rows
    /// bottom-up like the game's own captures. Null for anything else.</summary>
    public static byte[]? BmpToThumbnail(byte[] bmp)
    {
        if (bmp.Length < 54 || bmp[0] != 'B' || bmp[1] != 'M') return null;
        var s = bmp.AsSpan();
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(s[10..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(s[18..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(s[22..]);
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(s[30..]);
        if (width <= 0 || height == 0 || (bpp != 24 && bpp != 32) || compression is not (0 or 3)) return null;
        bool bottomUp = height > 0;
        height = Math.Abs(height);
        if (width > 4096 || height > 4096) return null;
        int bytesPer = bpp / 8;
        int stride = (width * bytesPer + 3) & ~3;
        if (dataOffset < 0 || (long)dataOffset + (long)stride * height > bmp.Length) return null;
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)   // y = 0 is the bottom row
        {
            int srcRow = dataOffset + (bottomUp ? y : height - 1 - y) * stride;
            int dst = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int src = srcRow + x * bytesPer;
                rgba[dst++] = bmp[src + 2];   // R
                rgba[dst++] = bmp[src + 1];   // G
                rgba[dst++] = bmp[src];       // B
                rgba[dst++] = 255;
            }
        }
        return ThumbnailCodec.Encode(width, height, rgba);
    }
}
