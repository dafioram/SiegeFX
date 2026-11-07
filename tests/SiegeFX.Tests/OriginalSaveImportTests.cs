using System.Buffers.Binary;
using SiegeFX.Core.Actors;
using SiegeFX.Core.Assets;
using SiegeFX.Core.Save;
using SiegeFX.OriginalSaves;

namespace SiegeFX.Tests;

/// <summary>Converting an original-game save into a SiegeFX save. All data is
/// made up for the test; field names and shapes follow
/// docs/original-save-format.md.</summary>
public class OriginalSaveImportTests
{
    const string Info = """
        [info]
        {
            hero_name = "Wren";
            map_name = map_world;
            map_screen_name = "Kingdom of Ehb";
            region = fh_r2;
            screen_name = "Wren (0-10-00)";
            single_player = true;
            teleport = map_world:n:1.5,0.25,-2,0x0a0b0c0d;
            time_elapsed = 600;
        }
        """;

    const string Party = """
        [party]
        {
            [members]
            {
                [0]
                {
                    [member]
                    {
                        combat_orders = co_limited;
                        custom_head = test_hlmt_head-03;
                        focus_orders = fo_strongest;
                        movement_orders = mo_hold;
                        screen_name = "Wren";
                        selected_active_location = il_active_ranged_weapon;
                        template_name = farmgirl;
                        [inventory]
                        {
                            [0] { equip_slot = es_chest; inv_location = il_main; template_name = test_tunic; }
                            [1] { inv_location = il_active_melee_weapon; template_name = test_dagger; }
                            [2] { inv_location = il_active_ranged_weapon; template_name = test_bow; }
                            [3] { grid_x = 255; grid_y = 30; inv_location = il_main; template_name = test_potion; }
                            [4] { inv_location = il_active_primary_spell; template_name = test_spell_a; }
                            [5] { inv_location = il_spell_1; template_name = test_spell_b; }
                            [6] { equip_slot = es_ring_0; inv_location = il_main; template_name = test_ring_a; }
                            [7] { equip_slot = es_ring_1; inv_location = il_main; template_name = test_ring_b; }
                            [8] { inv_location = il_somewhere_new; template_name = test_odd; }
                            [9] { equip_slot = es_spellbook; inv_location = il_main; template_name = test_book; }
                            [10] { grid_x = 287; grid_y = 30; inv_location = il_main; template_name = test_spell_c; }
                        }
                        [skills]
                        {
                            combat_magic_level = 1.01;
                            combat_magic_xp = 4;
                            dexterity_level = 13.5;
                            intelligence_level = 10.25;
                            melee_level = 2.2;
                            melee_xp = 120.4;
                            nature_magic_level = 0;
                            nature_magic_xp = 0;
                            ranged_level = 3.4;
                            ranged_xp = 300.6;
                            strength_level = 12;
                            uber_level = 3.7;
                            uber_xp = 425;
                        }
                        [skins] { 0 = test_skin_03,test_hair_002; 1 = test_pant_005,test_shrt_004; }
                    }
                }
                [1]
                {
                    [member]
                    {
                        combat_orders = co_free;
                        focus_orders = fo_closest;
                        movement_orders = mo_free;
                        screen_name = "Ally";
                        template_name = test_ally;
                        [inventory]
                        {
                            [0] { equip_slot = es_weapon_hand; inv_location = il_main; template_name = test_axe; }
                            [1] { grid_x = 287; grid_y = 30; inv_location = il_main; template_name = test_bread; }
                        }
                        [skills] { melee_level = 2; melee_xp = 50; uber_level = 2.1; uber_xp = 60; }
                    }
                }
                [2]
                {
                    [member]
                    {
                        screen_name = "Mule";
                        template_name = test_mule;
                        [inventory] { [0] { grid_x = 255; grid_y = 30; inv_location = il_main; template_name = test_ore; } }
                    }
                }
            }
            [party] { gold = 77; hero_index = 0; }
        }
        """;

