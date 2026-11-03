// s18 — ground palettes: a coordinated set of surface assets per ROLE of a paved place, picked as one choice
// ("Palette") instead of one asset at a time. Generic on purpose: any type that lays hard ground (plaza, market
// square, transit plaza, a park's plaza and path surfaces, amphitheatre floors…) can adopt it with
// GroundPalettes.DefineControls + GroundPalettes.Resolve (docs/proposals/s18.md).
//
// Roles:   Field  — the main paving that fills the place
//          Border — the band that frames it (and kerbs, the ring round a focal pad)
//          Focal  — the pad under / round the focal point (rondel)
//          Path   — walks laid across the paving (entrance axes, flagstone paths)
//          Inlay  — the dark accent (thin inlay lines, tree pits, an outer ring)
//
// Same UX contract as the roads "road mix" (branch s16): one chooser at the top of the ground section,
// "Automatic" = the type's / preset's own palette, an individual surface pick on top of a palette makes it
// "Custom" (GroundPalettes.IsCustom). The per-role asset controls keep working as overrides, so saves stay
// stable: an empty role control means "from the palette".
//
// Asset ids and tones: verified in the player's catalog-dump (2026-10-14) and by eye on the mods' own icon
// images (…/.cache/Mods/pdx_mods/*/CustomSurfaces|Surfaces/*/icon.png; tone = icon mean colour). Every role
// ends its fallback chain in a vanilla surface whose look is closest (Tiles 01 light grey setts, Tiles 02 warm
// beige setts, Tiles 03 dark slab grid, Concrete 02 pale, Pavement 01 asphalt-dark).

using System;
using System.Collections.Generic;

namespace Placemaker.Core.Layers.Common
{
    /// <summary>Stones of one paved place, by role (see file header). Legacy palettes leave Focal/Path to Accent.</summary>
    public sealed class PavingPalette
    {
        public string Id;
        /// <summary>Field = main paving, Band = border band / kerbs / pad ring, Accent = light accent, Inlay = dark accent.</summary>
        public AssetRef Field, Band, Accent, Inlay;
        private AssetRef _focal, _path;
        /// <summary>Pad round the focal point (defaults to <see cref="Accent"/>).</summary>
        public AssetRef Focal { get => _focal.Id != null ? _focal : Accent; set => _focal = value; }
        /// <summary>Walks laid across the paving (defaults to <see cref="Accent"/>).</summary>
        public AssetRef Path { get => _path.Id != null ? _path : Accent; set => _path = value; }

        public PavingPalette Clone() => new PavingPalette { Id = Id, Field = Field, Band = Band, Accent = Accent, Inlay = Inlay, _focal = _focal, _path = _path };
    }

    /// <summary>Surface asset ids the palettes are made of (exact prefab names) and their measured tones.</summary>
    public static class GroundSurfaces
    {
        private const string G87 = "G87 Urban Surfaces Paving Pack ";
        private const string Eai = "EAI - Surface Pack ";
        private const string Rt = "Road Textures Surface Pack Pavement ";

