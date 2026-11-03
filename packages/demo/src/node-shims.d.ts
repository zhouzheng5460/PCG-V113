// 最小 Node 类型声明。
//
// 为什么不装 @types/node：这台机器当前没有 npm 网络（git clone 也失败），
// 而 run.ts 只用到 fs/path/url 的四个函数。与其为了四个函数引一整套类型，
// 不如写清楚需要哪几个——顺便让"这个包只依赖这四个 API"变成显式契约。

declare module 'node:fs' {
  export function readFileSync(path: string, encoding: 'utf8'): string;
  export function writeFileSync(path: string, data: string, encoding: 'utf8'): void;
  export function mkdirSync(path: string, opts?: { recursive?: boolean }): void;
  export function existsSync(path: string): boolean;
  export function readdirSync(path: string): string[];
}

declare module 'node:path' {
  export function dirname(p: string): string;
  export function join(...parts: string[]): string;
  export function resolve(...parts: string[]): string;
}

declare module 'node:url' {
  export function fileURLToPath(u: string): string;
}

declare module 'node:child_process' {
  export function execFileSync(
    file: string,
    args: string[],
    opts?: { encoding?: 'utf8'; stdio?: string | string[] },
  ): string;
}

declare const process: {
  argv: string[];
  exit(code?: number): never;
  stdout: { write(s: string): void };
};
declare const console: {
  log(...a: unknown[]): void;
  error(...a: unknown[]): void;
  warn(...a: unknown[]): void;
};
