// 假的 OpenAI 相容 provider，給 scripts/e2e-omniroute.sh 用。
//
// 為什麼需要它：要驗證「ERP → OmniRoute → provider → 工具 → 答案」這整條路徑，
// 但真的模型每次回什麼不保證，而且要花錢。這支只做一件事 ——
// 照問句裡的指令吐出指定的 tool_call，讓工具與轉譯那一段變成可重複驗證的。
//
// 協定：使用者問句裡放 @@CALL <tool_name> <json 參數>@@ 就會叫出那個工具，
// 放多個就一次回多個（測平行工具呼叫）。收到 tool 訊息後把內容原樣吐回來，
// 這樣測試腳本可以直接斷言工具查到的真實資料有沒有回到最終答案裡。
import http from "node:http";
import fs from "node:fs";

const PORT = Number(process.env.STUB_PORT ?? 8000);
const LOG = process.env.STUB_LOG ?? "";

function parseDirectives(text) {
  const out = [];
  const re = /@@CALL\s+(\S+)\s+(\{.*?\})@@/gs;
  let m;
  while ((m = re.exec(text)) !== null) out.push({ name: m[1], args: m[2] });
  return out;
}

function textOf(content) {
  if (typeof content === "string") return content;
  if (Array.isArray(content)) return content.map((c) => c?.text ?? "").join("\n");
  return "";
}

const server = http.createServer((req, res) => {
  let raw = "";
  req.on("data", (c) => (raw += c));
  req.on("end", () => {
    const send = (obj, code = 200) => {
      res.writeHead(code, { "content-type": "application/json" });
      res.end(JSON.stringify(obj));
    };

    if (req.method === "GET" && req.url.startsWith("/v1/models")) {
      return send({ object: "list", data: [{ id: "erp-fake", object: "model", owned_by: "fake", created: 1 }] });
    }

    if (req.method !== "POST" || !req.url.startsWith("/v1/chat/completions")) {
      return send({ error: "not found" }, 404);
    }

    let body = {};
    try { body = JSON.parse(raw); } catch { /* 解析不了就當空的，原文寫進 log */ }
    if (LOG) fs.appendFileSync(LOG, JSON.stringify({ at: new Date().toISOString(), body }) + "\n");

    const messages = body.messages ?? [];
    const reply = (content, extra = {}) => send({
      id: "chatcmpl-stub", object: "chat.completion", created: 1, model: body.model ?? "erp-fake",
      choices: [{ index: 0, message: { role: "assistant", content, ...extra }, finish_reason: extra.tool_calls ? "tool_calls" : "stop" }],
      usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 }
    });

    // 第二輪：工具結果回來了，原樣吐回去讓測試斷言
    const toolMsgs = messages.filter((m) => m.role === "tool");
    if (toolMsgs.length > 0) {
      return reply(`工具回傳：${toolMsgs.map((m) => `${m.name ?? "?"} => ${textOf(m.content)}`).join(" ||| ")}`);
    }

    // 第一輪：照問句裡的指令決定要叫哪些工具
    const lastUser = [...messages].reverse().find((m) => m.role === "user");
    const directives = parseDirectives(textOf(lastUser?.content));
    if (directives.length === 0) {
      return reply("問句裡沒有 @@CALL 指令");
    }

    return reply(null, {
      tool_calls: directives.map((d, i) => ({
        id: `call_${i + 1}`, type: "function", function: { name: d.name, arguments: d.args }
      }))
    });
  });
});

server.listen(PORT, "127.0.0.1", () => console.log(`fake OpenAI provider on http://127.0.0.1:${PORT}/v1`));
