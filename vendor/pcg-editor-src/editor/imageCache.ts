/**
 * 背景图缓存。渲染循环每帧向这里取图，图片加载完成后下一帧自动出现，
 * 不需要额外的订阅机制。
 */
const cache = new Map<string, HTMLImageElement>();
const failed = new Set<string>();

export function getImage(src: string): HTMLImageElement | null {
  if (!src || failed.has(src)) return null;
  const hit = cache.get(src);
  if (hit) return hit.complete && hit.naturalWidth > 0 ? hit : null;

  const img = new Image();
  img.decoding = 'async';
  img.onerror = () => failed.add(src);
  img.src = src;
  cache.set(src, img);
  return null;
}

export function loadImage(src: string): Promise<HTMLImageElement> {
  const hit = cache.get(src);
  if (hit && hit.complete && hit.naturalWidth > 0) return Promise.resolve(hit);
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => {
      cache.set(src, img);
      failed.delete(src);
      resolve(img);
    };
    img.onerror = () => {
      failed.add(src);
      reject(new Error('背景图加载失败'));
    };
    img.src = src;
  });
}

export function forgetImage(src: string) {
  cache.delete(src);
  failed.delete(src);
}
