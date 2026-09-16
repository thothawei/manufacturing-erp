// 展示用的假 provider：看中文問句決定呼叫哪個工具，拿到工具結果後用真實數字組一句話。
//
// 跟 fake-openai-provider.mjs 的分工：
//   fake-openai-provider.mjs  給 e2e-omniroute.sh 用，問句要明寫 @@CALL 指令 ——
//                             要的是可重複斷言，不是像人講話。
//   這一支（demo-nl-provider）給 demo-ai-screenshot.sh 用，吃的是自然語言 ——
//                             要的是展示畫面看起來就是「問一句話，它自己查完回答」。
//
// 哪些是真的、哪些是假的（截圖或展示時必須講清楚）：
//   真的：工具呼叫、資料庫查詢、回傳的每一個數字、tool-use 迴圈整條路徑。
//   假的：「挑哪個工具」是下面的關鍵字規則，「把數字寫成句子」是 compose() 的模板。
//         接真模型時這兩件事改由模型做，中間的工具與資料層一行都不用動。
//
// 用法：STUB_PORT=8000 node scripts/demo-nl-provider.mjs
//
import http from "node:http";

const PORT = Number(process.env.STUB_PORT ?? 8000);

// 關鍵字 → 要呼叫的工具（順序就是優先序）
const ROUTES = [
  { kw: ["缺料", "缺多少", "採購", "要買"], name: "run_mrp_shortage_analysis", args: {} },
  { kw: ["風險", "延遲", "會不會來不及", "趕不上"], name: "list_work_orders_at_risk", args: {} },
  { kw: ["做幾台", "能做", "可製造", "夠不夠", "生產"], name: "check_material_sufficiency_for_item", args: { item_code: "TV-100" } },
  { kw: ["庫存", "還有多少", "剩多少", "可以用"], name: "get_item_inventory_status", args: { item_code: "PANEL-01" } },
];

function textOf(content) {
  if (typeof content === "string") return content;
  if (Array.isArray(content)) return content.map((c) => c?.text ?? "").join("\n");
  return "";
}

function pickTool(question) {
  for (const r of ROUTES) if (r.kw.some((k) => question.includes(k))) return r;
  return null;
}

// 把工具回傳的真實 JSON 寫成一句中文，數字一律取自 JSON，不自己編
function compose(name, data) {
  try {
    const d = JSON.parse(data);
    if (name === "get_item_inventory_status") {
      return `${d.item_name}（${d.item_code}）目前帳上 ${d.on_hand_qty} ${d.unit}，其中 ${d.reserved_qty} ${d.unit} 已被其他工單保留，實際可動用的是 ${d.available_qty} ${d.unit}。排產請以可用量為準，用帳上量會把別張工單的料重複算進來。`;
    }
    if (name === "check_material_sufficiency_for_item") {
      const s = (d.shortage_components ?? []).length;
      return `以目前可用庫存試算，${d.item_code}（BOM ${d.bom_version}）最多可製造 ${d.max_buildable_qty} 台` +
        (s === 0 ? "，沒有缺料件。" : `，缺料件 ${s} 項：${d.shortage_components.map((c) => c.item_code).join("、")}。`);
    }
    if (name === "list_work_orders_at_risk") {
      return `目前有 ${d.length} 張工單有延遲風險：\n` +
        d.map((w) => `・${w.work_order_no}（${w.item_code}）交期 ${w.due_date}，落後 ${w.delay_days} 天 —— ${w.risk_reason}`).join("\n");
    }
    if (name === "run_mrp_shortage_analysis") {
      const items = d.shortage_items ?? [];
      if (items.length === 0) return "未來 30 天內沒有缺料。";
      return `未來 30 天有 ${items.length} 項料件會短缺：\n` +
        items.map((i) => `・${i.item_name}（${i.item_code}）毛需求 ${i.gross_requirement_qty}、可用 ${i.available_qty}，淨缺 ${i.net_shortage_qty}。` +
          `${i.needed_by_date} 前需到料，供應商 ${i.supplier_code} 交期 ${i.lead_time_days} 天，建議採購 ${i.suggested_order_qty}。`).join("\n") +
        `\n是否要我開立採購建議？（建議需人工核准才會成立採購單）`;
    }
    return `工具 ${name} 回傳：${data}`;
  } catch {
    return `工具 ${name} 回傳：${data}`;
  }
}

http.createServer((req, res) => {
  let raw = "";
  req.on("data", (c) => (raw += c));
  req.on("end", () => {
    const send = (o, code = 200) => { res.writeHead(code, { "content-type": "application/json" }); res.end(JSON.stringify(o)); };
    if (req.method === "GET" && req.url.startsWith("/v1/models")) {
      return send({ object: "list", data: [{ id: "erp-fake", object: "model", owned_by: "fake", created: 1 }] });
    }
    if (req.method !== "POST" || !req.url.startsWith("/v1/chat/completions")) return send({ error: "not found" }, 404);

    let body = {};
    try { body = JSON.parse(raw); } catch { /* 空的就走沒指令的分支 */ }
    const messages = body.messages ?? [];
    const reply = (content, extra = {}) => send({
      id: "chatcmpl-stub", object: "chat.completion", created: 1, model: body.model ?? "erp-fake",
      choices: [{ index: 0, message: { role: "assistant", content, ...extra }, finish_reason: extra.tool_calls ? "tool_calls" : "stop" }],
      usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
    });

    const toolMsgs = messages.filter((m) => m.role === "tool");
    if (toolMsgs.length > 0) {
      return reply(toolMsgs.map((m) => compose(m.name ?? "?", textOf(m.content))).join("\n\n"));
    }

    const lastUser = [...messages].reverse().find((m) => m.role === "user");
    const q = textOf(lastUser?.content);
    const route = pickTool(q);
    if (!route) return reply("這個問題我沒有對應的工具可以查，請改問庫存、可製造量、工單風險或缺料採購。");
    return reply(null, {
      tool_calls: [{ id: "call_1", type: "function", function: { name: route.name, arguments: JSON.stringify(route.args) } }],
    });
  });
}).listen(PORT, "127.0.0.1", () => console.log(`nl stub provider on http://127.0.0.1:${PORT}/v1`));
