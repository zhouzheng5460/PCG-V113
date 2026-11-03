// 冒烟测试：内联的 three 能不能在 file:// 下真的跑起来。
//
// 为什么单独测：three 的 ESM → 经典脚本是"删 import/export + 包 IIFE"的机械操作，
// 最容易出的错是**重复声明**（同作用域里两个同名 class/const）和**全局名冲突**。
// 这两种错都只在解析/执行那一刻炸，纯读代码看不出来。

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const puppeteer = require('C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/puppeteer-core');
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

const three = readFileSync('C:/Users/Administrator/WorkBuddy/城市工具最终版本/packages/view3d/public/js/vendor/three.inline.js', 'utf8');

const dir = join(tmpdir(), 'three-smoke');
mkdirSync(dir, { recursive: true });
const file = join(dir, 'smoke.html');

writeFileSync(
  file,
  `<!doctype html><meta charset="utf-8"><body style="margin:0">
<div id="c" style="width:600px;height:400px"></div>
<script>${three}</script>
<script>
window.__result = (function () {
  try {
    var T = window.THREE;
    var keys = Object.keys(T).length;
    var need = ['Scene','PerspectiveCamera','WebGLRenderer','Mesh','BoxGeometry','MeshStandardMaterial','DirectionalLight','Vector3','Color','Group','BufferGeometry','BufferAttribute','InstancedMesh','Shape','ExtrudeGeometry'];
    var missing = need.filter(function (k) { return !T[k]; });

    var r = new T.WebGLRenderer({ antialias: true });
    r.setSize(600, 400);
    document.getElementById('c').appendChild(r.domElement);

    var sc = new T.Scene();
    sc.background = new T.Color(0x101418);
    var cam = new T.PerspectiveCamera(50, 1.5, 0.1, 1000);
    cam.position.set(6, 6, 6);
    cam.lookAt(0, 0, 0);

    var m = new T.Mesh(new T.BoxGeometry(2, 2, 2), new T.MeshStandardMaterial({ color: 0x77ccff }));
    sc.add(m);
    sc.add(new T.DirectionalLight(0xffffff, 2));
    sc.add(new T.AmbientLight(0xffffff, 0.6));
    r.render(sc, cam);

    return { ok: missing.length === 0 && keys > 300, keys: keys, missing: missing, gl: r.getContext().getParameter(r.getContext().VERSION) };
  } catch (e) {
    return { ok: false, error: String(e && e.message || e), stack: String(e && e.stack || '').slice(0, 400) };
  }
})();
</script></body>`,
);

const errs = [];
const browser = await puppeteer.launch({ executablePath: EDGE, headless: 'new', args: ['--no-sandbox', '--use-gl=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage();
page.on('pageerror', (e) => errs.push(String(e).slice(0, 300)));
await page.goto('file:///' + file.replace(/\\/g, '/'), { waitUntil: 'load' });
await new Promise((r) => setTimeout(r, 600));
const res = await page.evaluate(() => window.__result);
await browser.close();

console.log(JSON.stringify({ ...res, pageErrors: errs }, null, 2));
process.exitCode = res.ok && errs.length === 0 ? 0 : 1;
