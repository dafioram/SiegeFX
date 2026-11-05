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
        var allObjects = world?.Find(OriginalWorldFacts.ObjectsPath)?.Children.ToList() ?? [];
        // The hero is an object made at run time (no map SCID) of the hero's template.
        var heroObject = Unique(allObjects, o => o.Value("m_scid")?.AsUInt32 == RunTimeScid && IsTemplate(o, hero.TemplateName));

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
                FaceIdx = 0, StyleIdx = 0, ColorIdx = 0, ShirtIdx = 0, PantsIdx = 0,
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
        p.Inventory.AddRange(Items(hero, $"hero '{hero.ScreenName}'", notes, spells, out var heroWorn));
        // The party file lists the worn spellbook but not the spells in it;
        // those are objects whose parent is the book, in the world tree.
        if (heroWorn.TryGetValue("es_spellbook", out var bookTemplate) && heroObject?.Value("m_goid")?.AsUInt32 is uint heroGoid)
        {
            var book = Unique(allObjects, o => o.Value("m_parent")?.AsUInt32 == heroGoid && IsTemplate(o, bookTemplate));
            if (book?.Value("m_goid")?.AsUInt32 is uint bookGoid)
            {
                var inBook = allObjects.Where(o => o.Value("m_parent")?.AsUInt32 == bookGoid)
                                       .Select(o => o.Value("m_datatemplate")?.AsText ?? "")
                                       .Where(t => t.Length > 0).ToList();
                foreach (var spell in inBook)
                {
                    if (spell == spells.PrimarySpell || spell == spells.SecondarySpell || spells.Placed.Contains(spell)) continue;
                    if (spells.PrimarySpell is null) spells.PrimarySpell = spell;
                    else spells.Placed.Add(spell);
                    // A spell the party file also listed loose stays in the book only.
                    int loose = p.Inventory.FindIndex(e => e.Slot.Length == 0 && e.Reference == spell);
                    if (loose >= 0) p.Inventory.RemoveAt(loose);
                }
                notes.Add($"hero: {inBook.Count} spell(s) in {bookTemplate}: {string.Join(", ", inBook)}" +
                          (inBook.Count > 0 ? $" (the first is Active Spell 1)" : ""));
            }
            else
                notes.Add($"hero: {bookTemplate} not found in the world tree; the spells in it are not carried");
        }
        if (spells.PrimarySpell is not null || spells.SecondarySpell is not null || spells.Placed.Count > 0)
            p.Spellbook = spells;
        // Life and mana as the original left them; the load clamps them to the
        // new maximums. The game sets the row's position from SpawnNode.
        if (heroObject is not null)
        {
            var (life, mana) = Vitals(heroObject);
            file.Actors.Add(new ActorSnapshot { Scid = HeroScid, TemplateName = hero.TemplateName,
                                                CurrentLife = life ?? Full, CurrentMana = mana ?? Full });
            if (life is null || mana is null)
                notes.Add($"hero: {(life is null ? "life" : "mana")} not found on the hero's object; starts full");
        }
        else if (world is not null)
            notes.Add($"hero: no single {hero.TemplateName} object in the world tree; life and mana start full");
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
            var obj = objects.TryGetValue(scid, out var placed) ? placed
                : Unique(allObjects, o => o.Value("m_scid")?.AsUInt32 == RunTimeScid && IsTemplate(o, m.TemplateName));
            var (mLife, mMana) = obj is null ? (null, null) : Vitals(obj);
            var cs = new CompanionSnapshot
            {
                Scid = scid,
                TemplateName = m.TemplateName,
                PartyIndex = partyIndex++,
                CurrentLife = mLife ?? Full,
                CurrentMana = mMana ?? Full,
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
            if (OriginalWorldFacts.Formation(world) is { } formation)
            {
                ws.PartyFormation = PartyFormation(formation) ?? "";
                if (ws.PartyFormation.Length == 0)
                    notes.Add($"world: formation '{formation}' has no SiegeFX match; the default stands");
            }
            file.World = ws;
            // The original's own tips are behind the player: don't replay
            // SiegeFX's from the first one. Its "show tips" option carries over.
            file.NextTipIndex = 15;
            file.TipsDisabled = OriginalWorldFacts.TutorialTips(world) == false;

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
        // The original counts levels from 0 (uber_level 0 at 0 XP), SiegeFX's
        // level math from 1: the same XP is one level higher here.
        level = Math.Max(1, (int)Math.Floor(m.Level) + 1);
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

    /// <summary>The original's formation name (a world/ai/formations.gas
    /// block) as SiegeFX's PartyFormation name; null for one it lacks.</summary>
    public static string? PartyFormation(string name) => name.ToLowerInvariant() switch
    {
        "circle" => "Circle",
        "column" => "Column",
        "double_column" => "DoubleColumn",
        "line" => "Row",
        "double_line" => "DoubleRow",
        "wedge" => "Pyramid",
        _ => null,
    };

    /// <summary>The SCID the original gives objects made at run time.</summary>
    const uint RunTimeScid = 0xfffffffe;

    static bool IsTemplate(XferNode o, string template) =>
        string.Equals(o.Value("m_datatemplate")?.AsText, template, StringComparison.OrdinalIgnoreCase);

    /// <summary>The one object matching, or null when none or several do.</summary>
    static XferNode? Unique(IEnumerable<XferNode> objects, Func<XferNode, bool> match)
    {
        XferNode? found = null;
        foreach (var o in objects)
        {
            if (!match(o)) continue;
            if (found is not null) return null;
            found = o;
        }
        return found;
    }

    /// <summary>Current life and mana from a creature's <c>aspect</c>
    /// component (the <c>m_componentcoll</c> entry named <c>aspect</c>, which
    /// holds <c>life</c> and <c>max_life</c>; <c>mana</c> is read the same way).
    /// Null where the value isn't there.</summary>
    public static (float? Life, float? Mana) Vitals(XferNode obj)
    {
        foreach (var comp in obj.Child("m_componentcoll")?.Children ?? [])
        {
            var nodes = comp.DescendantsAndSelf().ToList();
            if (!nodes.Any(n => n.Value("_name")?.AsText == "aspect")) continue;
            float? Find(string name) => nodes.Select(n => n.Value(name)?.AsSingle).FirstOrDefault(v => v is not null);
            return (Find("life"), Find("mana"));
        }
        return (null, null);
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