    /// <summary>A world tree with two quests, three flags, saved objects (the
    /// ally's own placement with its life, a map item carried in a pack, the
    /// hero with life and mana, the hero's spellbook and two spells in it)
    /// and two retired placements, with tips switched off.</summary>
    static XferTree World()
    {
        static XferBuilder.Node Quest(string i, string name, bool active, bool done) => new(i, new XferBuilder.Node("_value",
            XferBuilder.Text("sname", name), XferBuilder.Bool("bactive", active), XferBuilder.Bool("bcompleted", done)));
        static XferBuilder.Node Flag(string i, string key, bool b, int n) => new(i, XferBuilder.Text("m_key", key),
            XferBuilder.Int("m_int", n), XferBuilder.Bool("m_bool", b), XferBuilder.Float("m_float", 0), XferBuilder.Text("m_string", ""));
        static XferBuilder.Node Aspect(float life, float? mana) => new("m_componentcoll",
            new XferBuilder.Node("0", XferBuilder.Text("_name", "physique"), XferBuilder.Float("life", 999)),
            new XferBuilder.Node("1", [XferBuilder.Text("_name", "aspect"), XferBuilder.Float("life", life),
                .. mana is { } m ? new[] { XferBuilder.Float("mana", m) } : []]));
        const int RunTime = unchecked((int)0xfffffffe);
        static XferBuilder.Val Pos(float x, float y, float z, uint node)
        {
            var b = new byte[16];
            BinaryPrimitives.WriteSingleLittleEndian(b, x);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), z);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), node);
            return new XferBuilder.Val("position", b);
        }
        // An object made at run time lying in the world: placement, and a gui
        // icon when it's an item (a monster has none).
        static XferBuilder.Node Ground(string i, string template, bool item, uint node, params XferBuilder.Entry[] more) =>
            new(i, [XferBuilder.Int("m_scid", RunTime), XferBuilder.Int("m_parent", 0), XferBuilder.Text("m_datatemplate", template),
                new XferBuilder.Node("m_componentcoll", [
                    new XferBuilder.Node("0", XferBuilder.Text("_name", "placement"), Pos(-7.25f, 0f, -11.75f, node)),
                    .. item ? new[] { new XferBuilder.Node("1", XferBuilder.Text("_name", "gui"), XferBuilder.Text("inventory_icon", "icon")) } : [],
                    .. more])]);
        var (idx, dat) = XferBuilder.Build(new XferBuilder.Node("",
            new XferBuilder.Node("uigame", XferBuilder.Bool("tutorial_tips", false)),
            new XferBuilder.Node("world",
                new XferBuilder.Node("m_pvictory", new XferBuilder.Node("m_quests",
                    Quest("0", "test_quest_open", true, false),
                    Quest("1", "test_quest_done", false, true),
                    Quest("2", "test_quest_unseen", false, false))),
                new XferBuilder.Node("m_pgameauditor", new XferBuilder.Node("m_entries",
                    Flag("0", "test_talked", true, 0),
                    Flag("1", "test_not_yet", false, 0),
                    Flag("2", "test_kills", false, 9)))),
            new XferBuilder.Node("godb",
                new XferBuilder.Node("m_globaldb",
                    new XferBuilder.Node("0", XferBuilder.Int("m_scid", 0x01c00b40), XferBuilder.Text("m_datatemplate", "test_ally"),
                        Aspect(42, null)),
                    Ground("8", "gold", true, 0xd037f9ef,
                        new XferBuilder.Node("2", XferBuilder.Text("_name", "aspect"), XferBuilder.Int("gold_value", 6))),
                    Ground("9", "test_dropped_sword", true, 0x0a0b0c0d),
                    Ground("10", "test_krug_spawned", false, 0x0a0b0c0d),
                    new XferBuilder.Node("7", XferBuilder.Int("m_scid", RunTime), XferBuilder.Text("m_datatemplate", "test_party"),
                        new XferBuilder.Node("m_componentcoll", new XferBuilder.Node("1",
                            new XferBuilder.Node("m_pformation", XferBuilder.Text("m_name", "wedge"))))),
                    new XferBuilder.Node("3", XferBuilder.Int("m_goid", 0x09000087), XferBuilder.Int("m_scid", RunTime),
                        XferBuilder.Text("m_datatemplate", "farmgirl"), Aspect(31.5f, 12)),
                    new XferBuilder.Node("4", XferBuilder.Int("m_goid", 0x09000100), XferBuilder.Int("m_scid", RunTime),
                        XferBuilder.Int("m_parent", 0x09000087), XferBuilder.Text("m_datatemplate", "test_book")),
                    new XferBuilder.Node("5", XferBuilder.Int("m_goid", 0x09000101), XferBuilder.Int("m_scid", RunTime),
                        XferBuilder.Int("m_parent", 0x09000100), XferBuilder.Text("m_datatemplate", "test_spell_c")),
                    new XferBuilder.Node("6", XferBuilder.Int("m_goid", 0x09000102), XferBuilder.Int("m_scid", RunTime),
                        XferBuilder.Int("m_parent", 0x09000100), XferBuilder.Text("m_datatemplate", "test_spell_d")),
                    new XferBuilder.Node("1", XferBuilder.Int("m_scid", 0x01c00274), XferBuilder.Text("m_datatemplate", "test_potion"),
                        XferBuilder.Bool("m_isinsideinventory", true)),
                    new XferBuilder.Node("2", XferBuilder.Int("m_scid", 0x01c005ca), XferBuilder.Text("m_datatemplate", "test_krug"))),
                new XferBuilder.Node("m_retiredscidcoll",
                    new XferBuilder.Node("0", XferBuilder.Int("_value", 0x01c005ca)),
                    new XferBuilder.Node("1", XferBuilder.Int("_value", 0x01c00008))))));
        return XferTree.Load(idx, dat);
    }

    static OriginalSaveImportResult Import(string info = Info, XferTree? world = null, byte[]? thumb = null) =>
        OriginalSaveImport.Import(OriginalSaveInfo.Parse(GasDocument.Parse(info)),
                                  OriginalParty.Parse(GasDocument.Parse(Party)), world, thumb,
                                  "Wren.dssave", new DateTime(2026, 11, 5, 12, 0, 0));

    [Fact]
    public void The_save_lands_in_the_saved_region_with_the_hero_on_the_saved_node()
    {
        var s = Import(world: World()).Save;
        Assert.Equal("/world/maps/map_world/regions/fh_r2", s.RegionPath);
        Assert.Equal(("Original: Wren (0-10-00)", "Wren", "Kingdom of Ehb", 600.0), (s.DisplayName, s.HeroName, s.MapName, s.ElapsedSeconds));
        Assert.Equal("Wren.dssave", s.ImportedFrom);
        Assert.Equal(0x0a0b0c0du, s.Player!.SpawnNode);
        Assert.Equal(new Vec3(1.5f, 0.25f, -2f), s.Player.SpawnOffset);
        Assert.Equal(OriginalSaveImport.HeroScid, s.Player.Scid);
    }

    [Fact]
    public void The_hero_keeps_xp_attributes_gold_orders_and_gender()
    {
        var p = Import(world: World()).Save.Player!;
        Assert.Equal((425L, 4), (p.TotalXp, p.Level));   // uber_level 3.7, counted from 1
        Assert.Equal([120L, 301L, 0L, 4L], p.SkillXp);   // Melee, Ranged, NatureMagic, CombatMagic
        Assert.Equal((12f, 13.5f, 10.25f), (p.Strength, p.Dexterity, p.Intelligence));
        Assert.Equal(77L, p.Gold);
        Assert.Equal((2, 1, 1), (p.MoveOrder, p.AtkOrder, p.TgtOrder));
        Assert.Equal(1, p.ActiveSlot);
        Assert.Equal("girl", p.Variant!.Gender);
        Assert.Equal(0, p.Variant.FaceIdx);
    }

    [Fact]
    public void The_look_is_carried_by_name()
    {
        // custom_head and both [skins] rows (skin + hair; pants + shirt), as
        // the original writes them; the game places the names in its creator.
        var v = Import(world: World()).Save.Player!.Variant!;
        Assert.Equal("test_hlmt_head-03", v.HeadMesh);
        Assert.Equal(("test_skin_03", "test_hair_002"), (v.SkinTexture, v.HairTexture));
        Assert.Equal(("test_shrt_004", "test_pant_005"), (v.ShirtTexture, v.PantsTexture));
    }

    [Fact]
    public void Worn_gear_active_weapons_and_spells_land_in_their_slots()
    {
        var r = Import(world: World());
        var p = r.Save.Player!;
        string? Worn(string slot) => p.Inventory.FirstOrDefault(e => e.Slot == "equipped:" + slot)?.Reference;
        Assert.Equal("test_tunic", Worn("chest"));
        Assert.Equal("test_dagger", Worn("weapon_hand"));
        Assert.Equal("test_bow", Worn("ranged_weapon"));
        Assert.Equal("test_ring_a", Worn("ring"));
        Assert.Equal("test_ring_b", Worn("ring_1"));
        // test_spell_c sits in the book in the world tree, so it leaves the pack.
        Assert.Equal(["test_potion", "test_odd"], p.Inventory.Where(e => e.Slot == "").Select(e => e.Reference));
        Assert.Equal("test_book", Worn("spellbook"));
        Assert.Equal("test_spell_a", p.Spellbook!.PrimarySpell);
        Assert.Equal(["test_spell_b", "test_spell_c", "test_spell_d"], p.Spellbook.Placed);
        Assert.Contains(r.Notes, n => n.Contains("il_somewhere_new"));
        Assert.DoesNotContain(r.Notes, n => n.Contains("appearance"));   // the look is carried
    }

    [Fact]
    public void Companions_join_on_their_own_placement_or_as_new_actors()
    {
        var r = Import(world: World());
        var (ally, mule) = (r.Save.Party[0], r.Save.Party[1]);
        Assert.Equal((0x01c00b40u, "test_ally", 1), (ally.Scid, ally.TemplateName, ally.PartyIndex));
        Assert.Equal((60L, 50L), (ally.TotalXp, ally.SkillXp[0]));
        Assert.Equal((0, 0, 0), (ally.MoveOrder, ally.AtkOrder, ally.TgtOrder));
        Assert.Equal("test_axe", ally.Equipment["es_weapon_hand"]);
        Assert.Equal(["test_bread"], ally.Inventory.Select(e => e.Reference));   // worn gear stays out of the pack
        Assert.Equal(default, ally.Position);   // the game stands them beside the hero
        Assert.Equal((OriginalSaveImport.ImportedCompanionScidBase + 2, 2), (mule.Scid, mule.PartyIndex));
        Assert.Contains(r.Notes, n => n.Contains("'Mule'"));
    }

    [Fact]
    public void Quests_flags_and_retired_placements_come_from_the_world()
    {
        var s = Import(world: World()).Save;
        Assert.Equal([("test_quest_open", QuestState.Active), ("test_quest_done", QuestState.Completed)],
                     s.Player!.Quests.Select(q => (q.Key, q.State)));
        Assert.Equal(new Dictionary<string, bool> { ["test_talked"] = true, ["test_not_yet"] = false }, s.World!.Bools);
        Assert.Equal([(0x01c00008u, ""), (0x01c005cau, "test_krug")],
                     s.Actors.Where(a => a.IsDead).Select(a => (a.Scid, a.TemplateName)));
        // Retired placements and the map item carried in the pack stay gone.
        Assert.Equal([0x01c00008u, 0x01c00274u, 0x01c005cau], s.Player.ConsumedInventoryScids);
    }

    [Fact]
    public void Life_mana_and_tips_come_from_the_world()
    {
        var s = Import(world: World()).Save;
        var hero = Assert.Single(s.Actors, a => a.Scid == OriginalSaveImport.HeroScid);
        Assert.Equal((31.5f, 12f, false), (hero.CurrentLife, hero.CurrentMana, hero.IsDead));
        Assert.Equal(default, hero.Position);   // the game places it from SpawnNode
        Assert.Equal(42f, s.Party[0].CurrentLife);
        Assert.True(s.Party[0].CurrentMana > 1e6f);   // no mana saved: full
        Assert.True(s.Party[1].CurrentLife > 1e6f);   // the mule has no object: full
        Assert.True(s.TipsDisabled);
        Assert.Equal(15, s.NextTipIndex);
    }

    [Theory]
    [InlineData("wedge", "Pyramid")]
    [InlineData("circle", "Circle")]
    [InlineData("double_line", "DoubleRow")]
    [InlineData("line", "Row")]
    [InlineData("double_column", "DoubleColumn")]
    [InlineData("column", "Column")]
    [InlineData("spiral", null)]
    public void The_original_formation_names_map_to_siegefx(string original, string? siegefx) =>
        Assert.Equal(siegefx, OriginalSaveImport.PartyFormation(original));

    [Fact]
    public void The_formation_comes_from_the_party_object()
    {
        Assert.Equal("wedge", OriginalWorldFacts.Formation(World()));
        Assert.Equal("Pyramid", Import(world: World()).Save.World!.PartyFormation);
    }

    [Fact]
    public void Loot_on_the_ground_becomes_piles_on_its_node()
    {
        var piles = Import(world: World()).Save.LootPiles;
        Assert.Equal(2, piles.Count);   // the spawned monster isn't loot
        Assert.Equal((0xd037f9efu, new Vec3(-7.25f, 0f, -11.75f)), (piles[0].Node, piles[0].NodeOffset));
        Assert.Equal(("gold", "6-6"), (piles[0].Entries[0].Slot, piles[0].Entries[0].Reference));
        Assert.Equal(("", "test_dropped_sword"), (piles[1].Entries[0].Slot, piles[1].Entries[0].Reference));
        Assert.Equal(default, piles[0].Position);   // the game places it from the node
    }

    [Fact]
    public void A_character_file_without_a_world_starts_the_campaign()
    {
        var r = Import();
        Assert.Equal(OriginalSaveImport.CampaignStartRegion, r.Save.RegionPath);
        Assert.Equal(0u, r.Save.Player!.SpawnNode);
        Assert.Empty(r.Save.Actors);
        Assert.Null(r.Save.World);
        Assert.Equal((0, false), (r.Save.NextTipIndex, r.Save.TipsDisabled));   // a new campaign keeps the tips
        Assert.Contains(r.Notes, n => n.Contains("no world"));
    }

    [Fact]
    public void A_legends_of_aranna_save_is_refused()
    {
        var loa = Info.Replace("map_name = map_world;", "map_name = map_expansion;");
        Assert.Throws<NotSupportedException>(() => Import(loa));
    }

    [Fact]
    public void An_import_round_trips_through_the_save_file()
    {
        var back = SaveStore.Deserialize(SaveStore.Serialize(Import(world: World()).Save))!;
        Assert.Equal("Wren.dssave", back.ImportedFrom);
        Assert.Equal(0x0a0b0c0du, back.Player!.SpawnNode);
        Assert.Equal(new Vec3(1.5f, 0.25f, -2f), back.Player.SpawnOffset);
    }

    /// <summary>A 2×2 bitmap: bottom row red, blue; top row green, white.</summary>
    static byte[] Bmp(int bpp, bool topDown)
    {
        int bytesPer = bpp / 8, stride = (2 * bytesPer + 3) & ~3;
        var b = new byte[54 + stride * 2];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), topDown ? -2 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)bpp);
        byte[][] bottom = [[0, 0, 255], [255, 0, 0]];       // BGR: red, blue
        byte[][] top = [[0, 255, 0], [255, 255, 255]];      // green, white
        void Row(int fileRow, byte[][] px)
        {
            for (int x = 0; x < 2; x++) px[x].CopyTo(b, 54 + fileRow * stride + x * bytesPer);
        }
        Row(topDown ? 1 : 0, bottom);
        Row(topDown ? 0 : 1, top);
        return b;
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(24, true)]
    [InlineData(32, false)]
    public void A_thumbnail_bitmap_becomes_bottom_up_rgba(int bpp, bool topDown)
    {
        Assert.True(ThumbnailCodec.TryDecode(OriginalSaveImport.BmpToThumbnail(Bmp(bpp, topDown)), out var w, out var h, out var rgba));
        Assert.Equal((2, 2), (w, h));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255, 255, 255, 255, 255 }, rgba);
    }

    [Fact]
    public void A_thumbnail_that_is_not_a_plain_bitmap_is_left_out()
    {
        var r = Import(world: World(), thumb: [1, 2, 3]);
        Assert.Null(r.Save.Thumbnail);
        Assert.Contains(r.Notes, n => n.StartsWith("thumbnail"));
    }
}
