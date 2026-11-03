// Art pass (a1): explicit adventure playground defaults. The automatic pools put an agricultural sprinkler
// boom on the splash pad ("water.jets") and a chainsaw logger among the play logs ("play.nature"); these
// are the vanilla pieces the game's own playgrounds and campfire sites use.

using Placemaker.Core.Layers.Park;

namespace Placemaker.Core.Layers.Types.AdventurePlayground
{
    public static class PlayArt
    {
        public static readonly string[] Jets = { "SmallFountain02", "SmallFountain01", "SmallFountain03" };
        /// <summary>Whole tree trunks (~10 m): thin and medium ones for balancing.</summary>
        public static readonly string[] Logs = { "Log03", "Log01", "Log06", "Log04", "Log09", "Log07" };
        public static readonly string[] Boulders = { "Boulder01", "Boulder03", "Boulder05", "Boulder02", "Boulder06", "Boulder04" };
        public static readonly string[] TunnelShrubs = { "GreenBushWild02", "GreenBushWildAdult01", "GreenBushWildElderly01" };
        /// <summary>The jungle gym's centre tower.</summary>
        public static readonly string[] Tower = { "ClimbingFrame04", "PlaygroundPlayset02" };
        public static readonly string[] ShadeTrees = { "OakTree01", "NA_LindenTree01", "EU_ChestnutTree01", "NA_LondonPlaneTree01" };
        /// <summary>A low residential fence round the toddler pen and the site (no mesh: this is a playground, not a run).</summary>
        public static readonly string[] Fence = { "FenceResidentialLow02", "FenceResidentialLow01" };

        /// <summary>Play surface ids per ground choice: vanilla sand, bark mulch (modded) or the forest floor, tiles for "rubber".</summary>
        public static string[] Ground(string ground, bool toddler)
        {
            if (toddler && ground == "grass") ground = "rubber";
            switch (ground)
            {
                case "sand": return new[] { ParkArt.Sand };
                case "bark": return ParkArt.Mulch;
                case "rubber": return new[] { "Tiles Surface 02", ParkArt.Tiles };
                default: return new string[0];
            }
        }
    }
}
