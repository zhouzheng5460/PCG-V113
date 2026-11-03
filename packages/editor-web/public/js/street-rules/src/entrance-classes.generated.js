// 由 packages/street-rules/tools/gen-entrance-classes.mjs 自动生成，不要手改。
//
// 数据来源：placemaker-generators/1（driver 的 --generators 反射 GeneratorRegistry.All）
// 130 个生成器，6 个家族。
//
// 分布：none 97｜typed 27｜parking 1｜
//       shop 3｜market 1｜roads 1｜unknown 0
//
// 为什么要生成：手抄 130 行会漂移，而且已经漂过一次（plaza 被误归为 typed，
// 实际 PlazaLayer.CheckReady 恒 true —— docs/D 的 D-024）。归类只依赖"虚方法最近声明点
// + 两个 protected virtual 开关的实例值"，反射能全部拿到，人抄不出来。
//
// 每行的注释是那个声明的**源码位置**（file:line），用来顺藤摸瓜回读实现。
/** 生成器 id → 家族。 */
export const ENTRANCE_TABLE = {
    "zoningSuburb": 'none', // LayerBase.cs:428  group=zoning
    "zoningVillage": 'none', // LayerBase.cs:428  group=zoning
    "zoningTownhouses": 'none', // LayerBase.cs:428  group=zoning
    "zoningMainStreet": 'none', // LayerBase.cs:428  group=zoning
    "zoningTownCentre": 'none', // LayerBase.cs:428  group=zoning
    "zoningDowntown": 'none', // LayerBase.cs:428  group=zoning
    "zoningBusinessPark": 'none', // LayerBase.cs:428  group=zoning
    "zoningIndustrial": 'none', // LayerBase.cs:428  group=zoning
    "zoningSeaside": 'none', // LayerBase.cs:428  group=zoning
    "parking": 'parking', // ParkingLayer.cs:235  group=parking
    "polishGarden": 'none', // LayerBase.cs:428  group=zoning
    "polishNatural": 'none', // LayerBase.cs:428  group=zoning
    "polishTidy": 'none', // LayerBase.cs:428  group=zoning
    "polishYard": 'none', // LayerBase.cs:428  group=zoning
    "polishMeadow": 'none', // LayerBase.cs:428  group=zoning
    "polishRustic": 'none', // LayerBase.cs:428  group=zoning
    "stripMall": 'shop', // CommercialLayer.cs:39 :37  group=commercial
    "polishCoastal": 'none', // LayerBase.cs:428  group=zoning
    "shoppingPlaza": 'none', // CommercialLayer.cs:39 :37  group=commercial
    "outdoorMall": 'none', // CommercialLayer.cs:39 :37  group=commercial
    "polishManicured": 'none', // LayerBase.cs:428  group=zoning
    "shopsPark": 'none', // CommercialLayer.cs:39 :37  group=commercial
    "mallPark": 'shop', // CommercialLayer.cs:39 :37  group=commercial
    "plaza": 'none', // PlazaLayer.cs:283  group=squares
    "highStreet": 'none', // CommercialLayer.cs:39 :37  group=commercial
    "retailPark": 'none', // CommercialLayer.cs:39 :37  group=commercial
    "mallDistrict": 'shop', // CommercialLayer.cs:39 :37  group=commercial
    "park": 'typed', // ParkLayer.cs:257  group=parks
    "campground": 'typed', // CampgroundLayer.cs:88  group=nature
    "pond": 'none', // LayerBase.cs:428  hidden
    "botanicalGarden": 'typed', // LeisureLayer.cs:117  group=gardens
    "sportsPark": 'typed', // LeisureLayer.cs:117  group=sports
    "dogPark": 'typed', // LeisureLayer.cs:117  group=parks
    "picnicGrounds": 'typed', // LeisureLayer.cs:117  group=parks
    "natureReserve": 'typed', // LeisureLayer.cs:117  group=nature
    "formalGarden": 'typed', // LeisureLayer.cs:117  group=gardens
    "communityGarden": 'typed', // LeisureLayer.cs:117  group=gardens
    "marketSquare": 'market', // MarketSquareLayer.cs:32  group=squares
    "skatePark": 'typed', // LeisureLayer.cs:117  hidden, group=sports
    "cemeteryGrounds": 'typed', // LeisureLayer.cs:117  group=parks
    "greenwayPark": 'typed', // LeisureLayer.cs:117  group=nature
    "split": 'none', // LayerBase.cs:428  hidden
    "containerYard": 'none', // LeisureLayer.cs:117  group=industrial
    "carYard": 'none', // LeisureLayer.cs:117  group=industrial
    "junkYard": 'none', // LeisureLayer.cs:117  group=industrial
    "industrialWorks": 'none', // LeisureLayer.cs:117  group=industrial
    "beach": 'typed', // LeisureLayer.cs:117  group=water
    "urbanFarm": 'typed', // LeisureLayer.cs:117  group=farms
    "animalPark": 'typed', // LeisureLayer.cs:117  group=farms
    "beachBar": 'none', // LayerBase.cs:428  hidden, group=water
    "constructionSite": 'none', // LeisureLayer.cs:117  group=industrial
    "waterfront": 'typed', // LeisureLayer.cs:117  group=water
    "quarry": 'none', // LeisureLayer.cs:117  group=industrial
    "fairground": 'typed', // LeisureLayer.cs:117  group=events
    "amphitheatre": 'typed', // LeisureLayer.cs:117  group=events
    "restArea": 'none', // LeisureLayer.cs:117  group=transport
    "transitPlaza": 'none', // LeisureLayer.cs:117  group=transport
    "ai": 'none', // LayerBase.cs:428  group=ai
    "fountain": 'none', // LayerBase.cs:428
    "shore": 'none', // LayerBase.cs:428
    "playground": 'none', // LayerBase.cs:428
    "restrooms": 'none', // LayerBase.cs:428
    "kiosk": 'none', // LayerBase.cs:428
    "shops": 'none', // LayerBase.cs:428
    "picnic": 'none', // LayerBase.cs:428
    "farmstead": 'none', // LeisureLayer.cs:117  group=farms
    "honeyHives": 'none', // 别名 → apiary（LayerBase.cs:428）  hidden
    "orchardPark": 'typed', // LeisureLayer.cs:117  group=parks
    "farmStall": 'none', // 别名 → farmStand（LayerBase.cs:428）  hidden
    "bigtrees": 'none', // LayerBase.cs:428
    "terrain": 'none', // LayerBase.cs:428
    "plazatrees": 'none', // LayerBase.cs:428
    "rocks": 'none', // LayerBase.cs:428
    "ground": 'none', // LayerBase.cs:428
    "decals": 'none', // LayerBase.cs:428
    "flowerbeds": 'none', // LayerBase.cs:428
    "volleyball": 'none', // LayerBase.cs:428
    "basketball": 'none', // LayerBase.cs:428
    "spoilHeaps": 'none', // LayerBase.cs:428
    "tableTennis": 'none', // LayerBase.cs:428
    "workLights": 'none', // LayerBase.cs:428
    "chess": 'none', // LayerBase.cs:428
    "fitness": 'none', // LayerBase.cs:428
    "splashPad": 'none', // LayerBase.cs:428
    "bandstand": 'none', // LayerBase.cs:428
    "dogRun": 'none', // LayerBase.cs:428
    "noticeboard": 'none', // LayerBase.cs:428
    "apiary": 'none', // LayerBase.cs:428
    "bigStatue": 'none', // LayerBase.cs:428
    "debris": 'none', // LayerBase.cs:428
    "lightTowers": 'none', // LayerBase.cs:428
    "dirtJumps": 'none', // LayerBase.cs:428
    "farmStand": 'none', // LayerBase.cs:428
    "festivalCamping": 'none', // LayerBase.cs:428
    "obelisk": 'none', // LayerBase.cs:428
    "triumphalArch": 'none', // LayerBase.cs:428
    "sculptureWalk": 'none', // LayerBase.cs:428
    "folly": 'none', // LayerBase.cs:428
    "terraceGarden": 'none', // LayerBase.cs:428
    "pitch": 'none', // LayerBase.cs:428
    "baseballDiamond": 'none', // LayerBase.cs:428
    "runningTrack": 'none', // LayerBase.cs:428
    "tennisCourts": 'none', // LayerBase.cs:428
    "basketballCourt": 'none', // LayerBase.cs:428
    "cricketGround": 'none', // LayerBase.cs:428
    "skateBowl": 'none', // LayerBase.cs:428
    "golf": 'typed', // LeisureLayer.cs:117  group=sports
    "lookoutHill": 'typed', // LeisureLayer.cs:117  group=nature
    "adventurePlayground": 'typed', // LeisureLayer.cs:117  group=parks
    "memorialPark": 'typed', // LeisureLayer.cs:117  group=parks
    "kickabout": 'none', // LayerBase.cs:428  hidden
    "flagCircle": 'none', // LayerBase.cs:428  hidden
    "puttingGreen": 'none', // LayerBase.cs:428
    "viewpoint": 'none', // LayerBase.cs:428
    "logisticsYard": 'none', // LeisureLayer.cs:117  group=industrial
    "yardBreak": 'none', // LayerBase.cs:428
    "hikingTrail": 'typed', // LeisureLayer.cs:117  group=nature
    "trailCamp": 'none', // LayerBase.cs:428
    "abandonedLot": 'none', // LeisureLayer.cs:117  group=industrial
    "scrapyard": 'none', // LeisureLayer.cs:117  group=industrial
    "landfill": 'none', // LeisureLayer.cs:117  group=industrial
    "urbanRuins": 'typed', // LeisureLayer.cs:117  group=sports
    "roads": 'roads', // RoadsLayer.cs:115  group=roads
    "wetland": 'typed', // LeisureLayer.cs:117  group=nature
    "wildlifeHabitat": 'none', // LayerBase.cs:428
    "zenGarden": 'typed', // LeisureLayer.cs:117  group=gardens
    "teaHouse": 'none', // LayerBase.cs:428
    "edgeZoning": 'none', // EdgeZoningAddOn.cs:98
    "junkPiles": 'none', // LayerBase.cs:428  hidden
    "skipRow": 'none', // LayerBase.cs:428  hidden
};
/** 生成器 id → 建议器是否可能产出非空 marker（决定 auto 计数，不等于影响 ready）。 */
export const SUGGESTS_TABLE = {
    "parking": true,
    "plaza": true,
    "park": true,
    "campground": true,
    "botanicalGarden": true,
    "sportsPark": true,
    "dogPark": true,
    "picnicGrounds": true,
    "natureReserve": true,
    "formalGarden": true,
    "communityGarden": true,
    "skatePark": true,
    "cemeteryGrounds": true,
    "greenwayPark": true,
    "containerYard": true,
    "carYard": true,
    "junkYard": true,
    "industrialWorks": true,
    "beach": true,
    "urbanFarm": true,
    "animalPark": true,
    "constructionSite": true,
    "waterfront": true,
    "quarry": true,
    "fairground": true,
    "amphitheatre": true,
    "restArea": true,
    "transitPlaza": true,
    "farmstead": true,
    "orchardPark": true,
    "golf": true,
    "lookoutHill": true,
    "adventurePlayground": true,
    "memorialPark": true,
    "logisticsYard": true,
    "hikingTrail": true,
    "abandonedLot": true,
    "scrapyard": true,
    "landfill": true,
    "urbanRuins": true,
    "wetland": true,
    "zenGarden": true,
};
/** 表里一共有多少个生成器（用于和 driver 的 count 对账）。 */
export const GENERATOR_COUNT = 130;
