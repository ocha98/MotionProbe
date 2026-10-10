// Builds a VPM (VRChat Creator Companion) listing from this repository's GitHub releases.
// Every release that has a package zip and package.json attached becomes one package version.
// usage: node build-listing.mjs <outDir>   (env: GITHUB_REPOSITORY, GITHUB_TOKEN)
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const outDir = process.argv[2] ?? "site";
const repo = process.env.GITHUB_REPOSITORY;
const [owner, name] = repo.split("/");
const headers = { Authorization: `Bearer ${process.env.GITHUB_TOKEN}`, "User-Agent": "vpm-listing" };
const listingUrl = `https://${owner.toLowerCase()}.github.io/${name}/index.json`;
const local = JSON.parse(readFileSync("Packages/com.ocha98.motionprobe/package.json", "utf8"));

async function get(url, accept) {
  const res = await fetch(url, { headers: { ...headers, Accept: accept } });
  if (!res.ok) throw new Error(`${res.status} ${url}`);
  return res;
}

const releases = [];
for (let page = 1; ; page++) {
  const batch = await (await get(`https://api.github.com/repos/${repo}/releases?per_page=100&page=${page}`, "application/vnd.github+json")).json();
  releases.push(...batch);
  if (batch.length < 100) break;
}

const packages = {};
for (const rel of releases) {
  if (rel.draft || rel.prerelease) continue;
  const zip = rel.assets.find(a => a.name.endsWith(".zip"));
  const manifest = rel.assets.find(a => a.name === "package.json");
  if (!zip || !manifest) continue;
  // Download through the API so it works right after the release is created.
  const pkg = await (await get(manifest.url, "application/octet-stream")).json();
  const bytes = Buffer.from(await (await get(zip.url, "application/octet-stream")).arrayBuffer());
  pkg.url = zip.browser_download_url;
  pkg.zipSHA256 = createHash("sha256").update(bytes).digest("hex");
  (packages[pkg.name] ??= { versions: {} }).versions[pkg.version] = pkg;
  console.log(`added ${pkg.name} ${pkg.version}`);
}

const listing = {
  name: `${local.displayName} (${owner})`,
  id: `com.${owner.toLowerCase()}.${name.toLowerCase()}.listing`,
  url: listingUrl,
  author: owner,
  packages,
};

mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, "index.json"), JSON.stringify(listing, null, 2));
writeFileSync(join(outDir, "index.html"), `<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${local.displayName} VPM</title>
<style>body{font-family:system-ui,sans-serif;max-width:640px;margin:40px auto;padding:0 16px;line-height:1.7}
a.btn{display:inline-block;padding:10px 18px;background:#2563eb;color:#fff;border-radius:6px;text-decoration:none}
code{background:#eee;padding:2px 6px;border-radius:4px;word-break:break-all}</style></head>
<body><h1>${local.displayName}</h1><p>${local.description}</p>
<p><a class="btn" href="vcc://vpm/addRepo?url=${encodeURIComponent(listingUrl)}">VCCに追加</a></p>
<p>ボタンが動かないときは、VCCの Settings → Packages → Add Repository に次のURLを貼り付けてください。<br><code>${listingUrl}</code></p>
<p><a href="https://github.com/${repo}">GitHub</a></p></body></html>
`);
console.log(`listing: ${Object.values(packages).reduce((n, p) => n + Object.keys(p.versions).length, 0)} version(s)`);
