// PlacemakerCore 常驻服务（--serve）。
//
// 为什么加它：编辑器里「楼的来源 = Placemaker」原本是 TS 复刻（vendor/townforge/src/bridge/）。
// 复刻永远是近似的；要用**真 C# 算法**，就得有一个能被前端调用的进程。
//
// 设计上刻意保持最小：
//   · 只两个端点：GET /health · POST /plan（body = case JSON，返回 plan JSON）
//   · **完全复用 `Emit.Run`** —— 请求体写到临时文件，跑同一条已验证的管线，读完删掉。
//     不重写一遍核心调用：那样迟早和 CLI 的口径分叉（而这套项目已经因为"两份实现"栽过）。
//   · 默认只监听 127.0.0.1：本机工具，不对外暴露。
//   · 单线程顺序处理即可：一份 24 ms，编辑器一次最多几十份。

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Placemaker.Driver
{
    internal static class Serve
    {
        public static int Run(int port)
        {
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[serve] 端口 {port} 起不来: {e.Message}");
                Console.Error.WriteLine($"[serve] 换个端口：--serve 8788");
                return 4;
            }

            Console.WriteLine($"[serve] Placemaker 服务已启动  {prefix}");
            Console.WriteLine("[serve] GET  /health   探活");
            Console.WriteLine("[serve] POST /plan     body = case JSON，返回 plan JSON");
            Console.Out.Flush();

            var tmpDir = Path.Combine(Path.GetTempPath(), "placemaker-serve");
            Directory.CreateDirectory(tmpDir);
            int seq = 0;

            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    break; // 监听器被关掉（Ctrl+C / 进程退出）
                }

                int id = Interlocked.Increment(ref seq);
                Handle(ctx, tmpDir, id);
            }
            return 0;
        }

        private static void Handle(HttpListenerContext ctx, string tmpDir, int id)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // 前端跑在 vite dev（另一个端口），必须放行跨源
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            res.Headers["Access-Control-Max-Age"] = "600";

            try
            {
                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
                if (path.Length == 0) path = "/";

                if (path == "/health" && req.HttpMethod == "GET")
                {
                    var body = "{\"ok\":true,\"service\":\"placemaker\",\"protocol\":\"placemaker-service/1\","
                             + "\"planProtocol\":\"placemaker-plan/1\",\"generators\":[\"parking\",\"plaza\",\"formalGarden\"]}";
                    Write(res, 200, "application/json; charset=utf-8", body);
                    Console.WriteLine($"[serve] #{id} GET /health 200");
                    return;
                }

                if (path == "/plan" && req.HttpMethod == "POST")
                {
                    string caseJson;
                    using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                        caseJson = reader.ReadToEnd();

                    if (string.IsNullOrWhiteSpace(caseJson))
                    {
                        Write(res, 400, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"empty body\"}");
                        return;
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var inPath = Path.Combine(tmpDir, $"case-{id}.json");
                    var outPath = Path.Combine(tmpDir, $"plan-{id}.json");
                    File.WriteAllText(inPath, caseJson, new UTF8Encoding(false));

                    // 复用 CLI 那条管线：同一份代码、同一套口径
                    int code;
                    var errText = new StringWriter();
                    var oldErr = Console.Error;
                    try
                    {
                        Console.SetError(errText);
                        code = Emit.Run(inPath, outPath);
                    }
                    finally
                    {
                        Console.SetError(oldErr);
                    }

                    if (code != 0 || !File.Exists(outPath))
                    {
                        var msg = errText.ToString().Trim().Replace("\"", "'");
                        Console.Error.WriteLine($"[serve] #{id} POST /plan 失败 code={code} {msg}");
                        Write(res, 422, "application/json; charset=utf-8",
                              "{\"ok\":false,\"code\":" + code + ",\"error\":\"" + msg + "\"}");
                    }
                    else
                    {
                        var planJson = File.ReadAllText(outPath, Encoding.UTF8);
                        Write(res, 200, "application/json; charset=utf-8", planJson);
                        Console.WriteLine($"[serve] #{id} POST /plan 200  {sw.ElapsedMilliseconds} ms  {planJson.Length} B");
                    }

                    try
                    {
                        File.Delete(inPath);
                        File.Delete(outPath);
                    }
                    catch { /* 临时文件删不掉不影响结果 */ }
                    return;
                }

                Write(res, 404, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"not found\"}");
            }
            catch (Exception e)
            {
                try
                {
                    Write(res, 500, "application/json; charset=utf-8",
                          "{\"ok\":false,\"error\":\"" + e.Message.Replace("\"", "'") + "\"}");
                }
                catch { /* 连接已断 */ }
                Console.Error.WriteLine($"[serve] #{id} 异常: {e.Message}");
            }
        }

        private static void Write(HttpListenerResponse res, int code, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            res.StatusCode = code;
            res.ContentType = type;
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }
    }
}
