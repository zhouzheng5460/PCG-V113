import { Id, LayerKey } from '../core/model';

/** 语义工具 + 几何工具（PRD §11） */
export type ToolId =
  | 'select'
  | 'rect'
  | 'polygon'
  | 'polyline'
  | 'circle'
  | 'measure'
  | 'pan';

export type MeasureMode = 'distance' | 'area';

/** 几何工具创建的语义类型 */
export type DrawKind = 'site' | 'parcel' | 'building' | 'road' | 'access' | 'nobuild' | 'ground';

export type SelectionFilter = 'all' | 'parcels' | 'buildings' | 'roads' | 'ground' | 'generated';

export interface EditorViewState {
  activeView: '2d' | '3d';
  selectionIds: Id[];
  hoverId?: Id;

  activeTool: ToolId;
  drawKind: DrawKind;
  measureMode: MeasureMode;

  snapEnabled: boolean;
  gridVisible: boolean;
  gridSnap: boolean;
  gridSize: number;

  focusObjectId?: Id;

  layerVisibility: Record<LayerKey, boolean>;
  layerLocked: Record<LayerKey, boolean>;

  selectionFilter: SelectionFilter;
  zoom: number;
  /** 背景图编辑模式：拖动移动 / Alt 旋转 / Shift 缩放 */
  bgEditing: boolean;
}

export const TOOL_LABEL: Record<ToolId, string> = {
  select: '选择',
  rect: '矩形',
  polygon: '多边形',
  polyline: '折线',
  circle: '圆',
  measure: '测量',
  pan: '平移',
};

export const TOOL_SHORTCUT: Record<string, ToolId> = {
  v: 'select',
  r: 'rect',
  p: 'polygon',
  l: 'polyline',
  c: 'circle',
  m: 'measure',
  h: 'pan',
};

export const DRAW_KIND_LABEL: Record<DrawKind, string> = {
  site: '园区范围',
  parcel: '地块',
  building: '建筑',
  road: '道路',
  access: '出入口',
  nobuild: '禁建区',
  ground: '铺装',
};

export const defaultViewState = (): EditorViewState => ({
  activeView: '2d',
  selectionIds: [],
  activeTool: 'select',
  drawKind: 'parcel',
  measureMode: 'distance',
  snapEnabled: true,
  gridVisible: true,
  gridSnap: true,
  gridSize: 1,
  layerVisibility: {
    site: true,
    parcels: true,
    roads: true,
    buildings: true,
    access: true,
    ground: true,
    landscape: true,
  },
  layerLocked: {
    site: false,
    parcels: false,
    roads: false,
    buildings: false,
    access: false,
    ground: false,
    landscape: false,
  },
  selectionFilter: 'all',
  zoom: 5,
  bgEditing: false,
});