        // G87 Urban Surfaces: "Paving Slab" = setts laid in fans; "Cobble Natural Stone" = natural stone setts/flags.
        public const string SlabGrey = G87 + "Tiles G87 Paving Slab A Grey Surface";            // mid grey fan setts
        public const string SlabBeige = G87 + "Tiles G87 Paving Slab A Beige Surface";          // ochre fan setts (the "mustard")
        public const string SlabWhite = G87 + "Tiles G87 Paving Slab A White Surface";          // pale grey fan setts
        public const string SlabBlack = G87 + "Tiles G87 Paving Slab B Black Surface";          // black granite fan setts
        public const string ArcGrey = G87 + "Pavement G87 Cobble Natural Stone 01 Surface";      // grey cobble arcs
        public const string Setts = G87 + "Pavement G87 Cobble Natural Stone 02 Surface";        // blue-grey granite setts
        public const string WarmCobble = G87 + "Pavement G87 Cobble Natural Stone 03 Surface";   // warm grey setts
        public const string ArcCobble = G87 + "Pavement G87 Cobble Natural Stone 04 Surface";    // dark cobble arcs
        public const string CrazyPaving = G87 + "Pavement G87 Cobble Natural Stone 07 Surface";  // pale sandstone flags
        public const string DarkFlags = G87 + "Pavement G87 Cobble Natural Stone 08 Surface";    // dark slate flags
        public const string ClayA = G87 + "Brick G87 Clay Paver A Surface";                      // muted brick herringbone
        public const string ClayC = G87 + "Brick G87 Clay Paver C Surface";                      // grey-brown herringbone
        public const string ClayD = G87 + "Brick G87 Clay Paver D Surface";                      // orange brick herringbone
        public const string ClayF = G87 + "Brick G87 Clay Paver F Surface";                      // dark red brick
        public const string ClayH = G87 + "Brick G87 Clay Paver H Surface";                      // brown herringbone
        public const string ClayM = G87 + "Brick G87 Clay Paver M Surface";                      // dark grey-brown pavers
        public const string Blocks = G87 + "Concrete G87 Regular Blocks 12 Surface";             // pale concrete blocks
        public const string LargeSlabs = G87 + "Concrete G87 Regular Blocks 13 Surface";         // large grey slabs
        public const string LargeSlabsDark = G87 + "Concrete G87 Regular Blocks 14 Surface";     // large grey slabs, darker joints
        // EAI Surface Pack.
        public const string GreyStone = Eai + "Tiles Grey Paving Stone Surface";                  // pale granite slabs
        public const string Herringbone = Eai + "Tiles Herringbone Grey Paving Stone 001 Surface";
        public const string Hexagonal = Eai + "Tiles Hexagonal Dark Grey Paving Stone Surface";
        public const string Polished = Eai + "Concrete Polished Concrete Surface";
        public const string RedBrick = Eai + "Brick Red Brick Surface";
        public const string WalkingStone = Eai + "Tiles Grey Walking stone Surface";
        // Road Textures Surface Pack.
        public const string StrippedBeige = Rt + "Concrete Stripped Beige Surface";                // smooth sandstone-beige
        public const string ConcreteDark = Rt + "Concrete Dark Surface";
        // Vanilla.
        public const string VTilesLight = "Tiles Surface 01", VTilesWarm = "Tiles Surface 02", VTilesDark = "Tiles Surface 03";
        public const string VConcrete = "Concrete Surface 01", VConcretePale = "Concrete Surface 02", VAsphalt = "Pavement Surface 01";
        public const string VGrass = "Grass Surface 01";

