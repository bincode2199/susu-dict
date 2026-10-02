// Generates the redistribution inventory from the locked dependency graph (F00.1).
// Usage: node tools/license-inventory.mjs <output.json> <project packages.lock.json>...
// Reads NuGet nuspec metadata from the repository-local package folder, the pinned native
// sources, and the UI's production npm dependencies. It reports; it does not decide licensing.
import { readFileSync, existsSync, readdirSync, writeFileSync, realpathSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const [output, ...lockfiles] = process.argv.slice(2);
if (!output || lockfiles.length === 0) throw new Error('Usage: node tools/license-inventory.mjs <output.json> <packages.lock.json>...');

// Build-time only: compilers and trimming tools, not redistributed as separate files.
const buildOnly = new Set(['microsoft.dotnet.ilcompiler', 'microsoft.net.illink.tasks']);
// Supplies the NativeAOT runtime compiled into the executable (not a separate file).
const linkedRuntime = new Set(['runtime.win-x64.microsoft.dotnet.ilcompiler']);
const packages = new Map();
for (const lockfile of lockfiles) {
  const lock = JSON.parse(readFileSync(join(root, lockfile), 'utf8'));
  for (const [framework, deps] of Object.entries(lock.dependencies)) {
    for (const [id, info] of Object.entries(deps)) {
      if (info.type === 'Project' || !info.resolved) continue;
      const key = `${id.toLowerCase()}@${info.resolved}`;
      const entry = packages.get(key) ?? { id, version: info.resolved, usedBy: new Set(), framework };
      entry.usedBy.add(lockfile.split('/').slice(-2, -1)[0]);
      packages.set(key, entry);
    }
  }
}

function tag(xml, name) {
  const match = xml.match(new RegExp(`<${name}[^>]*>([\\s\\S]*?)</${name}>`));
  return match ? match[1].trim() : null;
}

const nuget = [];
for (const entry of packages.values()) {
  const folder = join(root, '.tools', 'nuget', entry.id.toLowerCase(), entry.version);
  const nuspec = existsSync(folder) ? readdirSync(folder).find((f) => f.endsWith('.nuspec')) : null;
  const xml = nuspec ? readFileSync(join(folder, nuspec), 'utf8') : '';
  const licenseTag = xml.match(/<license type="(\w+)"[^>]*>([^<]+)<\/license>/);
  const licenseFiles = existsSync(folder) ? readdirSync(folder).filter((f) => /^(license|licence|thirdpartynotices|notice)/i.test(f)) : [];
  nuget.push({
    id: entry.id,
    version: entry.version,
    usedBy: [...entry.usedBy].sort(),
    redistributed: !buildOnly.has(entry.id.toLowerCase()),
    license: licenseTag ? `${licenseTag[1]}:${licenseTag[2]}` : tag(xml, 'licenseUrl') ?? 'UNKNOWN',
    copyright: tag(xml, 'copyright'),
    projectUrl: tag(xml, 'projectUrl'),
    licenseFiles,
    note: linkedRuntime.has(entry.id.toLowerCase()) ? 'NativeAOT runtime linked into the executable (.NET, MIT; see .NET ThirdPartyNotices)' : buildOnly.has(entry.id.toLowerCase())
      ? 'build tool; the NativeAOT runtime it links into the executable is .NET (MIT, see .NET ThirdPartyNotices)'
      : null,
  });
}
nuget.sort((a, b) => a.id.localeCompare(b.id));

const deps = JSON.parse(readFileSync(join(root, 'native', 'dependencies.json'), 'utf8'));
const quickjsFolder = join(root, '.tools', 'quickjs-source', `quickjs-${deps.quickjs.commit}`);
const native = [
  { name: 'QuickJS-NG', version: `${deps.quickjs.tag} (${deps.quickjs.commit})`, license: deps.quickjs.license,
    licenseFile: existsSync(join(quickjsFolder, 'LICENSE')) ? 'LICENSE in pinned source' : 'missing', linkedInto: 'susu_quickjs.dll (static)' },
  { name: 'Microsoft WebView2 loader (static library)', version: '1.0.4191.47', license: 'Microsoft WebView2 SDK license (LICENSE.txt in package)',
    licenseFile: 'microsoft.web.webview2/1.0.4191.47/LICENSE.txt', linkedInto: 'susu_native.dll, susu_windows_probe.dll (static)' },
  { name: 'MSVC static runtime (CRT/UCRT)', version: deps.msvc, license: 'Visual Studio license terms (redistributable code, statically linked)',
    licenseFile: 'Visual Studio Build Tools license', linkedInto: 'susu_quickjs.dll, susu_native.dll, susu_plugin_sandbox.dll, susu_selection.dll, susu_windows_probe.dll (/MT since F00)' },
];

// UI production closure: walk package.json dependencies from ui/package.json (installed tree).
const npm = [];
const seen = new Set();
function walk(name, fromDir) {
  let dir = fromDir, manifest = null;
  while (dir && !manifest) {
    const candidate = join(dir, 'node_modules', name, 'package.json');
    if (existsSync(candidate)) manifest = candidate; else { const up = dirname(dir); dir = up === dir ? null : up; }
  }
  if (!manifest) { npm.push({ name, error: 'not installed' }); return; }
  const real = realpathSync(manifest);
  const pkg = JSON.parse(readFileSync(real, 'utf8'));
  const key = `${pkg.name}@${pkg.version}`;
  if (seen.has(key)) return;
  seen.add(key);
  const license = typeof pkg.license === 'string' ? pkg.license : pkg.license?.type ?? 'UNKNOWN';
  const files = readdirSync(dirname(real)).filter((f) => /^(license|licence|notice)/i.test(f));
  npm.push({ name: pkg.name, version: pkg.version, license, licenseFiles: files });
  for (const dep of Object.keys(pkg.dependencies ?? {})) walk(dep, dirname(real));
}
const ui = JSON.parse(readFileSync(join(root, 'ui', 'package.json'), 'utf8'));
for (const dep of Object.keys(ui.dependencies ?? {})) walk(dep, join(root, 'ui'));
npm.sort((a, b) => a.name.localeCompare(b.name));

const unknown = nuget.filter((p) => p.license === 'UNKNOWN').map((p) => p.id);
const report = { generated: new Date().toISOString(), lockfiles, nuget, native, npm, unknownLicenses: unknown,
  notes: [
    'libsodium.dll (via NSec) imports VCRUNTIME140.dll; a clean Windows 11 system does not guarantee the VC++ runtime. Decide app-local VCRUNTIME redistribution or a static Ed25519 verifier before F18.',
    'Development-only tools (Node, pnpm, Vite, TypeScript, vue-tsc, CMake, MSVC toolchain, Firefox/Electron test targets) are not shipped.',
  ] };
writeFileSync(join(root, output), JSON.stringify(report, null, 2) + '\n');
console.log(`${nuget.length} NuGet packages, ${native.length} native components, ${npm.length} npm production packages; unknown licenses: ${unknown.length}`);
