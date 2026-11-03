// Pond layer (Fill, hidden from the Make row): turns a basin ring into water — permanent
// terraform plus a water source, with the permanent-change warning. Used by the park's "pond"
// feature: the park shapes the basin between its paths (ParkLakePlanner) and hands it over as
// the "place:pond" region. Grand park wave (g2): islands with bridges, fountain jets, a boathouse
// with rowing boats and a lakeside promenade (AddOns/Water/LakeDressing.cs).

using System;
using Placemaker.Core.Layers.AddOns.Water;
using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.Park
{
    [AreaGenerator(45, Hidden = true)]
    public sealed class PondLayer : LayerBase
    {
        public const string LayerId = "pond";

        protected override string Area => "Pond";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Water.svg";
        public override LayerKind Kind => LayerKind.Fill;
        public override string[] Accepts => new[] { ParkLayer.PondPlace, "water" };
        public override float MinArea => 100f;

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("depth", 3, 1, 8, 0.5, "m", ControlTier.Essential, "water").Tip();
            c.Slider("islands", 1, 0, 3, 1, "count", ControlTier.Essential, "islands").Tip();
            c.Toggle("bridges", true, ControlTier.More, "islands").Tip();
            c.Toggle("islandTrees", true, ControlTier.More, "islands");
            c.Slider("jets", 0, 0, 8, 1, "count", ControlTier.More, "jets").Tip();
            c.Choice("jetLayout", "line", ControlTier.More, "jets", LakeDressing.JetLayouts);
            c.Toggle("boathouse", false, ControlTier.More, "boating").Tip();
            c.Slider("rowboats", 0, 0, 8, 1, "count", ControlTier.More, "boating").Tip();
            c.Toggle("promenade", false, ControlTier.More, "promenade").Tip();
            c.Slider("promWidth", 8, 4, 14, 0.5, "m", ControlTier.More, "promenade").When("promenade=true");
            c.Slider("benchSpacing", 14, 0, 40, 1, "m", ControlTier.More, "promenade").Layout(4).When("promenade=true");
            c.Toggle("lamps", true, ControlTier.More, "promenade").When("promenade=true");
            c.Toggle("connect", true, ControlTier.More, "promenade").Tip();
            c.Fixed("depth");
            c.Fixed("connect");
            c.RandomRange("islands", 0, 2);
            c.RandomRange("jets", 0, 4);
            c.RandomRange("rowboats", 0, 4);
            c.Chance("boathouse", 0.35f);
            c.Chance("promenade", 0.35f);
        }

        protected override void DefinePresets()
        {
            Preset("plain", ("islands", 0.0), ("jets", 0.0));
            Preset("islandLake", ("islands", 2.0), ("bridges", true), ("islandTrees", true));
            Preset("boatingLake", ("islands", 1.0), ("boathouse", true), ("rowboats", 4.0), ("promenade", true));
            Preset("fountainLake", ("islands", 0.0), ("jets", 5.0), ("jetLayout", "line"), ("promenade", true), ("promWidth", 9.0));
            Preset("grandLake", ("islands", 3.0), ("jets", 3.0), ("jetLayout", "cluster"), ("boathouse", true), ("rowboats", 3.0), ("promenade", true));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            LakeDressing.Plan(ctx, ctx.LayerRegion.Ring, new LakeDressing.Options
            {
                Depth = Math.Max(1f, ctx.F("depth")),
                Islands = ctx.I("islands"), Bridges = ctx.B("bridges"), IslandTrees = ctx.B("islandTrees"),
                Jets = ctx.I("jets"), JetLayout = ctx.S("jetLayout"),
                Boathouse = ctx.B("boathouse"), Rowboats = ctx.I("rowboats"),
                Promenade = ctx.B("promenade"), PromWidth = ctx.F("promWidth"), BenchSpacing = ctx.F("benchSpacing"),
                Lamps = ctx.B("lamps"), Connect = ctx.B("connect"),
            });
            ctx.Plan.Facts["pondArea"] = ctx.LayerRegion.Area;
        }
    }
}
