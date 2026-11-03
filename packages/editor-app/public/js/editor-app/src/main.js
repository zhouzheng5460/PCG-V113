// 入口。只做"挂载"，任何逻辑都不放在这里 —— 保证 build 出来的包只有一个副作用点。
import { mount } from "./app.js";
const host = document.getElementById('root');
if (!host)
    throw new Error('#root 不存在');
mount(host);