        private static readonly Dictionary<string, string> Tones = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SlabGrey] = "#69686c", [SlabBeige] = "#897452", [SlabWhite] = "#9e9c9d", [SlabBlack] = "#3d3b3c",
            [ArcGrey] = "#817e80", [Setts] = "#7c7f82", [WarmCobble] = "#8b8681", [ArcCobble] = "#5f5d64", [CrazyPaving] = "#a5a39b",
            [DarkFlags] = "#57535e", [ClayA] = "#906b5e", [ClayC] = "#897e78", [ClayD] = "#a95e44", [ClayF] = "#86463b", [ClayH] = "#815b3e",
            [ClayM] = "#676460", [Blocks] = "#928e8b", [LargeSlabs] = "#8d8b8c", [LargeSlabsDark] = "#8b888a",
            [GreyStone] = "#9ba5a5", [Herringbone] = "#9ea8a8", [Hexagonal] = "#5d6766", [Polished] = "#7e8583", [RedBrick] = "#a89084",
            [WalkingStone] = "#7b807c", [StrippedBeige] = "#978e86", [ConcreteDark] = "#565656",
            [VTilesLight] = "#74716c", [VTilesWarm] = "#81735d", [VTilesDark] = "#555454", [VConcrete] = "#7a797a", [VConcretePale] = "#888988",
            [VAsphalt] = "#464745", [VGrass] = "#3b4421",
        };

        /// <summary>Measured tone ("#rrggbb", the asset icon's mean colour) of a known surface, else null.</summary>
        public static string Tone(string id) => id != null && Tones.TryGetValue(id, out var t) ? t : null;

        /// <summary>Every surface id the palettes name (for tests / tools).</summary>
        public static IEnumerable<string> Known => Tones.Keys;
    }

    /// <summary>The player-facing ground palettes and the shared "Palette" control.</summary>
    public static class GroundPalettes
    {
        public const string Automatic = "auto";
        public const string Custom = "custom";
        /// <summary>Control id of the palette chooser (new in s18; every adopting type uses this id).</summary>
        public const string Control = "palette";
        /// <summary>Per-role override controls. Field keeps each type's existing id (plaza: "surface").</summary>
        public const string BorderControl = "borderSurface", FocalControl = "focalSurface", PathControl = "pathSurface", InlayControl = "inlaySurface";

        public const string Classic = "classic", Sandstone = "sandstone", BrickStone = "brickStone", Modern = "modern",
            Terracotta = "terracotta", Cobble = "cobble", Garden = "garden";

        private static AssetRef R(string id, params string[] fallbacks) => new AssetRef(id, fallbacks);
        private static PavingPalette P(string id, AssetRef field, AssetRef border, AssetRef focal, AssetRef path, AssetRef inlay) =>
            new PavingPalette { Id = id, Field = field, Band = border, Accent = focal, Focal = focal, Path = path, Inlay = inlay };

        /// <summary>The palettes in display order (Automatic and Custom are not palettes).</summary>
        public static readonly PavingPalette[] All =
        {
            // Calm greys: pale granite slabs framed in slate, a grey fan-set rondel, herringbone walks.
            P(Classic, R(GroundSurfaces.GreyStone, GroundSurfaces.SlabWhite, GroundSurfaces.VTilesLight), R(GroundSurfaces.DarkFlags, GroundSurfaces.Hexagonal, GroundSurfaces.VTilesDark),
                R(GroundSurfaces.ArcGrey, GroundSurfaces.SlabGrey, GroundSurfaces.VTilesLight), R(GroundSurfaces.Herringbone, GroundSurfaces.SlabWhite, GroundSurfaces.VConcretePale),
                R(GroundSurfaces.ArcCobble, GroundSurfaces.Hexagonal, GroundSurfaces.VTilesDark)),
            // Soft warm stone: smooth sandstone field, warm-grey paver border, pale flagstone rondel.
            P(Sandstone, R(GroundSurfaces.StrippedBeige, GroundSurfaces.WarmCobble, GroundSurfaces.VTilesWarm), R(GroundSurfaces.ClayM, GroundSurfaces.WalkingStone, GroundSurfaces.VTilesDark),
                R(GroundSurfaces.CrazyPaving, GroundSurfaces.SlabWhite, GroundSurfaces.VConcretePale), R(GroundSurfaces.WarmCobble, GroundSurfaces.VTilesWarm),
                R(GroundSurfaces.ClayC, GroundSurfaces.WalkingStone, GroundSurfaces.VTilesDark)),
            // Muted brick herringbone inside a granite-sett frame.
            P(BrickStone, R(GroundSurfaces.ClayA, GroundSurfaces.RedBrick, GroundSurfaces.VTilesWarm), R(GroundSurfaces.Setts, GroundSurfaces.GreyStone, GroundSurfaces.VTilesLight),
                R(GroundSurfaces.SlabGrey, GroundSurfaces.Setts, GroundSurfaces.VTilesLight), R(GroundSurfaces.Setts, GroundSurfaces.VTilesLight),
                R(GroundSurfaces.ClayF, GroundSurfaces.RedBrick, GroundSurfaces.VTilesDark)),
            // Large concrete slabs, black granite band, polished concrete pad.
            P(Modern, R(GroundSurfaces.LargeSlabs, GroundSurfaces.Polished, GroundSurfaces.VConcretePale), R(GroundSurfaces.SlabBlack, GroundSurfaces.Hexagonal, GroundSurfaces.VTilesDark),
                R(GroundSurfaces.Polished, GroundSurfaces.SlabWhite, GroundSurfaces.VConcrete), R(GroundSurfaces.Blocks, GroundSurfaces.VConcretePale),
                R(GroundSurfaces.Hexagonal, GroundSurfaces.ConcreteDark, GroundSurfaces.VTilesDark)),
            // The warm Mediterranean look (art pass a2 "warm"): ochre slabs, orange brick bands. An option, never a default.
            P(Terracotta, R(GroundSurfaces.SlabBeige, GroundSurfaces.VTilesWarm), R(GroundSurfaces.ClayD, GroundSurfaces.RedBrick, GroundSurfaces.VTilesDark),
                R(GroundSurfaces.SlabWhite, GroundSurfaces.Polished, GroundSurfaces.VConcretePale), R(GroundSurfaces.SlabWhite, GroundSurfaces.Polished, GroundSurfaces.VConcretePale),
                R(GroundSurfaces.ClayF, GroundSurfaces.RedBrick, GroundSurfaces.VTilesDark)),
            // Old town: granite setts, slate flag border, cobble-fan rondel, pale flagstone walks.
            P(Cobble, R(GroundSurfaces.Setts, GroundSurfaces.GreyStone, GroundSurfaces.VTilesLight), R(GroundSurfaces.DarkFlags, GroundSurfaces.WalkingStone, GroundSurfaces.VTilesDark),
                R(GroundSurfaces.ArcGrey, GroundSurfaces.SlabGrey, GroundSurfaces.VTilesLight), R(GroundSurfaces.CrazyPaving, GroundSurfaces.SlabWhite, GroundSurfaces.VConcretePale),
                R(GroundSurfaces.ArcCobble, GroundSurfaces.Hexagonal, GroundSurfaces.VTilesDark)),
            // Garden: soft grey-brown brick, warm sett border, pale flagstone pads and walks.
            P(Garden, R(GroundSurfaces.ClayC, GroundSurfaces.Herringbone, GroundSurfaces.VTilesWarm), R(GroundSurfaces.WarmCobble, GroundSurfaces.Setts, GroundSurfaces.VTilesLight),
                R(GroundSurfaces.CrazyPaving, GroundSurfaces.SlabWhite, GroundSurfaces.VConcretePale), R(GroundSurfaces.CrazyPaving, GroundSurfaces.SlabWhite, GroundSurfaces.VConcretePale),
                R(GroundSurfaces.ClayM, GroundSurfaces.Hexagonal, GroundSurfaces.VTilesDark)),
        };

        /// <summary>The calm default when a type has nothing more specific.</summary>
        public static PavingPalette Default => All[0];

        public static PavingPalette Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in All) if (p.Id == id) return p;
            return null;
        }

        public static PavingPalette Get(string id) => Find(id) ?? Default;

        /// <summary>Choice values of the palette control: Automatic, every palette, Custom.</summary>
        public static string[] Values()
        {
            var v = new string[All.Length + 2];
            v[0] = Automatic;
            for (int i = 0; i < All.Length; i++) v[i + 1] = All[i].Id;
            v[All.Length + 1] = Custom;
            return v;
        }

        public static string LabelKey(string id) => "Placemaker.Palette." + id;
        /// <summary>The visible one-line description ("Pale granite slabs · slate border").</summary>
        public static string DetailKey(string id) => "Placemaker.Palette." + id + ".Detail";

        /// <summary>
        /// Adds the palette chooser (at the current position: put it first in the ground section) and the
        /// per-role override asset controls. <paramref name="fieldControl"/> is the type's existing main
        /// paving control (not added here). Option labels and details are shared by every type.
        /// </summary>
        public static void DefineControls(ControlSet c, string group, string fieldControl, ControlTier overrides = ControlTier.More)
        {
            var ch = c.Choice(Control, Automatic, ControlTier.Essential, group, Values());
            ch.LabelKey = "Placemaker.Palette";
            ch.TooltipKey = "Placemaker.Palette.Tooltip";
            foreach (var o in ch.Options)
            {
                // s18 (on s16's option cards): the description is the card's visible line, the swatch its picture.
                o.LabelKey = LabelKey(o.Value);
                o.DetailKey = DetailKey(o.Value);
                var p = Find(o.Value);
                if (p != null) o.Swatch = OptionSwatch(p);
            }
            c.Fixed(Control);
            // The palette a Custom mix started from (written by OnValueSet / Derive); hidden, never rolled.
            var b = c.Choice(BaseControl, Automatic, ControlTier.More, group, BaseValues()).When(Control + "=legacy");
            b.LabelKey = "Placemaker.Palette";
            foreach (var o in b.Options) o.LabelKey = LabelKey(o.Value);
            c.Fixed(BaseControl);
            DefineRoleControls(c, group, overrides);
        }

        /// <summary>Hidden control: the palette a Custom mix is based on.</summary>
        public const string BaseControl = "paletteBase";

        private static string[] BaseValues()
        {
            var v = new string[All.Length + 1];
            v[0] = Automatic;
            for (int i = 0; i < All.Length; i++) v[i + 1] = All[i].Id;
            return v;
        }

        /// <summary>Relative stripe widths of the swatch (the UI draws a tiny square: field, band round it, pad, walk, inlay).</summary>
        public static List<OptionSwatch> OptionSwatch(PavingPalette p)
        {
            var list = new List<OptionSwatch>();
            foreach (var (role, color) in Swatch(p))
                list.Add(new OptionSwatch { Role = role, Color = color, Width = role == "field" ? 10f : role == "focal" ? 5f : role == "border" ? 1.5f : role == "path" ? 2f : 0.8f });
            return list;
        }

        /// <summary>
        /// (ILinkedControls.OnValueSet for palette-aware layers) Picking a palette starts from its own stones: the role
        /// overrides are cleared and it becomes the base. Returns the ids written.
        /// </summary>
        public static string[] OnValueSet(SettingsBag s, string controlId, string fieldControl)
        {
            if (s == null || controlId != Control) return Array.Empty<string>();
            string id = s.S(Control, Automatic);
            if (id == Custom) return Array.Empty<string>();
            var ids = new List<string>(ClearOverrides(s, fieldControl));
            s.Values[BaseControl] = Find(id) != null ? id : Automatic;
            ids.Add(BaseControl);
            return ids.ToArray();
        }

        /// <summary>
        /// (ILinkedControls.Derive) The chooser reads what the ground is: Custom while any role is overridden (remembering
        /// the palette it started from), else that palette / Automatic.
        /// </summary>
        public static void Derive(SettingsBag s, string fieldControl)
        {
            if (s == null) return;
            string id = s.S(Control, Automatic);
            bool overridden = IsOverridden(s, fieldControl);
            if (overridden && id != Custom)
            {
                s.Values[BaseControl] = Find(id) != null ? id : Automatic;
                s.Values[Control] = Custom;
            }
            else if (!overridden && id == Custom)
            {
                string b = s.S(BaseControl, Automatic);
                s.Values[Control] = Find(b) != null ? b : Automatic;
            }
        }

        private static bool IsOverridden(SettingsBag s, string fieldControl)
        {
            foreach (var id in new[] { fieldControl, BorderControl, FocalControl, PathControl, InlayControl })
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(s.S(id, ""))) return true;
            return false;
        }

        /// <summary>Per-role override controls (empty = from the palette).</summary>
        public static void DefineRoleControls(ControlSet c, string group, ControlTier tier = ControlTier.More)
        {
            foreach (var id in new[] { BorderControl, FocalControl, PathControl, InlayControl })
            {
                var a = c.Asset(id, AssetCategories.SurfacePaving, tier, group);
                a.LabelKey = "Placemaker.Palette.Role." + id;
                a.TooltipKey = a.LabelKey + ".Tooltip";
            }
        }

        /// <summary>
        /// The palette in effect: the chosen one (Automatic / Custom / unknown → <paramref name="autoId"/>), with
        /// every role the player picked a surface for replaced by that pick (the palette's own stone stays its fallback).
        /// </summary>
        public static PavingPalette Resolve(SettingsBag s, string autoId, string fieldControl)
        {
            string id = s?.S(Control, Automatic) ?? Automatic;
            if (id == Custom) id = s.S(BaseControl, Automatic);   // a Custom mix keeps the stones of the palette it started from
            var basis = Find(id) ?? Find(autoId) ?? Default;
            var p = basis.Clone();
            if (s == null) return p;
            p.Field = Override(s.S(fieldControl ?? "", ""), basis.Field);
            p.Band = Override(s.S(BorderControl, ""), basis.Band);
            p.Focal = Override(s.S(FocalControl, ""), basis.Focal);
            p.Accent = p.Focal;
            p.Path = Override(s.S(PathControl, ""), basis.Path);
            p.Inlay = Override(s.S(InlayControl, ""), basis.Inlay);
            return p;
        }

        private static AssetRef Override(string chosen, AssetRef designed)
        {
            if (string.IsNullOrEmpty(chosen)) return designed;
            var fb = new List<string>();
            if (!string.IsNullOrEmpty(designed.Id)) fb.Add(designed.Id);
            if (designed.Fallbacks != null) fb.AddRange(designed.Fallbacks);
            return new AssetRef(chosen, fb.ToArray());
        }

        /// <summary>True when the player replaced any role's surface: the palette then reads "Custom".</summary>
        public static bool IsCustom(SettingsBag s, string fieldControl)
        {
            if (s == null) return false;
            foreach (var id in new[] { fieldControl, BorderControl, FocalControl, PathControl, InlayControl })
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(s.S(id, ""))) return true;
            return s.S(Control, Automatic) == Custom;
        }

        /// <summary>What the chooser should show: the chosen palette, Automatic, or Custom when a role was overridden.</summary>
        public static string Displayed(SettingsBag s, string fieldControl)
        {
            if (s != null && IsOverridden(s, fieldControl)) return Custom;
            string id = s?.S(Control, Automatic) ?? Automatic;
            if (id == Custom) id = s.S(BaseControl, Automatic);
            return Find(id) != null ? id : Automatic;
        }

        /// <summary>Clears the role overrides (picking a palette starts from its own stones, like a road mix).</summary>
        public static string[] ClearOverrides(SettingsBag s, string fieldControl)
        {
            var ids = new List<string>();
            if (s == null) return ids.ToArray();
            foreach (var id in new[] { fieldControl, BorderControl, FocalControl, PathControl, InlayControl })
                if (!string.IsNullOrEmpty(id)) { s.Values[id] = ""; ids.Add(id); }
            return ids.ToArray();
        }

        /// <summary>Role → measured tone of the palette's first stone (for an option swatch; roles field, border, focal, path, inlay).</summary>
        public static List<(string role, string color)> Swatch(PavingPalette p)
        {
            string T(AssetRef a)
            {
                var t = GroundSurfaces.Tone(a.Id);
                if (t == null && a.Fallbacks != null) foreach (var f in a.Fallbacks) if ((t = GroundSurfaces.Tone(f)) != null) break;
                return t ?? "#888888";
            }
            return new List<(string, string)> { ("field", T(p.Field)), ("border", T(p.Band)), ("focal", T(p.Focal)), ("path", T(p.Path)), ("inlay", T(p.Inlay)) };
        }
    }
}
