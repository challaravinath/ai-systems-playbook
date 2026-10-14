// Render a README (Markdown + Mermaid + local SVGs) to a PDF next to it, using headless Chrome.
//
//   node tools/build-pdf.mjs <path/to/README.md> [output.pdf] [title]
//
// Needs Node 18+ and Chrome or Chromium. Markdown and Mermaid are loaded from jsDelivr at render
// time, so the machine needs internet access. Collapsed <details> hints are printed open.
import { existsSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { basename, dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [src, outArg, titleArg] = process.argv.slice(2);
if (!src) {
  console.error("usage: node tools/build-pdf.mjs <README.md> [output.pdf] [title]");
  process.exit(2);
}
const mdPath = resolve(src);
const md = readFileSync(mdPath, "utf8").replaceAll("<details>", "<details open>");
const title = titleArg ?? (md.match(/^#\s+(.+)$/m)?.[1] ?? basename(mdPath)).replace(/[<>&"]/g, "");
const out = resolve(outArg ?? join(dirname(mdPath), `${basename(dirname(mdPath))}.pdf`));

const chrome = [
  process.env.CHROME,
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  "/usr/bin/google-chrome",
  "/usr/bin/chromium",
  "/usr/bin/chromium-browser",
].find((p) => p && existsSync(p));
if (!chrome) {
  console.error("Chrome not found. Set CHROME=/path/to/chrome.");
  process.exit(1);
}

const html = `<!doctype html><html><head><meta charset="utf-8"><title>${title}</title>
<style>
@page{size:A4;margin:16mm 14mm}
body{font:10.5pt/1.5 "Segoe UI",Roboto,Arial,sans-serif;color:#1d232b;margin:0}
h1{font-size:21pt;line-height:1.2;color:#0b3d91;margin:0 0 6px}
h2{font-size:14.5pt;color:#0b3d91;border-bottom:2px solid #d6e2f5;padding-bottom:3px;margin:22px 0 8px;break-after:avoid}
h3{font-size:11.5pt;color:#16325c;margin:14px 0 6px;break-after:avoid}
table{border-collapse:collapse;width:100%;margin:8px 0;font-size:9pt}
tr{break-inside:avoid}
th,td{border:1px solid #cfd8e3;padding:4px 6px;vertical-align:top;text-align:left}
th{background:#eef3fa}
code{font:8.6pt Consolas,"DejaVu Sans Mono",monospace;background:#f2f4f7;padding:0 3px;border-radius:3px}
pre{background:#f6f8fa;border:1px solid #e1e6ec;padding:8px 10px;border-radius:6px;font-size:8.4pt;white-space:pre-wrap;break-inside:avoid}
pre code{background:none;padding:0}
blockquote{margin:8px 0;padding:6px 12px;border-left:4px solid #1a73e8;background:#f1f6fe}
details{margin:4px 0;padding:4px 10px;border:1px dashed #cfd8e3;border-radius:6px}
img{max-width:100%;display:block;margin:10px auto;break-inside:avoid}
.mermaid{text-align:center;break-inside:avoid;margin:10px 0;background:none;border:none}
.mermaid svg{max-width:100%;height:auto;max-height:150mm}
hr{border:none;border-top:1px solid #dde3ea;margin:14px 0}
ul,ol{padding-left:20px}
li{margin:2px 0}
</style>
<script src="https://cdn.jsdelivr.net/npm/marked@12.0.2/marked.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js"></script>
</head><body><div id="doc"></div>
<script type="text/markdown" id="md">${md.replace(/<\/script/gi, "<\\/script")}</script>
<script>
(async () => {
  const r = new marked.Renderer();
  const base = r.code.bind(r);
  r.code = (t, lang, esc) => lang === 'mermaid' ? '<pre class="mermaid">' + t.replace(/</g, '&lt;') + '</pre>' : base(t, lang, esc);
  document.getElementById("doc").innerHTML = marked.parse(document.getElementById("md").textContent, { renderer: r });
  mermaid.initialize({ startOnLoad: false, theme: "neutral", securityLevel: "loose" });
  await mermaid.run({ querySelector: ".mermaid" });
})();
</script></body></html>`;

// Write the page beside the README so relative image paths (architecture.svg) resolve.
const tmp = join(dirname(mdPath), `.build-pdf-${process.pid}.html`);
writeFileSync(tmp, html);
try {
  execFileSync(chrome, ["--headless=new", "--disable-gpu", "--no-pdf-header-footer", "--virtual-time-budget=25000",
    "--run-all-compositor-stages-before-draw", `--print-to-pdf=${out}`, pathToFileURL(tmp).href], { stdio: "ignore" });
} finally {
  rmSync(tmp, { force: true });
}
console.log(`wrote ${out}`);
