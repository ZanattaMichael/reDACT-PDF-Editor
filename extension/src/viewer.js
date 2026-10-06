// PDF Editor viewer page. Talks straight to the native host; the working
// document lives here as bytes and every edit round-trips through the host.

import { HostClient, probeHost, bytesToBase64, base64ToBytes } from './host-client.js';
import { runFormScript } from './formScript.js';
import { ActivityLog, formatTime } from './activity-log.js';
import { displayToPage, pageToDisplay } from './geometry.js';
import { formatBuildInfo, loadBuildMeta } from './build-info.js';
import { hostDiagnosticsLines } from './host-diagnostics.js';
import {
  HOST_STATE, detectPlatform, hostInstallGuide, hostInstallGuideLines,
} from './host-install.js';
import { hostVersionGuide, hostVersionGuideLines } from './host-version.js';

const host = new HostClient();

// Everything the viewer does gets recorded here and shown by Help ▸ Activity console. The point
// (#72) is that a failure the UI recovers from — a page that renders blank, a forms panel that
// says "no fields" because listing them threw — still leaves a trace someone can read.
const activity = new ActivityLog();

// Every host round-trip is timed and logged: the action name and its duration, which is the one
// number worth having when the editor feels slow, plus the error when it fails. Wrapping `call`
// once here means no call site can forget, and no failure can be swallowed before it is recorded.
const rawHostCall = host.call.bind(host);
host.call = async (action, payload = {}) => {
  const started = performance.now();
  const elapsed = () => `${Math.round(performance.now() - started)} ms`;
  try {
    const result = await rawHostCall(action, payload);
    // With diagnostics on (#97), the per-action line also carries the payload/result sizes — the
    // other half of "why is this slow" beyond the duration — and is tagged as diagnostic.
    const detail = diagnosticsOn
      ? `${elapsed()} · sent ${approxSize(payload)} · recv ${approxSize(result)}`
      : elapsed();
    activity.add('info', action, detail, { diagnostic: diagnosticsOn });
    return result;
  } catch (e) {
    activity.add('error', `${action} failed`, `${e?.message ?? e} (after ${elapsed()})`,
      { diagnostic: diagnosticsOn });
    throw e;
  }
};

/** Rough transfer size of a host payload/result: the base64 doc if present, else the JSON length. */
function approxSize(obj) {
  const b64 = typeof obj?.pdf === 'string' ? obj.pdf.length : 0;
  let bytes = b64 ? Math.round(b64 * 0.75) : 0; // base64 is 4 chars per 3 bytes
  if (!bytes && obj) { try { bytes = JSON.stringify(obj).length; } catch { bytes = 0; } }
  return bytes >= 1024 ? `${Math.round(bytes / 1024)} KB` : `${bytes} B`;
}

// URL/link handling: a document with links shows a warning badge, links are disabled (stripped on
// save) by default, and the 🔗 Links panel lists every URL (the "source") so the user can review
// them and opt in to keeping them — optionally rated by the offline classifier / Cloudflare scanner.
const URL_SCANNING_ENABLED = true;

const state = {
  pdf: null,            // Uint8Array — current working document
  pdfB64: null,         // base64 of pdf, recomputed once per version (not per request)
  version: 0,           // bumped whenever pdf changes; keys the render cache
  password: null,       // password for the working document, if encrypted
  info: null,           // { pageCount, encrypted, pages: [{number,x,y,width,height}] }
  page: 1,
  zoom: 1,
  dpi: 144,
  fileName: 'document.pdf',
  history: [],          // undo stack of previous byte states (cap 10)
  future: [],           // redo stack of states undone (cleared on any new edit)
  sidebarOpen: false,   // page-thumbnail sidebar visibility
  tool: 'select',
  regions: [],          // pending redaction regions {page,x,y,width,height} (PDF space)
  pendingEditRegion: null,
  pendingSignRegion: null,
  pendingField: null,   // { fieldType, name } while placing a new form field
  textMode: 'edit',     // 'edit' (replace existing) or 'add' (stamp new) for the text panel
  signatures: [],
  drawColor: '#e53935',
  drawWidth: 2.5,
  highlightColor: '#ffeb3b',
  // 'sweep' marks the characters swept over (#23); 'box' marks the rectangle dragged. Sweep is the
  // default because it is what a highlighter does to text, but a box is the only thing that works
  // on a page with nothing selectable — a scan — and is sometimes just what is wanted over a table
  // or a figure, so it stays available rather than being inferred.
  highlightMode: 'sweep',
  safety: null,         // { hasActiveContent, javaScriptCount, urlCount, samples }
  safetyScan: null,     // the pending (or last) refreshSafety() — Save waits on it
  compareOther: null,   // { b64, name } — the last document compared against, kept for visual diff
  keepActiveContent: false, // false = strip JavaScript on save until the user opts in
  keepLinks: false,     // false = strip link URLs on save until the user enables them
  links: [],            // extracted { page, url } — the side panel's list
  linkHotspots: [],     // [{ page, url, kind, x, y, width, height }] — the on-page overlay (all link types)
  formFields: [],       // [{ name, type, value, readOnly, page, x, y, width, height }] once listed
  urlVerdicts: [],      // [{ page, url, level, category, source, detail }] once scanned
  scripts: [],          // document-level JavaScript { name, script } once listed
  hidden: null,         // hidden-data report { metadataFields, attachments, ... } once inspected
};

// Freehand strokes captured for the draw tool, in CSS pixels per page: Map(pageNum -> [stroke]),
// each stroke an array of {x,y}. Converted to PDF user space when applied.
const inkByPage = new Map();

// Rendered pages are cached in memory so navigating back and forth is instant instead of
// re-rendering (and re-uploading the whole document) every time. Entries are keyed by the
// document version, so any edit invalidates the whole cache automatically.
const renderCache = new Map(); // `${version}|${page}|${dpi}` -> png base64
const MAX_CACHED_PAGES = 24;
// Only pages within this many of the current one are rendered ahead of time. The host renders
// serially, so a large radius keeps it endlessly busy on pages you may never look at, starving
// the page you're actually on. Keep it small; the cache still holds far more once you visit them.
const PREFETCH_RADIUS = 3;
let prefetchToken = 0;
let prefetchTimer = null;

// Thumbnails live in their own small-image cache so opening the sidebar can't evict the
// full-size page renders (they render at a different DPI and would otherwise thrash each other).
const thumbCache = new Map(); // `${version}|${page}` -> png base64
const MAX_CACHED_THUMBS = 400;

// Per-page text runs (bboxes in PDF space) for the selectable text layer.
const spanCache = new Map(); // `${version}|${page}` -> [{ text, x, y, width, height }]

/** Installs new working bytes: encode once, bump the version, drop now-stale renders. */
function setWorkingPdf(bytes, base64) {
  state.pdf = bytes;
  state.pdfB64 = base64 ?? bytesToBase64(bytes);
  state.version++;
  renderCache.clear();
  thumbCache.clear();
  spanCache.clear();
  inkByPage.clear();  // uncommitted freehand strokes belong to the old document
  pageRenderFailures.clear(); // a new document's pages have not failed yet
  prefetchToken++; // cancel any in-flight prefetch for the previous document
}

const $ = (id) => document.getElementById(id);
const pagesEl = $('pages');
const scrollArea = $('scroll-area');
const modal = $('modal');

// One entry per page: { wrap, img, overlay, ratio, renderedKey }. Rebuilt on every load.
let pageEls = [];
let nearObserver = null; // renders pages as they approach the viewport
let visObserver = null;  // tracks which page is currently front-and-centre

// ------------------------------------------------------------------ utils

function setStatus(text, busy = false) {
  const statusEl = $('status');
  statusEl.textContent = '';
  if (busy) {
    const spinner = document.createElement('span');
    spinner.className = 'spinner';
    statusEl.appendChild(spinner);
    statusEl.appendChild(document.createTextNode(text));
  } else {
    statusEl.textContent = text;
  }
  // The loading wheel rides along with the busy-status calls that already wrap every
  // host round-trip, so any operation that makes a button "hang" shows a spinner.
  const overlay = $('busy-overlay');
  if (busy) {
    $('busy-text').textContent = text;
    overlay.hidden = false;
  } else {
    overlay.hidden = true;
  }
}

/**
 * Progress for work that runs in the background while the document stays usable (#19: parsing and
 * rating links). It reuses the status bar's spinner, but in its own slot: a background task must
 * never dim the page behind the busy overlay, and must never overwrite a real message in #status.
 * Pass '' to clear it — every caller has to, on failure as well as on success.
 */
function setBackgroundStatus(text) {
  const el = $('link-status');
  el.textContent = '';
  if (!text) {
    el.hidden = true;
    return;
  }
  const spinner = document.createElement('span');
  spinner.className = 'spinner';
  el.appendChild(spinner);
  el.appendChild(document.createTextNode(text)); // textContent — never interpolated HTML
  el.hidden = false;
}

/** Clears a busy status only if it is still the one showing, so a newer message survives. */
function clearBusyStatus(text) {
  if ($('busy-text').textContent === text) setStatus('');
}

function toast(text) {
  setStatus(text);
  setTimeout(() => { if ($('status').textContent === text) setStatus(''); }, 5000);
}

function fail(err) {
  console.error(err);
  activity.add('error', 'error', err?.message ?? String(err));
  setStatus(`⚠ ${err.message ?? err}`);
}

// -------------------------------------------------------- activity console
// A dockable log at the bottom of the window. It is strictly additive: it never touches #status,
// so a toast can't be clobbered by a log entry the way placeField()'s once was.

const CONSOLE_OPEN_KEY = 'activityConsoleOpen';
const CONSOLE_DIAGNOSTICS_KEY = 'activityConsoleDiagnostics';
let diagnosticsOn = false;    // #97: opt-in extra logging (sizes, environment) for bug reports
let consoleRenderedSeq = 0;   // seq of the newest entry that is in the DOM
let consoleFlushQueued = false;

function consoleOpen() { return !$('console-pane').hidden; }

/**
 * One row, built entirely from nodes. Messages and details carry document-derived text — host
 * error strings, file names, form-field names, link URLs — so this must never grow an innerHTML
 * (#74); scripts/check-innerhtml.mjs enforces that.
 */
function consoleEntryEl(entry) {
  const row = document.createElement('div');
  row.className = `console-entry ${entry.level}${entry.diagnostic ? ' diag' : ''}`;
  const time = document.createElement('span');
  time.className = 'console-time';
  time.textContent = formatTime(entry.time);
  const level = document.createElement('span');
  level.className = 'console-level';
  level.textContent = entry.level;
  const message = document.createElement('span');
  message.className = 'console-message';
  message.textContent = entry.message;
  if (entry.detail) {
    const detail = document.createElement('span');
    detail.className = 'console-detail';
    detail.textContent = ` — ${entry.detail}`;
    message.appendChild(detail);
  }
  row.append(time, level, message);
  return row;
}

/**
 * Appends everything logged since the last flush in one batch on the next frame — and does
 * nothing at all while the pane is closed, so rendering a link- or field-heavy page cannot pay
 * for per-entry DOM work (#19). Opening the pane rebuilds from the store.
 */
function scheduleConsoleFlush() {
  if (consoleFlushQueued || !consoleOpen()) return;
  consoleFlushQueued = true;
  requestAnimationFrame(flushConsole);
}

function flushConsole() {
  consoleFlushQueued = false;
  const logEl = $('console-log');
  const pending = activity.entries.filter((e) => e.seq > consoleRenderedSeq);
  if (pending.length > 0) {
    const batch = document.createDocumentFragment();
    for (const entry of pending) batch.appendChild(consoleEntryEl(entry));
    logEl.appendChild(batch);
    consoleRenderedSeq = pending[pending.length - 1].seq;
    // The store caps what it retains; drop the rows it no longer holds so a long session's DOM
    // stays bounded too.
    while (logEl.childElementCount > activity.capacity) logEl.firstElementChild.remove();
    if ($('console-autoscroll').checked) logEl.scrollTop = logEl.scrollHeight;
  }
  updateConsoleCount();
}

function updateConsoleCount() {
  const total = activity.entries.length;
  const dropped = activity.dropped;
  $('console-count').textContent = total === 0
    ? 'no entries yet'
    : `${total} entr${total === 1 ? 'y' : 'ies'}${dropped > 0 ? `, oldest ${dropped} dropped` : ''}`;
}

function setConsoleOpen(open, persist = true) {
  $('console-pane').hidden = !open;
  $('btn-console').setAttribute('aria-pressed', String(open));
  if (open) {
    $('console-log').textContent = '';  // rebuild from the store, which kept logging while closed
    consoleRenderedSeq = 0;
    flushConsole();
  }
  // Remembering the pane's state is a one-key write; the log itself is deliberately not persisted.
  if (persist) {
    chrome.storage?.local?.set({ [CONSOLE_OPEN_KEY]: open })
      ?.catch((e) => activity.add('warn', 'could not save the console state', e?.message ?? e));
  }
}

async function copyConsole() {
  try {
    await navigator.clipboard.writeText(activity.toText());
    toast('Activity log copied to the clipboard.');
  } catch (e) {
    fail(e);
  }
}

/** #97: turns opt-in diagnostic logging on/off, records an environment snapshot when enabled. */
function setDiagnostics(on, persist = true) {
  diagnosticsOn = on;
  $('console-diagnostics').checked = on;
  if (persist) {
    chrome.storage?.local?.set({ [CONSOLE_DIAGNOSTICS_KEY]: on })
      ?.catch((e) => activity.add('warn', 'could not save the diagnostics setting', e?.message ?? e));
  }
  if (on) logEnvironment();
}

/** Logs the runtime environment (versions, browser, platform) as diagnostic entries. */
async function logEnvironment() {
  const m = chrome.runtime.getManifest();
  activity.add('info', 'diagnostics enabled', 'extra detail is now recorded', { diagnostic: true });
  activity.add('info', 'extension version', m.version, { diagnostic: true });
  activity.add('info', 'browser', browserSummary(), { diagnostic: true });
  activity.add('info', 'platform', navigator.platform || 'unknown', { diagnostic: true });
  try {
    // Prefer the host's full self-report; fall back to ping's version on an older host.
    let logged = false;
    try {
      for (const line of hostDiagnosticsLines(await host.call('diagnostics'))) {
        activity.add('info', 'native host', line, { diagnostic: true });
        logged = true;
      }
    } catch {
      // Host predates the 'diagnostics' action; fall through to ping below.
    }
    if (!logged) {
      const pong = await host.call('ping');
      activity.add('info', 'native host version', pong?.version ?? 'unknown', { diagnostic: true });
    }
  } catch {
    // A failed call is already logged by the host.call wrapper; nothing to add.
  }
}

/** Saves the activity log to a text file (with an environment header) for attaching to bug reports. */
function downloadConsole() {
  const m = chrome.runtime.getManifest();
  const header = [
    'reDACT — activity log',
    `Extension: v${m.version}`,
    `Browser: ${browserSummary()}`,
    `Platform: ${navigator.platform || 'unknown'}`,
    `Generated: ${new Date().toISOString()}`,
    '', '',
  ].join('\n');
  const blob = new Blob([header + activity.toText() + '\n'], { type: 'text/plain' });
  chrome.downloads.download({
    url: URL.createObjectURL(blob),
    filename: 'pdf-editor-activity-log.txt',
    saveAs: true,
  });
  activity.add('info', 'activity log downloaded', '', { diagnostic: diagnosticsOn });
  toast('Activity log downloaded.');
}

function initConsole() {
  activity.subscribe((entry) => {
    if (entry === null) {
      $('console-log').textContent = '';
      consoleRenderedSeq = 0;
      updateConsoleCount();
      return;
    }
    scheduleConsoleFlush();
  });
  $('btn-console').addEventListener('click', () => setConsoleOpen(!consoleOpen()));
  $('btn-about').addEventListener('click', showAbout);
  $('console-close').addEventListener('click', () => setConsoleOpen(false));
  $('console-clear').addEventListener('click', () => activity.clear());
  $('console-copy').addEventListener('click', copyConsole);
  $('console-download').addEventListener('click', downloadConsole);
  $('console-diagnostics').addEventListener('change', (e) => setDiagnostics(e.target.checked));
  updateConsoleCount();
}

/** Restores the pane's last open/closed state (the log itself never persists). */
async function restoreConsoleState() {
  try {
    const stored = await chrome.storage.local.get({
      [CONSOLE_OPEN_KEY]: false, [CONSOLE_DIAGNOSTICS_KEY]: false,
    });
    if (stored[CONSOLE_OPEN_KEY]) setConsoleOpen(true, false);
    if (stored[CONSOLE_DIAGNOSTICS_KEY]) setDiagnostics(true, false);
  } catch (e) {
    activity.add('warn', 'could not read the console state', e?.message ?? e);
  }
}

function pageSize(pageNum = state.page) {
  return state.info.pages[pageNum - 1];
}

// Rotation transforms between the displayed image and the unrotated crop box now live in
// geometry.js (imported above); cssToPdf/pdfRectToCss below add the crop-box origin and scale.

/** CSS pixel (relative to a page's image) → PDF user-space point on that page. */
function cssToPdf(pageNum, img, cssX, cssY) {
  const p = pageSize(pageNum);
  const [fx, fy] = displayToPage(p.rotation, cssX / img.clientWidth, cssY / img.clientHeight);
  return { x: p.x + fx * p.width, y: p.y + fy * p.height };
}

/**
 * PDF user-space rect → CSS box on the page image. `size` optionally supplies the image's
 * {w, h} measured once by the caller: reading clientWidth per call forces a synchronous layout,
 * which on a page carrying hundreds of link hotspots turns the overlay build into layout thrash.
 */
function pdfRectToCss(pageNum, img, region, size = null) {
  const p = pageSize(pageNum);
  const [ua, va] = pageToDisplay(p.rotation, (region.x - p.x) / p.width, (region.y - p.y) / p.height);
  const [ub, vb] = pageToDisplay(p.rotation,
    (region.x + region.width - p.x) / p.width, (region.y + region.height - p.y) / p.height);
  const w = size ? size.w : img.clientWidth;
  const h = size ? size.h : img.clientHeight;
  return {
    left: Math.min(ua, ub) * w,
    top: Math.min(va, vb) * h,
    width: Math.abs(ua - ub) * w,
    height: Math.abs(va - vb) * h,
  };
}

// ------------------------------------------------------------ doc lifecycle

async function loadDocument(bytes, fileName, { pushHistory = false, password } = {}) {
  if (pushHistory && state.pdf) {
    state.history.push({ pdf: state.pdf, pdfB64: state.pdfB64, info: state.info, password: state.password });
    if (state.history.length > 10) state.history.shift();
    state.future = []; // a fresh edit invalidates any redo history
  }
  const pdfB64 = bytesToBase64(bytes);
  let info;
  try {
    info = await host.call('info', { pdf: pdfB64, pdfPassword: password ?? state.password });
  } catch (e) {
    if (/password/i.test(e.message)) {
      const entered = await promptDialog('This PDF is password-protected', [
        { id: 'pw', label: 'Password', type: 'password' },
      ]);
      if (!entered) return;
      return loadDocument(bytes, fileName, { pushHistory, password: entered.pw });
    }
    throw e;
  }
  setWorkingPdf(bytes, pdfB64);
  state.password = password ?? state.password;
  state.info = info;
  if (fileName) state.fileName = fileName;
  state.page = Math.min(state.page, info.pageCount);
  state.regions = state.regions.filter((r) => r.page <= info.pageCount);
  // Paint the document first; the signature/active-content scans (which walk the whole file) then
  // run in the background and light up their badges a moment later rather than delaying first paint.
  state.signatures = [];
  state.safety = null;
  // Clear the on-page overlays so a reload never shows the previous document's links/fields; the
  // background scans below repopulate them for the new document a moment later.
  state.linkHotspots = [];
  state.urlVerdicts = [];
  state.formFields = [];
  const freshOpen = !!fileName; // only warn about active content when a document is first opened
  if (freshOpen) {
    // A panel left open across an Open must not keep showing the PREVIOUS document's data (#24).
    // state.links backs the Links list and was never cleared here, and openLinks()/openForms()
    // only refetch when the panel is (re)opened — so a panel already on screen kept the old rows.
    // Drop the stale lists (model and DOM) and close the panel; reopening it fetches for the new
    // document. Guarded on freshOpen so an edit/undo reload never closes the panel being worked in.
    state.links = [];
    state.scripts = [];
    renderLinks();
    hidePanels();
  }
  await showDocument();
  updateChrome();
  startScans().then(() => {
    updateChrome();
    if (freshOpen) warnActiveContent();
    refreshLinks(); // fetch, rate, and draw the clickable link hotspots
    refreshFormFields(); // outline the document's form fields on the page
  });
}

/** On opening a document, tell the user if it carries JavaScript or links (disabled by default). */
function warnActiveContent() {
  const s = state.safety;
  const bits = [];
  if (s?.javaScriptCount > 0) bits.push(`JavaScript (${s.javaScriptCount})`);
  if (URL_SCANNING_ENABLED && s?.urlCount > 0) bits.push(`${s.urlCount} link${s.urlCount === 1 ? '' : 's'}`);
  if (bits.length === 0) return;
  toast(`⚠ This document contains ${bits.join(' and ')} — disabled and removed when you save. ` +
    'Click the badge above to review or keep it.');
}

async function applyResult(base64Pdf, message) {
  await loadDocument(base64ToBytes(base64Pdf), null, { pushHistory: true });
  toast(message);
}

/**
 * Fast apply for content-only edits (highlight / draw / add & edit text) that don't change page
 * geometry, signatures, or active-content: skips the info call, the signature/JS-URL rescans, and
 * the full page-column rebuild, re-rendering only the affected pages. Keeps highlighting snappy.
 */
async function applyContentEdit(base64Pdf, pages, message) {
  state.history.push(snapshot());
  if (state.history.length > 10) state.history.shift();
  state.future = [];
  setWorkingPdf(base64ToBytes(base64Pdf), base64Pdf);
  for (const p of pages) {
    const pe = pageEls[p - 1];
    if (pe) { pe.renderedKey = null; pe.textKey = null; }
  }
  await Promise.all(pages.map((p) => renderPageEl(p)));
  updateChrome();
  toast(message);
}

async function refreshSignatures() {
  try {
    const result = await host.call('signatures', {
      pdf: state.pdfB64, pdfPassword: state.password,
    });
    state.signatures = result.signatures ?? [];
  } catch (e) {
    // Kept non-fatal — but "this document is unsigned" and "we could not tell" are different
    // claims, and the badge only shows the first. Say which one this is (#72).
    activity.add('warn', 'signatures could not be listed',
      `${e?.message ?? e} — the document will be shown as unsigned`);
    state.signatures = [];
  }
}

/**
 * Starts the signature and active-content scans of the working document, which run after it is
 * painted. Save waits for the active-content scan (state.safetyScan), because its result decides
 * what Save strips: a Save pressed while it was still running found state.safety null and wrote
 * the document's scripts and links out unstripped.
 */
function startScans() {
  state.safetyScan = refreshSafety();
  return Promise.all([refreshSignatures(), state.safetyScan]);
}

/** Scans for embedded JavaScript / URL actions so the UI can flag (and, by default, strip) them. */
async function refreshSafety() {
  try {
    state.safety = await host.call('scan-safety', {
      pdf: state.pdfB64, pdfPassword: state.password,
    });
  } catch (e) {
    // A failed scan is not a clean document: nothing gets badged, and the strip-on-save default
    // has nothing to strip. That silence is exactly what #72 is about.
    activity.add('warn', 'active-content scan failed',
      `${e?.message ?? e} — embedded JavaScript and link URLs are unknown for this document`);
    state.safety = null;
  }
}

function currentDpi() {
  return Math.min(300, Math.round(state.dpi * state.zoom));
}

function cacheKey(page, dpi) {
  return `${state.version}|${page}|${dpi}`;
}

/** Renders one page (or returns it from cache), memoising the result. */
async function renderToCache(page, dpi) {
  const key = cacheKey(page, dpi);
  const cached = renderCache.get(key);
  if (cached !== undefined) {
    renderCache.delete(key); // move to most-recently-used
    renderCache.set(key, cached);
    return cached;
  }
  const result = await host.call('render', {
    pdf: state.pdfB64, page, dpi, pdfPassword: state.password,
  });
  renderCache.set(key, result.png);
  while (renderCache.size > MAX_CACHED_PAGES) {
    renderCache.delete(renderCache.keys().next().value); // evict least-recently-used
  }
  return result.png;
}

/**
 * Fills the cache for a few pages either side of `centerPage`, nearest first, in the background.
 * Debounced so rapid scrolling doesn't restart it on every page and flood the serial host; the
 * radius is deliberately small so prefetch finishes quickly and leaves the host free for the page
 * you're actually viewing.
 */
function prefetchAround(centerPage, dpi) {
  clearTimeout(prefetchTimer);
  prefetchTimer = setTimeout(() => {
    const token = ++prefetchToken;
    const total = state.info?.pageCount ?? 0;
    const order = [];
    for (let d = 1; d <= PREFETCH_RADIUS; d++) {
      if (centerPage + d <= total) order.push(centerPage + d); // ahead first — the likely direction
      if (centerPage - d >= 1) order.push(centerPage - d);
    }
    (async () => {
      for (const page of order) {
        if (token !== prefetchToken) return; // navigation, zoom, or edit superseded us
        if (renderCache.has(cacheKey(page, dpi))) continue;
        try {
          await renderToCache(page, dpi);
        } catch (e) {
          // Never fatal — the page renders on demand instead. host.call already logged the error
          // itself; this records which page it cost us, so a document that only ever fails to
          // prefetch is distinguishable from one that never tried.
          activity.add('info', `prefetch of page ${page} abandoned`, e?.message ?? e);
        }
        await new Promise((r) => setTimeout(r, 0)); // yield so the UI stays responsive
      }
    })();
  }, 180);
}

function displaySize(pageNum) {
  const p = pageSize(pageNum);
  const factor = state.zoom * (96 / 72);
  const swap = p.rotation === 90 || p.rotation === 270; // landscape when rotated a quarter-turn
  return {
    width: (swap ? p.height : p.width) * factor,
    height: (swap ? p.width : p.height) * factor,
  };
}

/** (Re)builds the scrollable column of page placeholders and starts lazy rendering. */
function buildPages() {
  nearObserver?.disconnect();
  visObserver?.disconnect();
  pageEls = [];
  pagesEl.innerHTML = '';
  $('empty-state').style.display = 'none';

  for (let n = 1; n <= state.info.pageCount; n++) {
    const { width, height } = displaySize(n);
    const wrap = document.createElement('div');
    wrap.className = 'page';
    wrap.dataset.page = String(n);
    wrap.style.width = `${width}px`;
    wrap.style.height = `${height}px`;

    const img = document.createElement('img');
    img.className = 'page-image';
    img.alt = `Page ${n}`;
    img.draggable = false;

    const overlay = document.createElement('div');
    overlay.className = 'overlay';
    if (state.tool !== 'select') overlay.classList.add('tool-active');

    wrap.append(img, overlay);
    pagesEl.appendChild(wrap);
    pageEls.push({ wrap, img, overlay, ratio: 0, renderedKey: null });
  }

  // Render pages a little before they scroll into view; keep the cache warm around them.
  nearObserver = new IntersectionObserver((entries) => {
    for (const e of entries) if (e.isIntersecting) renderPageEl(Number(e.target.dataset.page));
  }, { root: scrollArea, rootMargin: '500px 0px' });

  // Track which page is most in view so the page counter and nav stay correct.
  visObserver = new IntersectionObserver((entries) => {
    for (const e of entries) {
      const pe = pageEls[Number(e.target.dataset.page) - 1];
      if (pe) pe.ratio = e.isIntersecting ? e.intersectionRatio : 0;
    }
    let best = state.page;
    let bestRatio = -1;
    pageEls.forEach((pe, i) => { if (pe.ratio > bestRatio) { bestRatio = pe.ratio; best = i + 1; } });
    if (best !== state.page) {
      state.page = best;
      updateNav();
      prefetchAround(best, currentDpi());
    }
  }, { root: scrollArea, threshold: [0, 0.25, 0.5, 0.75, 1] });

  for (const pe of pageEls) { nearObserver.observe(pe.wrap); visObserver.observe(pe.wrap); }
  pagesEl.classList.toggle('select-mode', state.tool === 'select');
  drawRegions();
}

/** Renders one page's image (from cache when possible) into its placeholder. */
async function renderPageEl(pageNum) {
  const pe = pageEls[pageNum - 1];
  if (!pe) return;
  const dpi = currentDpi();
  const key = cacheKey(pageNum, dpi);
  if (pe.renderedKey === key) return;

  const cached = renderCache.get(key);
  if (cached !== undefined) {
    pe.img.src = `data:image/png;base64,${cached}`;
    pe.renderedKey = key;
    clearPageError(pe, pageNum);
    ensureTextLayer(pe, pageNum);
    buildLinkLayer(pe, pageNum); // a cached page still needs its overlays (re)built for this page
    buildFieldLayer(pe, pageNum);
    return;
  }
  try {
    const png = await renderToCache(pageNum, dpi);
    if (cacheKey(pageNum, currentDpi()) !== key || !pe.wrap.isConnected) return;
    pe.img.src = `data:image/png;base64,${png}`;
    pe.renderedKey = key;
    clearPageError(pe, pageNum);
    ensureTextLayer(pe, pageNum);
    buildLinkLayer(pe, pageNum); // draw any link hotspots for this page
    buildFieldLayer(pe, pageNum); // outline any form fields for this page
  } catch (e) {
    // Retrying on scroll is deliberate, so this still doesn't throw and still leaves the
    // placeholder in place. What changes (#72) is that the failure is no longer invisible: it is
    // always logged, and a page that fails *twice running for the same render* is not a hiccup
    // during fast scrolling — it gets a visible placeholder rather than the silent blank
    // rectangle an out-of-memory rasteriser produced in #20.
    const failures = (pageRenderFailures.get(pageNum) ?? 0) + 1;
    pageRenderFailures.set(pageNum, failures);
    const repeated = failures >= PAGE_ERROR_AFTER;
    activity.add(repeated ? 'error' : 'warn', `page ${pageNum} did not render`,
      `${e?.message ?? e}${repeated ? ' (repeated)' : ' — retrying when it scrolls back into view'}`);
    if (repeated && pe.renderedKey !== key && pe.wrap.isConnected) showPageError(pe);
  }
}

// Consecutive render failures per page, cleared the moment one succeeds (and on a new document,
// via setWorkingPdf). Keyed by page rather than by page element because zooming rebuilds every
// element, and by page rather than by render key because the observer retries the same key.
const pageRenderFailures = new Map();
const PAGE_ERROR_AFTER = 2;

/** Replaces a persistently blank page with something that says so. */
function showPageError(pe) {
  if (pe.wrap.querySelector('.page-error')) return;
  const note = document.createElement('div');
  note.className = 'page-error';
  note.textContent = '⚠ This page could not be rendered. See Help ▸ Activity console for details.';
  pe.wrap.insertBefore(note, pe.overlay);
}

function clearPageError(pe, pageNum) {
  pageRenderFailures.delete(pageNum);
  pe.wrap.querySelector('.page-error')?.remove();
}

// ------------------------------------------------------ selectable text layer

/** Fetches (and caches) the page's text runs and lays an invisible selectable layer over it. */
async function ensureTextLayer(pe, pageNum) {
  const key = `${state.version}|${pageNum}`;
  let spans = spanCache.get(key);
  if (spans === undefined) {
    try {
      const result = await host.call('page-text', {
        pdf: state.pdfB64, page: pageNum, pdfPassword: state.password,
      });
      spans = result.spans ?? [];
      spanCache.set(key, spans);
    } catch (e) {
      // Selection is a nicety; never block rendering on it. But "this page has no selectable
      // text" and "we could not read its text" look identical on screen (#72).
      activity.add('warn', `text layer unavailable on page ${pageNum}`,
        `${e?.message ?? e} — text on this page cannot be selected or right-click edited`);
      return;
    }
  }
  if (!pe.wrap.isConnected || pe.textKey === key) return;
  buildTextLayer(pe, pageNum, spans);
  pe.textKey = key;
}

function buildTextLayer(pe, pageNum, spans) {
  pe.wrap.querySelector('.text-layer')?.remove();
  if (spans.length === 0) return;
  const layer = document.createElement('div');
  layer.className = 'text-layer';
  for (const s of spans) {
    const css = pdfRectToCss(pageNum, pe.img, s);
    if (css.width <= 0 || css.height <= 0) continue;
    const el = document.createElement('span');
    el.textContent = s.text;
    el.style.cssText = `left:${css.left}px;top:${css.top}px;font-size:${css.height}px;`;
    el.dataset.w = css.width;
    // Absolute PDF-space box, so a right-click can edit exactly this run in place.
    el.dataset.region = JSON.stringify({ page: pageNum, x: s.x, y: s.y, width: s.width, height: s.height });
    layer.appendChild(el);
  }
  pe.wrap.insertBefore(layer, pe.overlay);
  // One measure/adjust pass: stretch each run horizontally to match its rendered width.
  for (const el of layer.children) {
    const natural = el.offsetWidth;
    const target = Number(el.dataset.w);
    if (natural > 0) el.style.transform = `scaleX(${(target / natural).toFixed(4)})`;
  }
}

/** Rebuilds the page column for the current document and renders the visible page now. */
async function showDocument() {
  buildPages();
  buildThumbnails();  // no-op unless the sidebar is open; keyed off the new document version
  await renderPageEl(state.page); // render the landing page eagerly rather than waiting on the observer
  prefetchAround(state.page, currentDpi());
}

/** Smoothly scrolls a page to the top of the viewport. */
function goToPage(pageNum, behavior = 'smooth') {
  const n = Math.max(1, Math.min(state.info?.pageCount ?? 1, pageNum));
  pageEls[n - 1]?.wrap.scrollIntoView({ behavior, block: 'start' });
}

/** Re-lays-out every page at a new zoom, keeping the current page in view. */
function setZoom(z) {
  if (!state.pdf) return;
  const clamped = Math.max(0.5, Math.min(3, Math.round(z * 100) / 100));
  if (clamped === state.zoom) return;
  state.zoom = clamped;
  const anchor = state.page;
  buildPages();
  redrawInk(); // overlays were rebuilt; repaint any in-progress strokes at the new scale
  renderPageEl(anchor);
  goToPage(anchor, 'auto'); // jump, not smooth, so the same page stays put
  updateChrome();
}

/** Parses the editable page box and jumps there. */
function jumpToTypedPage() {
  const n = Number.parseInt($('page-input').value, 10);
  if (Number.isFinite(n)) goToPage(n, 'auto');
  $('page-input').value = String(state.page); // normalise (clamp / reject junk)
  $('page-input').blur();
}

// --------------------------------------------------------- thumbnail sidebar

const THUMB_DPI = 26;              // small, fixed resolution for the page rail
let thumbEls = [];                 // one { wrap, img, renderedKey } per page
let thumbObserver = null;          // renders thumbnails lazily as they scroll into view

function toggleSidebar(force) {
  state.sidebarOpen = force ?? !state.sidebarOpen;
  $('thumbnails').hidden = !state.sidebarOpen;
  $('btn-sidebar').classList.toggle('active', state.sidebarOpen);
  if (state.sidebarOpen) buildThumbnails();
}

/** (Re)builds the thumbnail rail for the current document. */
function buildThumbnails() {
  if (!state.sidebarOpen || !state.info) return;
  thumbObserver?.disconnect();
  const rail = $('thumbnails');
  rail.innerHTML = '';
  thumbEls = [];
  for (let n = 1; n <= state.info.pageCount; n++) {
    const wrap = document.createElement('div');
    wrap.className = 'thumb';
    wrap.dataset.page = String(n);
    const img = document.createElement('img');
    img.alt = `Page ${n}`;
    const skeleton = document.createElement('div');
    skeleton.className = 'thumb-skeleton';
    const num = document.createElement('span');
    num.className = 'thumb-num';
    num.textContent = String(n);
    wrap.append(skeleton, num);
    wrap.addEventListener('click', () => goToPage(n, 'smooth'));
    rail.appendChild(wrap);
    thumbEls.push({ wrap, img, skeleton, renderedKey: null });
  }
  thumbObserver = new IntersectionObserver((entries) => {
    for (const e of entries) if (e.isIntersecting) renderThumb(Number(e.target.dataset.page));
  }, { root: rail, rootMargin: '300px 0px' });
  for (const t of thumbEls) thumbObserver.observe(t.wrap);
  markCurrentThumb();
}

/** Renders one thumbnail (from its own cache), so it never evicts full-size page renders. */
async function renderThumbToCache(page) {
  const key = `${state.version}|${page}`;
  const cached = thumbCache.get(key);
  if (cached !== undefined) {
    thumbCache.delete(key);
    thumbCache.set(key, cached); // move to most-recently-used
    return cached;
  }
  const result = await host.call('render', {
    pdf: state.pdfB64, page, dpi: THUMB_DPI, pdfPassword: state.password,
  });
  thumbCache.set(key, result.png);
  while (thumbCache.size > MAX_CACHED_THUMBS) {
    thumbCache.delete(thumbCache.keys().next().value);
  }
  return result.png;
}

async function renderThumb(pageNum) {
  const t = thumbEls[pageNum - 1];
  if (!t) return;
  const key = `${state.version}|${pageNum}`;
  if (t.renderedKey === key) return;
  try {
    const png = await renderThumbToCache(pageNum);
    if (!t.wrap.isConnected) return;
    t.img.src = `data:image/png;base64,${png}`;
    if (t.skeleton.parentNode) t.skeleton.replaceWith(t.img);
    t.renderedKey = key;
  } catch (e) {
    // Leave the skeleton; it retries when it next scrolls into view. Logged so a rail that stays
    // grey is traceable to the failure that caused it rather than looking like slow rendering.
    activity.add('warn', `thumbnail for page ${pageNum} did not render`, e?.message ?? e);
  }
}

/** Highlights the thumbnail for the current page and scrolls it into view when needed. */
function markCurrentThumb() {
  if (!state.sidebarOpen) return;
  const current = thumbEls[state.page - 1];
  for (const t of thumbEls) t.wrap.classList.remove('current');
  if (!current) return;
  current.wrap.classList.add('current');
  // Only scroll the rail when the current thumbnail isn't already visible — scrolling it on
  // every scroll tick would fight the user and thrash layout.
  const rail = $('thumbnails').getBoundingClientRect();
  const box = current.wrap.getBoundingClientRect();
  if (box.top < rail.top || box.bottom > rail.bottom) current.wrap.scrollIntoView({ block: 'nearest' });
}

// ----------------------------------------------------------------- rotate

async function rotateCurrentPage(degrees) {
  if (!state.pdf) return;
  try {
    setStatus(`Rotating page ${state.page}…`, true);
    const result = await host.call('rotate', {
      pdf: state.pdfB64, pages: [state.page], degrees, pdfPassword: state.password,
    });
    const keepPage = state.page;
    await applyResult(result.pdf, `Rotated page ${keepPage}.`);
    goToPage(keepPage, 'auto');
  } catch (e) {
    fail(e);
  }
}

/** Updates just the page counter / nav buttons — cheap enough to call while scrolling. */
function updateNav() {
  if (!state.pdf) return;
  // Don't clobber what the user is typing into the page box while it has focus.
  if (document.activeElement !== $('page-input')) $('page-input').value = String(state.page);
  $('page-total').textContent = String(state.info.pageCount);
  $('btn-prev').disabled = state.page <= 1;
  $('btn-next').disabled = state.page >= state.info.pageCount;
  $('zoom-label').textContent = `${Math.round(state.zoom * 100)}%`;
  markCurrentThumb();
}

function updateChrome() {
  const loaded = !!state.pdf;
  for (const id of ['btn-save', 'btn-print', 'btn-sidebar', 'tool-text', 'tool-draw',
    'tool-highlight', 'tool-edit', 'tool-move', 'tool-redact', 'tool-sign',
    'btn-rotate-left', 'btn-rotate-right', 'btn-forms', 'btn-organize', 'btn-js', 'btn-sanitize', 'btn-ocr',
    'btn-find', 'btn-merge', 'btn-watermark', 'btn-bates', 'btn-flatten', 'btn-protect', 'btn-digital',
    'menu-read-trigger', 'menu-edit-trigger', 'btn-compare',
    'btn-prev', 'btn-next', 'btn-zoom-in', 'btn-zoom-out']) {
    $(id).disabled = !loaded;
  }
  $('page-input').disabled = !loaded;
  // "Remove encryption" only makes sense on an open, encrypted document.
  $('btn-decrypt').disabled = !loaded || !(state.info?.encrypted || state.password);
  $('btn-undo').disabled = state.history.length === 0;
  $('btn-redo').disabled = state.future.length === 0;
  if (loaded) {
    updateNav();
    document.title = `${state.fileName} — reDACT`;
  }
  const badgesEl = $('badges');
  badgesEl.innerHTML = '';
  if (state.info?.encrypted || state.password) {
    const badge = document.createElement('span');
    badge.className = 'badge locked';
    badge.textContent = '🔒 encrypted';
    badgesEl.appendChild(badge);
  }
  if (loaded && state.safety?.javaScriptCount > 0) {
    const badge = document.createElement('span');
    badge.className = 'badge warn';
    badge.title = 'This document contains embedded JavaScript — click for details';
    badge.textContent = `⚠ JavaScript ${state.keepActiveContent ? 'kept' : 'disabled'}`;
    badge.addEventListener('click', showSafetyDialog);
    badgesEl.appendChild(badge);
  }
  if (loaded && URL_SCANNING_ENABLED && state.safety?.urlCount > 0) {
    const badge = document.createElement('span');
    badge.className = 'badge warn';
    badge.title = 'This document contains links — click to review';
    badge.textContent = `🔗 links ${state.keepLinks ? 'enabled' : 'disabled'}`;
    badge.addEventListener('click', openLinks);
    badgesEl.appendChild(badge);
  }
  for (const s of state.signatures) {
    // s.name/s.signer come from the PDF's (attacker-controlled) signature
    // metadata -- never treat them as HTML.
    const badge = document.createElement('span');
    badge.className = 'badge signed';
    badge.title = s.name ?? '';
    badge.textContent = `🖋 ${s.signer ?? 'signed'}${s.valid ? ' ✓' : ' ✗'}`;
    badgesEl.appendChild(badge);
  }
}

// -------------------------------------------------------------- region UI

function drawRegions() {
  for (const pe of pageEls) pe.overlay.querySelectorAll('.region').forEach((el) => el.remove());
  for (const [index, region] of state.regions.entries()) {
    addRegionDiv(region, 'redact', `#${index + 1}`);
  }
  if (state.pendingEditRegion) addRegionDiv(state.pendingEditRegion, 'edit');
  if (state.pendingSignRegion) addRegionDiv(state.pendingSignRegion, 'sign');
  renderRedactList();
}

function addRegionDiv(region, kind, label = '') {
  const pe = pageEls[region.page - 1];
  if (!pe) return; // page not currently laid out
  const css = pdfRectToCss(region.page, pe.img, region);
  const div = document.createElement('div');
  div.className = `region ${kind}`;
  div.style.cssText =
    `left:${css.left}px;top:${css.top}px;width:${css.width}px;height:${css.height}px;`;
  div.textContent = label;
  pe.overlay.appendChild(div);
}

// Redaction Privacy levels, indexed by intensity 0–4 (see #privacy). Named here so both the slider
// label and the per-area chips can show them.
const REDACT_INTENSITY = ['Exact', 'Merge words', 'Rounded', 'Full line', 'Textured'];

/** The current global Privacy intensity (0–4) from the slider. */
function redactIntensity() { return Number($('redact-intensity')?.value ?? 0); }

/** Marks a region for redaction, stamping it with the current Privacy intensity (per-redaction). */
function markRegion(region) {
  region.intensity = redactIntensity();
  state.regions.push(region);
}

function renderRedactList() {
  const list = $('redact-list');
  list.innerHTML = '';
  for (const [index, region] of state.regions.entries()) {
    const item = document.createElement('li');
    const label = document.createElement('span');
    // Compact chip label; the on-page box already carries its number (#N). Show the per-area
    // Privacy type when it is not the plain default, so mixed settings are visible at a glance.
    const level = region.intensity ?? 0;
    label.textContent = level > 0
      ? `#${index + 1} p${region.page} · ${REDACT_INTENSITY[level]}`
      : `#${index + 1} p${region.page}`;
    item.appendChild(label);
    const remove = document.createElement('button');
    remove.textContent = '✕';
    remove.title = `Remove marked area #${index + 1}`;
    remove.setAttribute('aria-label', `Remove marked area #${index + 1} on page ${region.page}`);
    remove.addEventListener('click', () => {
      state.regions.splice(index, 1);
      drawRegions();
    });
    item.appendChild(remove);
    list.appendChild(item);
  }
  const count = state.regions.length;
  $('redact-count').textContent = count === 0
    ? 'No areas marked yet.'
    : `${count} area${count === 1 ? '' : 's'} marked — click ✕ to remove one.`;
  const any = count > 0;
  $('redact-preview').disabled = !any;
  $('redact-apply').disabled = !any;
  $('redact-clear').disabled = !any;
}

// --------------------------------------------------------- freehand drawing

/** Returns (creating if needed) the SVG layer that previews strokes on a page. */
function inkLayer(pe) {
  let svg = pe.overlay.querySelector('.ink-layer');
  if (!svg) {
    svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'ink-layer');
    pe.overlay.appendChild(svg);
  }
  return svg;
}

/** CSS pixels per PDF point on a page (for sizing the live stroke preview). */
function pxPerPoint(pageNum, img) {
  const p = pageSize(pageNum);
  const swap = p.rotation === 90 || p.rotation === 270;
  return img.clientWidth / (swap ? p.height : p.width);
}

let inkDrag = null; // { pe, pageNum, points: [{x,y}], poly }

/** Renders every captured stroke for a page (used after re-layout / zoom). */
function redrawInk() {
  for (const pe of pageEls) {
    const strokes = inkByPage.get(Number(pe.wrap.dataset.page));
    const svg = pe.overlay.querySelector('.ink-layer');
    if (svg) svg.innerHTML = '';
    if (!strokes || strokes.length === 0) continue;
    const layer = inkLayer(pe);
    const w = state.drawWidth * pxPerPoint(Number(pe.wrap.dataset.page), pe.img);
    for (const stroke of strokes) addPolyline(layer, stroke, w);
  }
}

function addPolyline(svg, points, widthPx) {
  const poly = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
  poly.setAttribute('points', points.map((pt) => `${pt.x},${pt.y}`).join(' '));
  poly.setAttribute('stroke', state.drawColor);
  poly.setAttribute('stroke-width', String(widthPx));
  svg.appendChild(poly);
  return poly;
}

// Drag-to-draw for edit/redact/sign/text tools. Delegated on the page column so a drag is
// attributed to whichever page it started on.
let drag = null;
// Grab-and-drag of an existing run of text with the Move tool.
let moveDrag = null; // { pe, pageNum, start:{x,y}, startPdf, span, ghost }

pagesEl.addEventListener('pointerdown', (e) => {
  if (state.tool === 'select' || !state.pdf) return;
  const overlay = e.target.closest('.overlay');
  if (!overlay) return;
  const pe = pageEls.find((p) => p.overlay === overlay);
  if (!pe) return;
  const rect = overlay.getBoundingClientRect();

  // Move tool: pick up the text run (or the image) under the cursor and drag it to a new spot.
  if (state.tool === 'move') {
    const pageNum = Number(pe.wrap.dataset.page);
    const start = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    const startPdf = cssToPdf(pageNum, pe.img, start.x, start.y);
    const spans = spanCache.get(`${state.version}|${pageNum}`) ?? [];
    // A text run under the cursor moves as text; otherwise the drag tries to move an image there.
    const span = spans.find((s) => startPdf.x >= s.x && startPdf.x <= s.x + s.width &&
      startPdf.y >= s.y && startPdf.y <= s.y + s.height) ?? null;
    moveDrag = { pe, pageNum, start, startPdf, span, ghost: null };
    overlay.setPointerCapture(e.pointerId);
    return;
  }

  if (state.tool === 'draw') {
    const pageNum = Number(pe.wrap.dataset.page);
    const point = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    const poly = addPolyline(inkLayer(pe), [point], state.drawWidth * pxPerPoint(pageNum, pe.img));
    inkDrag = { pe, pageNum, points: [point], poly };
    overlay.setPointerCapture(e.pointerId);
    return;
  }

  drag = { pe, pageNum: Number(pe.wrap.dataset.page), x0: e.clientX - rect.left, y0: e.clientY - rect.top, div: null };
  overlay.setPointerCapture(e.pointerId);
});

pagesEl.addEventListener('pointermove', (e) => {
  if (moveDrag) {
    if (!moveDrag.span) return; // image move: no run box to ghost, result appears on release
    const r = moveDrag.pe.overlay.getBoundingClientRect();
    const dx = (e.clientX - r.left) - moveDrag.start.x;
    const dy = (e.clientY - r.top) - moveDrag.start.y;
    if (!moveDrag.ghost) {
      moveDrag.ghost = document.createElement('div');
      moveDrag.ghost.className = 'region edit move-ghost';
      moveDrag.pe.overlay.appendChild(moveDrag.ghost);
    }
    const css = pdfRectToCss(moveDrag.pageNum, moveDrag.pe.img, moveDrag.span);
    moveDrag.ghost.style.cssText =
      `left:${css.left + dx}px;top:${css.top + dy}px;width:${css.width}px;height:${css.height}px;`;
    return;
  }
  if (inkDrag) {
    const rect = inkDrag.pe.overlay.getBoundingClientRect();
    inkDrag.points.push({ x: e.clientX - rect.left, y: e.clientY - rect.top });
    inkDrag.poly.setAttribute('points', inkDrag.points.map((pt) => `${pt.x},${pt.y}`).join(' '));
    return;
  }
  if (!drag) return;
  const rect = drag.pe.overlay.getBoundingClientRect();
  const x1 = e.clientX - rect.left;
  const y1 = e.clientY - rect.top;
  if (!drag.div) {
    const regionClass = REGION_CLASS_BY_TOOL[state.tool] ?? 'edit';
    drag.div = document.createElement('div');
    drag.div.className = `region ${regionClass}`;
    drag.pe.overlay.appendChild(drag.div);
  }
  const left = Math.min(drag.x0, x1);
  const top = Math.min(drag.y0, y1);
  drag.div.style.cssText =
    `left:${left}px;top:${top}px;width:${Math.abs(x1 - drag.x0)}px;height:${Math.abs(y1 - drag.y0)}px;`;
});

pagesEl.addEventListener('pointerup', async (e) => {
  if (moveDrag) {
    const r = moveDrag.pe.overlay.getBoundingClientRect();
    const endPdf = cssToPdf(moveDrag.pageNum, moveDrag.pe.img, e.clientX - r.left, e.clientY - r.top);
    const dx = endPdf.x - moveDrag.startPdf.x;
    const dy = endPdf.y - moveDrag.startPdf.y;
    if (moveDrag.ghost) moveDrag.ghost.remove();
    const md = moveDrag;
    moveDrag = null;
    if (Math.abs(dx) >= 1 || Math.abs(dy) >= 1) {
      if (md.span) await applyMoveText(md.span, md.pageNum, dx, dy);
      else await applyMoveImage(md.pageNum, md.startPdf, dx, dy);
    }
    return;
  }
  if (inkDrag) {
    if (inkDrag.points.length > 0) {
      const list = inkByPage.get(inkDrag.pageNum) ?? [];
      list.push(inkDrag.points);
      inkByPage.set(inkDrag.pageNum, list);
    }
    inkDrag = null;
    return;
  }
  if (!drag) return;
  const { pe, pageNum, x0, y0, div } = drag;
  const rect = pe.overlay.getBoundingClientRect();
  const x1 = e.clientX - rect.left;
  const y1 = e.clientY - rect.top;
  drag = null;
  if (div) div.remove();

  const tiny = Math.abs(x1 - x0) < 4 || Math.abs(y1 - y0) < 4;

  // The text tool accepts a plain click: drop a default-sized text box at the point.
  if (state.tool === 'text' && tiny) {
    const at = cssToPdf(pageNum, pe.img, x0, y0);
    const h = 26;
    beginAddText({ page: pageNum, x: at.x, y: at.y - h, width: 240, height: h });
    return;
  }
  // Highlight normally never reaches here: over a page carrying a text layer the sweep is a real
  // selection (see highlightSelection). This is the fallback for a page with no selectable text
  // — a scan — where there is nothing to select, so a box is all the user can express.
  if (state.tool === 'highlight') {
    if (Math.abs(x1 - x0) < 5 && Math.abs(y1 - y0) < 5) return; // ignore a click
    // min/max in PDF space, not screen space, so the box is not negative-sized on a rotated page
    // (same fix as the main region below).
    const h1 = cssToPdf(pageNum, pe.img, x0, y0);
    const h2 = cssToPdf(pageNum, pe.img, x1, y1);
    applyHighlight({
      page: pageNum, x: Math.min(h1.x, h2.x), y: Math.min(h1.y, h2.y),
      width: Math.abs(h2.x - h1.x), height: Math.max(Math.abs(h2.y - h1.y), 1),
    }, { snap: state.highlightMode !== 'box' });
    return;
  }
  if (tiny) return;

  // Map both dragged corners to PDF space, then take min/max there. Picking the "bottom-left" and
  // "top-right" in *screen* space only lines up with PDF min/max on an unrotated page: on a
  // /Rotate 90/270 page cssToPdf swaps the axes, so screen-min maps to PDF-max and the region
  // came out with a negative width/height. A negative-width rect still draws (the black box
  // appeared) but fails the redactor's glyph-intersection test (left > right matches nothing), so
  // the text under the box was never removed — recoverable under an opaque rectangle (#rotate-90).
  const c1 = cssToPdf(pageNum, pe.img, x0, y0);
  const c2 = cssToPdf(pageNum, pe.img, x1, y1);
  const region = {
    page: pageNum,
    x: Math.min(c1.x, c2.x), y: Math.min(c1.y, c2.y),
    width: Math.abs(c2.x - c1.x), height: Math.abs(c2.y - c1.y),
  };

  if (state.tool === 'redact') {
    markRegion(region);
    drawRegions();
  } else if (state.tool === 'field') {
    placeField(region);
  } else if (state.tool === 'text') {
    beginAddText(region);
  } else if (state.tool === 'edit') {
    state.pendingEditRegion = region;
    drawRegions();
    await beginTextEdit(region);
  } else if (state.tool === 'sign') {
    state.pendingSignRegion = region;
    drawRegions();
    showPanel('panel-sign');
  }
});

// Right-click a word (in select mode) to edit that run of text in place.
// ---------------------------------------------------------- right-click menu

const URL_IN_TEXT = /\bhttps?:\/\/[^\s)]+/i;

/** Region (PDF space) of the current text selection, or null if it isn't over a page. */
function selectionRegion() {
  const sel = window.getSelection();
  if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return null;
  const rect = sel.getRangeAt(0).getBoundingClientRect();
  if (rect.width === 0 && rect.height === 0) return null;
  const cx = rect.left + rect.width / 2;
  const cy = rect.top + rect.height / 2;
  for (const pe of pageEls) {
    const b = pe.img.getBoundingClientRect();
    if (cx < b.left || cx > b.right || cy < b.top || cy > b.bottom) continue;
    const pageNum = Number(pe.wrap.dataset.page);
    const a = cssToPdf(pageNum, pe.img, rect.left - b.left, rect.bottom - b.top);
    const c = cssToPdf(pageNum, pe.img, rect.right - b.left, rect.top - b.top);
    const pad = 1.5;
    return {
      page: pageNum,
      x: Math.min(a.x, c.x) - pad, y: Math.min(a.y, c.y) - pad,
      width: Math.abs(c.x - a.x) + 2 * pad, height: Math.abs(c.y - a.y) + 2 * pad,
    };
  }
  return null;
}

/** The text run region under a right-clicked span (fallback when nothing is selected). */
function spanRegion(target) {
  const span = target.closest?.('.text-layer span');
  if (!span?.dataset.region) return null;
  const r = JSON.parse(span.dataset.region);
  const pad = 1;
  return { page: r.page, x: r.x - pad, y: r.y - pad, width: r.width + 2 * pad, height: r.height + 2 * pad };
}

function ctxItem(label, action, disabled = false) { return { label, action, disabled }; }
const CTX_SEP = { sep: true };

/** Builds the context-sensitive menu items for a right-click. */
function buildContextItems(e) {
  const items = [];
  const selText = (window.getSelection()?.toString() ?? '').trim();
  const region = selText ? selectionRegion() : spanRegion(e.target);
  const runText = !selText && region ? null : selText;
  // Measured now, while the selection is still live: clicking an item in the menu moves focus and
  // can collapse it, so reading the selection inside the action would find nothing to highlight.
  const sweptRects = selText ? selectionHighlightRects() : null;

  if (region) {
    // Text context: act on the selected text / clicked run.
    items.push(ctxItem('✏ Edit text', () => { setTool('select'); beginTextEdit(region); }));
    items.push(ctxItem('⬛ Redact this', () => { markRegion(region); setTool('redact'); drawRegions(); toast('Marked for redaction — review and Apply.'); }));
    // Highlighting a selection marks the characters selected, exactly as the sweep tool does.
    // Going through applyHighlight(region) instead would paint selectionRegion()'s single bounding
    // rectangle — a block covering both lines and the gap between them for any selection spanning
    // a line break, which is the box-drawing #23 exists to get rid of. With nothing selected there
    // is only the run under the pointer to go on, so that whole run is still what gets marked.
    items.push(ctxItem('🖍 Highlight', () => (sweptRects?.size
      ? applyHighlightRects(sweptRects)
      : applyHighlight(region))));
    if (runText) items.push(ctxItem('📋 Copy', () => navigator.clipboard?.writeText(runText).catch(() => {})));
    const url = selText.match(URL_IN_TEXT)?.[0];
    if (url) { items.push(CTX_SEP); items.push(ctxItem('🔗 Open link', () => window.open(url, '_blank', 'noreferrer'))); }
    return items;
  }

  // Nothing selected: document-level actions.
  items.push(ctxItem('🔎 Make searchable (OCR)', runOcr));
  items.push(ctxItem('⚙ Show source code', showSourceCode));
  items.push(CTX_SEP);
  items.push(ctxItem('💾 Save', save));
  items.push(ctxItem('🖨 Print', printDocument));
  items.push(CTX_SEP);
  items.push(ctxItem('🔍 Zoom in', () => setZoom(state.zoom + 0.25)));
  items.push(ctxItem('🔎 Zoom out', () => setZoom(state.zoom - 0.25)));
  items.push(CTX_SEP);
  items.push(ctxItem('↩ Undo', undo, state.history.length === 0));
  items.push(ctxItem('↪ Redo', redo, state.future.length === 0));
  return items;
}

/** Shows detected JavaScript source (the safety dialog), or the document-script editor. */
function showSourceCode() {
  if (state.safety?.javaScriptCount > 0) showSafetyDialog();
  else openJavaScript();
}

function showContextMenu(e) {
  if (!state.pdf) return;
  const items = buildContextItems(e);
  if (items.length === 0) return;
  e.preventDefault();
  const menu = $('context-menu');
  menu.innerHTML = '';
  for (const it of items) {
    if (it.sep) { const s = document.createElement('div'); s.className = 'ctx-sep'; menu.appendChild(s); continue; }
    const b = document.createElement('button');
    b.className = 'ctx-item';
    b.textContent = it.label;
    b.disabled = !!it.disabled;
    if (!it.disabled) b.addEventListener('click', () => { hideContextMenu(); it.action(); });
    menu.appendChild(b);
  }
  menu.hidden = false;
  // Keep the menu on-screen.
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  const x = Math.min(e.clientX, window.innerWidth - mw - 6);
  const y = Math.min(e.clientY, window.innerHeight - mh - 6);
  menu.style.left = `${Math.max(4, x)}px`;
  menu.style.top = `${Math.max(4, y)}px`;
}

function hideContextMenu() { $('context-menu').hidden = true; }

scrollArea.addEventListener('contextmenu', showContextMenu);
document.addEventListener('click', hideContextMenu);
document.addEventListener('scroll', () => { hideContextMenu(); hideLinkPopup(); }, true);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideContextMenu(); });

// ----------------------------------------------------------------- panels

function showPanel(id) {
  $('panel').hidden = false;
  for (const section of document.querySelectorAll('.panel-section')) {
    section.hidden = section.id !== id;
  }
}

function hidePanels() {
  $('panel').hidden = true;
  state.pendingEditRegion = null;
  state.pendingSignRegion = null;
  drawRegions();
}

function setTool(tool) {
  if (state.tool !== tool) activity.add('info', 'tool changed', `${state.tool} → ${tool}`);
  if (state.tool === 'draw' && tool !== 'draw') clearDrawing(); // drop uncommitted strokes
  state.tool = tool;
  for (const button of document.querySelectorAll('.tool')) button.classList.remove('active');
  $(`tool-${tool}`).classList.add('active');
  // Surface on the Edit menu trigger that an editing tool is active (select lives outside the menu).
  $('menu-edit-trigger').classList.toggle('has-active', tool !== 'select');
  for (const pe of pageEls) pe.overlay.classList.toggle('tool-active', tool !== 'select');
  // In select mode the text layer is interactive (select/copy); tools capture the overlay instead.
  pagesEl.classList.toggle('select-mode', tool === 'select');
  // Highlight sweeps text rather than drawing a box (#23), so it too needs the text layer live.
  applyHighlightMode();
  pagesEl.classList.toggle('move-mode', tool === 'move'); // a "move" cursor over the page
  if (tool === 'redact') showPanel('panel-redact');
  else if (tool === 'draw') showPanel('panel-draw');
  else if (tool === 'highlight') showPanel('panel-highlight');
  else hidePanels(); // select/text/edit/sign: panel appears once a box is drawn
}

// ---------------------------------------------------------------- dialogs

/** Small form dialog; resolves with {fieldId: value} or null when cancelled. */
function promptDialog(title, fields, confirmLabel = 'OK') {
  return new Promise((resolve) => {
    // The heading is built as a node, not interpolated HTML: every current caller passes a
    // hardcoded title, but nothing in the signature says so, and the rule in
    // scripts/check-innerhtml.mjs is only enforceable with no exemptions (#74).
    modal.innerHTML = '';
    const heading = document.createElement('h2');
    heading.textContent = title;
    modal.appendChild(heading);
    const inputs = {};
    for (const field of fields) {
      const label = document.createElement('label');
      label.textContent = field.label;
      let input;
      if (field.type === 'select') {
        // Options are {value, label} pairs; the option text is set via textContent (never HTML).
        input = document.createElement('select');
        for (const opt of field.options ?? []) {
          const option = document.createElement('option');
          option.value = opt.value;
          option.textContent = opt.label;
          input.appendChild(option);
        }
        if (field.value != null) input.value = field.value;
      } else {
        input = document.createElement('input');
        input.type = field.type ?? 'text';
        input.value = field.value ?? '';
        if (field.placeholder) input.placeholder = field.placeholder;
      }
      label.appendChild(input);
      modal.appendChild(label);
      inputs[field.id] = input;
    }
    const actions = document.createElement('div');
    actions.className = 'actions';
    const ok = document.createElement('button');
    ok.textContent = confirmLabel;
    ok.className = 'danger';
    const cancel = document.createElement('button');
    cancel.textContent = 'Cancel';
    actions.append(ok, cancel);
    modal.appendChild(actions);

    const done = (value) => { modal.close(); resolve(value); };
    ok.addEventListener('click', () =>
      done(Object.fromEntries(Object.entries(inputs).map(([k, i]) => [k, i.value]))));
    cancel.addEventListener('click', () => done(null));
    modal.showModal();
    Object.values(inputs)[0]?.focus();
  });
}

// ------------------------------------------------------------- redaction

/** Finds every occurrence of a phrase and marks each as a redaction box. */
async function searchAndMarkRedactions() {
  const phrase = $('redact-search-text').value.trim();
  if (!phrase) { toast('Enter some text to search for.'); return; }
  if (!state.pdf) return;
  try {
    setStatus(`Searching for “${phrase}”…`, true);
    const result = await host.call('find-text', {
      pdf: state.pdfB64, phrase, pdfPassword: state.password,
      matchMode: $('redact-match-mode')?.value ?? 'contains',
      caseSensitive: $('redact-case')?.checked ?? false,
    });
    const matches = result.matches ?? [];
    setStatus('');
    if (matches.length === 0) { toast(`No matches for “${phrase}”.`); return; }
    // Pad each match slightly so the box fully covers the glyphs' edges.
    const pad = 1;
    for (const m of matches) {
      markRegion({
        page: m.page,
        x: m.x - pad, y: m.y - pad,
        width: m.width + 2 * pad, height: m.height + 2 * pad,
      });
    }
    $('redact-search-text').value = '';
    drawRegions();
    toast(`Marked ${matches.length} match${matches.length === 1 ? '' : 'es'} of “${phrase}” — review, then Preview or Apply.`);
  } catch (e) {
    fail(e);
  }
}

async function previewRedaction() {
  try {
    setStatus('Building redaction preview…', true);
    const result = await host.call('redact', {
      pdf: state.pdfB64, regions: state.regions, pdfPassword: state.password,
      intensity: Number($('redact-intensity')?.value ?? 0),
    });
    const pages = [...new Set(state.regions.map((r) => r.page))].sort((a, b) => a - b);
    const images = [];
    for (const page of pages) {
      const rendered = await host.call('render', {
        pdf: result.pdf, page, dpi: 110, pdfPassword: state.password,
      });
      images.push({ page, png: rendered.png });
    }
    setStatus('');

    modal.innerHTML = '<h2>Redaction preview</h2>' +
      '<p class="muted">This is how the affected pages will look. Applying removes the ' +
      'content behind each box permanently — this cannot be undone after saving.</p>';
    const container = document.createElement('div');
    container.className = 'preview-pages';
    for (const image of images) {
      const caption = document.createElement('p');
      caption.className = 'muted';
      caption.textContent = `Page ${image.page}`;
      const img = document.createElement('img');
      img.src = `data:image/png;base64,${image.png}`;
      container.append(caption, img);
    }
    modal.appendChild(container);
    const actions = document.createElement('div');
    actions.className = 'actions';
    const apply = document.createElement('button');
    apply.className = 'danger';
    apply.textContent = 'Apply redaction';
    const close = document.createElement('button');
    close.textContent = 'Close preview';
    actions.append(apply, close);
    modal.appendChild(actions);
    modal.showModal();

    apply.addEventListener('click', async () => {
      modal.close();
      await applyRedaction(result);
    });
    close.addEventListener('click', () => modal.close());
  } catch (e) {
    fail(e);
  }
}

async function applyRedaction(precomputed) {
  try {
    setStatus('Applying redaction…', true);
    const result = precomputed ?? await host.call('redact', {
      pdf: state.pdfB64, regions: state.regions, pdfPassword: state.password,
      intensity: Number($('redact-intensity')?.value ?? 0),
    });
    const count = state.regions.length;
    state.regions = [];
    await applyResult(result.pdf,
      `Redacted ${count} region${count === 1 ? '' : 's'} — content removed.` +
      (result.warnings?.length ? ` ⚠ ${result.warnings.join(' ')}` : ''));
    // #48: an auditable summary of what was removed, and what redaction leaves behind.
    if (result.report) showRedactionReport(result);
  } catch (e) {
    fail(e);
  }
}

/** #48: renders the redaction audit — per-page counts by category, plus what survives redaction. */
function showRedactionReport(result) {
  const report = result.report;
  modal.innerHTML = '<h2>Redaction report</h2>';
  modal.appendChild(reportSummaryEl(report));
  if (report.pages.length) modal.appendChild(reportTableEl(report));
  const removed = reportRemovedEl(report);
  if (removed) modal.appendChild(removed);
  for (const note of reportResidualNotes(report.residual)) modal.appendChild(note);

  const actions = document.createElement('div');
  actions.className = 'actions';
  const download = document.createElement('button');
  download.className = 'danger';
  download.textContent = '⬇ Download compliance report';
  download.addEventListener('click', () => downloadComplianceReport(result));
  const close = document.createElement('button');
  close.textContent = 'Close';
  actions.append(download, close);
  modal.appendChild(actions);
  modal.showModal();
  close.addEventListener('click', () => modal.close());
  close.focus();
}

function reportSummaryEl(report) {
  const summary = document.createElement('p');
  summary.className = 'muted';
  const rs = report.regions === 1 ? '' : 's';
  const ps = report.pagesAffected === 1 ? '' : 's';
  summary.textContent = `Removed content from ${report.regions} region${rs} `
    + `across ${report.pagesAffected} page${ps}.`;
  return summary;
}

function reportTableEl(report) {
  const table = document.createElement('table');
  table.className = 'report-table';
  const head = document.createElement('tr');
  for (const h of ['Page', 'Regions', 'Text runs', 'Images', 'Annotations']) {
    const th = document.createElement('th');
    th.textContent = h;
    head.appendChild(th);
  }
  table.appendChild(head);
  const addRow = (cells, isTotal) => {
    const tr = document.createElement('tr');
    if (isTotal) tr.className = 'report-total';
    cells.forEach((c, i) => {
      const cell = document.createElement(isTotal || i === 0 ? 'th' : 'td');
      cell.textContent = String(c);
      tr.appendChild(cell);
    });
    table.appendChild(tr);
  };
  for (const p of report.pages) addRow([p.page, p.regions, p.textRuns, p.images, p.annotations], false);
  const t = report.totals;
  addRow(['Total', report.regions, t.textRuns, t.images, t.annotations], true);
  return table;
}

/** A preview of the removed text per region (full text + thumbnails live in the download). Null if none. */
function reportRemovedEl(report) {
  const withText = (report.regionDetails ?? []).filter((r) => r.removed.text.trim() || r.removed.images > 0);
  if (!withText.length) return null;
  const frag = document.createDocumentFragment();
  const h = document.createElement('h3');
  h.textContent = 'Removed content';
  h.className = 'report-subhead';
  frag.appendChild(h);
  for (const r of withText.slice(0, 8)) frag.appendChild(reportRemovedLineEl(r));
  return frag;
}

function reportRemovedLineEl(r) {
  const p = document.createElement('p');
  p.className = 'report-removed';
  const where = document.createElement('span');
  where.className = 'report-removed-where';
  where.textContent = `p${r.page}: `;
  p.appendChild(where);
  const snippet = r.removed.text.replace(/\s+/g, ' ').trim();
  p.append(snippet.length > 140 ? snippet.slice(0, 140) + '…' : snippet);
  if (r.removed.images > 0) {
    const im = document.createElement('em');
    const s = r.removed.images === 1 ? '' : 's';
    im.textContent = `${snippet ? ' ' : ''}(${r.removed.images} image${s})`;
    p.appendChild(im);
  }
  return p;
}

/**
 * Redaction removes page content, but not document-level JavaScript or metadata — surface those so
 * an auditor knows to run "Remove hidden info" too. Returns a <p> element per note.
 */
function reportResidualNotes(res) {
  const notes = [];
  if (res.remainingJavaScript > 0) {
    const s = res.remainingJavaScript === 1 ? '' : 's';
    notes.push(`⚠ ${res.remainingJavaScript} embedded script${s} still present — `
      + 'redaction does not remove JavaScript. Use “Remove hidden info…”.');
  }
  if (res.remainingMetadata) {
    notes.push('⚠ Document metadata is still present — use “Remove hidden info…” to strip it.');
  }
  return notes.map((text) => {
    const p = document.createElement('p');
    p.className = 'report-note';
    p.textContent = text;
    return p;
  });
}

/** Escapes text for safe inclusion in the generated (downloaded) HTML report. */
function escapeHtml(s) {
  return String(s ?? '')
    .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;').replaceAll("'", '&#39;');
}

/** One region's HTML block for the downloaded report. Built by concatenation to avoid nesting templates. */
function regionBlockHtml(r) {
  const coords = `x ${r.x.toFixed(1)}, y ${r.y.toFixed(1)}, ${r.width.toFixed(1)}×${r.height.toFixed(1)} pt`;
  const text = r.removed.text.trim()
    ? `<pre class="rtext">${escapeHtml(r.removed.text)}</pre>`
    : '<p class="muted">No extractable text.</p>';
  const imgTags = (r.removed.imageThumbnails ?? [])
    .map((b64) => `<img class="rimg" alt="Removed image" src="data:image/png;base64,${b64}" />`).join('');
  const imgsBlock = imgTags ? `<div class="rimgs">${imgTags}</div>` : '';
  const counts = `${r.removed.textRuns} text run(s), ${r.removed.images} image(s), `
    + `${r.removed.annotations} annotation(s) removed.`;
  return `<div class="region"><h4>Page ${r.page} · region (${escapeHtml(coords)})</h4>`
    + `<p class="muted">${counts}</p>${text}${imgsBlock}</div>`;
}

/**
 * Builds a self-contained HTML compliance report — document identity, integrity hashes, per-page
 * and per-region detail, the exact text removed and thumbnails of the images removed, and residual
 * risk — and downloads it. Kept as one printable file so it can be archived or printed to PDF.
 */
function downloadComplianceReport(result) {
  const report = result.report;
  const version = chrome.runtime.getManifest().version;
  const generated = result.generatedUtc || new Date().toISOString();

  const pageRows = report.pages.map((p) =>
    `<tr><td>${p.page}</td><td>${p.regions}</td><td>${p.textRuns}</td><td>${p.images}</td><td>${p.annotations}</td></tr>`).join('');

  const regionBlocks = (report.regionDetails ?? []).map(regionBlockHtml).join('');

  const res = report.residual;
  const residual = [];
  if (res.remainingJavaScript > 0) residual.push(`${res.remainingJavaScript} embedded script(s) still present.`);
  if (res.remainingMetadata) residual.push('Identifying document metadata still present.');
  const residualItems = residual.map((r) => `<li>${escapeHtml(r)}</li>`).join('');
  const residualHtml = residual.length
    ? `<ul>${residualItems}</ul>`
    : '<p>None detected. (Redaction does not remove JavaScript or metadata; none were found.)</p>';

  const t = report.totals;
  const html = `<!doctype html><html lang="en"><head><meta charset="utf-8" />
<title>Redaction Compliance Report — ${escapeHtml(state.fileName)}</title>
<style>
  body { font: 14px/1.5 system-ui, sans-serif; color: #1a1a1a; max-width: 820px; margin: 32px auto; padding: 0 20px; }
  h1 { font-size: 22px; margin-bottom: 4px; } h2 { font-size: 16px; margin-top: 28px; border-bottom: 1px solid #ccc; padding-bottom: 4px; }
  table { border-collapse: collapse; width: 100%; margin: 8px 0; } th, td { border: 1px solid #ccc; padding: 4px 10px; text-align: right; } th:first-child, td:first-child { text-align: left; }
  .meta td { text-align: left; } .muted { color: #666; }
  .region { border: 1px solid #e0e0e0; border-radius: 6px; padding: 10px 14px; margin: 10px 0; }
  .region h4 { margin: 0 0 4px; font-size: 14px; }
  .rtext { white-space: pre-wrap; background: #f6f6f6; padding: 8px; border-radius: 4px; font: 12.5px ui-monospace, monospace; }
  .rimgs { display: flex; flex-wrap: wrap; gap: 8px; } .rimg { max-width: 220px; border: 1px solid #ccc; }
  code { font: 12px ui-monospace, monospace; word-break: break-all; }
</style></head><body>
<h1>Redaction Compliance Report</h1>
<p class="muted">Generated ${escapeHtml(generated)} by reDACT v${escapeHtml(version)}.</p>
<h2>Document</h2>
<table class="meta">
  <tr><td>File</td><td>${escapeHtml(state.fileName)}</td></tr>
  <tr><td>Original SHA-256</td><td><code>${escapeHtml(result.originalSha256 || 'n/a')}</code></td></tr>
  <tr><td>Redacted SHA-256</td><td><code>${escapeHtml(result.outputSha256 || 'n/a')}</code></td></tr>
</table>
<h2>Summary</h2>
<p>Removed content from <strong>${report.regions}</strong> region(s) across <strong>${report.pagesAffected}</strong> page(s): ${t.textRuns} text run(s), ${t.images} image(s), ${t.annotations} annotation(s).</p>
<table><tr><th>Page</th><th>Regions</th><th>Text runs</th><th>Images</th><th>Annotations</th></tr>${pageRows}
<tr><th>Total</th><th>${report.regions}</th><th>${t.textRuns}</th><th>${t.images}</th><th>${t.annotations}</th></tr></table>
<h2>Removed content by region</h2>
${regionBlocks || '<p class="muted">No itemised content.</p>'}
<h2>Residual risk</h2>
${residualHtml}
</body></html>`;

  const blob = new Blob([html], { type: 'text/html' });
  const suggested = state.fileName.replace(/\.pdf$/i, '') + '-redaction-report.html';
  chrome.downloads.download({ url: URL.createObjectURL(blob), filename: suggested, saveAs: true });
  activity.add('info', 'redaction report downloaded', suggested);
  toast('Compliance report downloaded.');
}

// ------------------------------------------------------------- text edit

function setStyleToggle(id, on) {
  $(id).classList.toggle('active', !!on);
}

async function beginTextEdit(region) {
  try {
    setStatus('Reading text in region…', true);
    const found = await host.call('get-region-text', {
      pdf: state.pdfB64, region, pdfPassword: state.password,
    });
    setStatus('');
    state.textMode = 'edit';
    // Own the region here rather than leaving each call site to set it, the way beginAddText does.
    // The context-menu entry set it and then called setTool('select'), whose hidePanels() promptly
    // nulled it again, so Apply found no region and returned in silence (#85).
    state.pendingEditRegion = region;
    $('edit-title').textContent = 'Edit text';
    $('edit-hint').textContent = 'Text found in the selected region:';
    $('edit-text').value = found.text;
    $('edit-size').value = Number(found.fontSize).toFixed(1);
    // Pre-fill the font controls with what was detected in the region.
    $('edit-font').value = ['helvetica', 'times', 'courier'].includes(found.fontFamily)
      ? found.fontFamily : 'helvetica';
    setStyleToggle('edit-bold', found.bold);
    setStyleToggle('edit-italic', found.italic);
    $('edit-color').value = '#000000';
    // Record what the size/face controls were matched to. The user sees the pre-filled controls;
    // the console says what they were derived from, which is the only place the original font name
    // appears at all (the picker can only offer the three families the editor can stamp).
    activity.add('info', 'matched the existing text style', describeMatchedFont(found));
    showPanel('panel-edit');
    $('edit-text').focus();
  } catch (e) {
    fail(e);
  }
}

/** "times 11.0pt bold (from ABCDEF+MinionPro-Bold)" — for the activity console. */
function describeMatchedFont(found) {
  const size = Number(found.fontSize);
  let text = `${found.fontFamily} ${Number.isFinite(size) ? size.toFixed(1) : '?'}pt`;
  if (found.bold) text += ' bold';
  if (found.italic) text += ' italic';
  if (found.sourceFont && found.sourceFont !== found.fontFamily) text += ` (from ${found.sourceFont})`;
  return text;
}

/** Opens the text panel in "add" mode for stamping brand-new text into a region. */
function beginAddText(region) {
  state.textMode = 'add';
  state.pendingEditRegion = region;
  $('edit-title').textContent = 'Add text';
  $('edit-hint').textContent = 'Type the text to place on the page:';
  $('edit-text').value = '';
  // Default the size to roughly the box height so a dragged box sets the type size.
  $('edit-size').value = Math.max(8, Math.min(72, Math.round(region.height))).toFixed(1);
  $('edit-font').value = 'helvetica';
  setStyleToggle('edit-bold', false);
  setStyleToggle('edit-italic', false);
  $('edit-color').value = '#000000';
  showPanel('panel-edit');
  $('edit-text').focus();
}

async function applyTextEdit() {
  const region = state.pendingEditRegion;
  if (!region) {
    // Bailing here used to be completely silent, which is how #85 stayed invisible: the panel was
    // open, Apply looked pressed, and nothing happened or was reported anywhere.
    activity.add('error', 'edit text', 'no region is selected — nothing to apply');
    toast('Select the text to edit first.');
    return;
  }
  const adding = state.textMode === 'add';
  if (adding && !$('edit-text').value.trim()) { toast('Type some text first.'); return; }
  try {
    setStatus(adding ? 'Adding text…' : 'Replacing text…', true);
    const result = await host.call(adding ? 'add-text' : 'replace-region-text', {
      pdf: state.pdfB64,
      region,
      text: $('edit-text').value,
      fontSize: Number.parseFloat($('edit-size').value) || undefined,
      fontFamily: $('edit-font').value,
      bold: $('edit-bold').classList.contains('active'),
      italic: $('edit-italic').classList.contains('active'),
      color: $('edit-color').value,
      pdfPassword: state.password,
    });
    hidePanels();
    if (adding) setTool('text');
    // Warnings from an edit used to be dropped on the floor. A font substitution changes how the
    // document looks, so it is reported rather than left for the user to notice later (#29, #72).
    const warnings = result.warnings ?? [];
    for (const warning of warnings) {
      activity.add('warn', adding ? 'text added with a warning' : 'text replaced with a warning', warning);
    }
    const substituted = warnings.some((w) => /substitut/i.test(w));
    let message = adding ? 'Text added.' : 'Text replaced.';
    if (substituted) message += ' The original font could not be reused — see Help ▸ Activity console.';
    await applyContentEdit(result.pdf, [region.page], message);
  } catch (e) {
    fail(e);
  }
}

// --------------------------------------------------------------- move text

/** Moves a run of existing text by (dx, dy) in PDF space (grabbed with the Move tool). */
async function applyMoveText(span, pageNum, dx, dy) {
  const pad = 1; // capture the whole run comfortably
  const region = {
    page: pageNum, x: span.x - pad, y: span.y - pad,
    width: span.width + 2 * pad, height: span.height + 2 * pad,
  };
  try {
    setStatus('Moving text…', true);
    const result = await host.call('move-text', {
      pdf: state.pdfB64, region, dx, dy, pdfPassword: state.password,
    });
    await applyContentEdit(result.pdf, [pageNum], 'Text moved.');
  } catch (e) {
    fail(e);
  }
}

/** Moves the image under the grab point by (dx, dy). No-ops (with a hint) if none is there. */
async function applyMoveImage(pageNum, startPdf, dx, dy) {
  const region = { page: pageNum, x: startPdf.x - 2, y: startPdf.y - 2, width: 4, height: 4 };
  try {
    setStatus('Moving image…', true);
    const result = await host.call('move-image', {
      pdf: state.pdfB64, region, dx, dy, pdfPassword: state.password,
    });
    if (result.pdf === state.pdfB64) { setStatus(''); toast('Nothing to move there — grab a word or an image.'); return; }
    await applyContentEdit(result.pdf, [pageNum], 'Image moved.');
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------------- highlighter

function rectsIntersect(a, b) {
  return a.x < b.x + b.width && b.x < a.x + a.width &&
         a.y < b.y + b.height && b.y < a.y + a.height;
}

/** Highlights the text runs a dragged box covers (or the box itself if the page has no text). */
async function applyHighlight(region, { snap = true } = {}) {
  // An explicitly chosen box marks the rectangle drawn, full stop. Snapping is a helpful guess when
  // the box is only a way of pointing at words; when the user has asked for a box it would quietly
  // widen the mark to whole runs — the very behaviour the box mode exists as an alternative to.
  if (!snap) return highlightRects(region.page, [
    { x: region.x, y: region.y, width: region.width, height: region.height }]);
  // Use the cached text runs; if the text layer hasn't built yet, fetch this page's runs now so a
  // highlight drawn immediately still snaps to the words instead of colouring the whole box.
  let spans = spanCache.get(`${state.version}|${region.page}`);
  if (!spans) {
    try {
      const r = await host.call('page-text', {
        pdf: state.pdfB64, page: region.page, pdfPassword: state.password,
      });
      spans = r.spans ?? [];
      spanCache.set(`${state.version}|${region.page}`, spans);
    } catch (e) {
      // Falls back to highlighting the whole dragged box instead of snapping to words — a
      // visibly different result, so record why (#72).
      activity.add('warn', 'highlight could not snap to words',
        `${e?.message ?? e} — highlighting the whole selected box instead`);
      spans = [];
    }
  }
  const covered = spans.filter((s) => rectsIntersect(s, region))
    .map((s) => ({ x: s.x, y: s.y, width: s.width, height: s.height }));
  const rects = covered.length > 0
    ? covered
    : [{ x: region.x, y: region.y, width: region.width, height: region.height }];
  return highlightRects(region.page, rects);
}

/** Stamps rects onto one page in the current highlight colour. */
async function highlightRects(page, rects) {
  try {
    setStatus('Highlighting…', true);
    const result = await host.call('add-highlight', {
      pdf: state.pdfB64, page, rects,
      color: state.highlightColor, pdfPassword: state.password,
    });
    await applyContentEdit(result.pdf, [page],
      `Highlighted ${rects.length} ${rects.length === 1 ? 'run' : 'runs'}.`);
  } catch (e) {
    fail(e);
  }
}

/**
 * Puts the pages into sweep mode or leaves them to the overlay's box drag, and says which is which
 * in the panel. Only sweep mode hands the pointer to the text layer, so switching to a box is what
 * makes the rectangle drag reachable again over text — otherwise the two would fight for the
 * pointer and which one won would depend on where the press happened to land.
 */
function applyHighlightMode() {
  const sweeping = state.tool === 'highlight' && state.highlightMode === 'sweep';
  pagesEl.classList.toggle('highlight-mode', sweeping);
  const hint = $('highlight-mode-hint');
  if (hint) {
    hint.textContent = state.highlightMode === 'sweep'
      ? 'Pages with no selectable text — scans — fall back to a box.'
      : 'Marks the whole rectangle, including any blank space in it.';
  }
}

// ------------------------------------------------- sweep-to-highlight (#23)
//
// Highlighting is a text selection, not a box: press, sweep across the words, release. The
// selectable text layer that already sits over each page does the selecting (the browser knows
// where characters begin and end), and the resulting selection is mapped back to PDF space.
// A box could only ever mark whole runs plus the whitespace around them, which is the complaint
// in #23. Pages with no text layer (scans) keep the box drag — there is nothing to select there.

/** Overlap of two PDF-space rects, or null when they don't overlap meaningfully. */
function intersectRect(a, b) {
  const x = Math.max(a.x, b.x);
  const y = Math.max(a.y, b.y);
  const right = Math.min(a.x + a.width, b.x + b.width);
  const top = Math.min(a.y + a.height, b.y + b.height);
  if (right - x <= 0.05 || top - y <= 0.05) return null;
  return { x, y, width: right - x, height: top - y };
}

/** The part of `range` that falls inside `el`, or null if it covers none of it. */
function rangeWithin(range, el) {
  const part = document.createRange();
  part.selectNodeContents(el);
  // Clamping to a boundary that lies outside the element collapses the range, which is exactly
  // how "the selection does not reach this run" should read.
  if (part.compareBoundaryPoints(Range.START_TO_START, range) < 0) {
    part.setStart(range.startContainer, range.startOffset);
  }
  if (part.compareBoundaryPoints(Range.END_TO_END, range) > 0) {
    part.setEnd(range.endContainer, range.endOffset);
  }
  return part.collapsed ? null : part;
}

/**
 * PDF-space rects covering the current text selection, grouped by page number.
 * Nothing is written to the DOM in here, so the per-run measurements all read one already-clean
 * layout — no forced reflow per rect, which is what made link hotspots slow in #19.
 */
function selectionHighlightRects() {
  const byPage = new Map();
  const sel = window.getSelection();
  if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return byPage;
  const range = sel.getRangeAt(0);
  for (const pe of pageEls) {
    const layer = pe.wrap.querySelector('.text-layer');
    if (!layer || !range.intersectsNode(layer)) continue;
    const pageNum = Number(pe.wrap.dataset.page);
    const box = pe.img.getBoundingClientRect(); // once per page, not once per run
    const rects = [];
    for (const el of layer.children) {
      if (!el.dataset.region || !range.intersectsNode(el)) continue;
      const part = rangeWithin(range, el);
      if (!part) continue;
      const r = part.getBoundingClientRect();
      if (r.width <= 0 || r.height <= 0) continue;
      const a = cssToPdf(pageNum, pe.img, r.left - box.left, r.bottom - box.top);
      const b = cssToPdf(pageNum, pe.img, r.right - box.left, r.top - box.top);
      const swept = {
        x: Math.min(a.x, b.x), y: Math.min(a.y, b.y),
        width: Math.abs(b.x - a.x), height: Math.abs(b.y - a.y),
      };
      // Clip to the run's true PDF box. The swept box comes from the browser laying the run out
      // in a substitute font, so where it cuts *within* a run is a good approximation; the run's
      // own extent is exact, and clipping keeps the highlight off the neighbouring whitespace.
      const clipped = intersectRect(swept, JSON.parse(el.dataset.region));
      if (clipped) rects.push(clipped);
    }
    if (rects.length > 0) byPage.set(pageNum, rects);
  }
  return byPage;
}

/** Stamps the swept rects in, chaining page by page so a selection may cross a page break. */
async function applyHighlightRects(byPage) {
  const pages = [...byPage.keys()].sort((p, q) => p - q);
  try {
    setStatus('Highlighting…', true);
    let pdfB64 = state.pdfB64;
    let total = 0;
    for (const page of pages) {
      const rects = byPage.get(page);
      const result = await host.call('add-highlight', {
        pdf: pdfB64, page, rects, color: state.highlightColor, pdfPassword: state.password,
      });
      pdfB64 = result.pdf; // chain each page's rects onto the growing document
      total += rects.length;
    }
    const across = pages.length > 1 ? ` across ${pages.length} pages` : '';
    await applyContentEdit(pdfB64, pages,
      `Highlighted ${total} ${total === 1 ? 'run' : 'runs'}${across}.`);
  } catch (e) {
    fail(e);
  }
}

/**
 * End of a sweep: turn whatever is selected into a highlight. A plain click selects nothing and
 * so does nothing. Safe to call twice — the selection is cleared as soon as it is measured.
 */
async function highlightSelection() {
  const sel = window.getSelection();
  const anchor = sel?.anchorNode?.nodeType === Node.ELEMENT_NODE
    ? sel.anchorNode : sel?.anchorNode?.parentElement;
  if (!anchor?.closest('.text-layer')) return; // the sweep did not start over page text
  const byPage = selectionHighlightRects();
  if (byPage.size === 0) return;
  sel.removeAllRanges(); // measured already; drop it before the page re-renders under it
  await applyHighlightRects(byPage);
}

// --- driving the selection ---------------------------------------------------------------
// The browser's own drag-selection is not usable over this text layer: its runs are absolutely
// positioned boxes with gaps between them, so the moment the pointer leaves a run — past the end
// of a line, in the leading above it, between two lines — the hit test lands on the layer itself
// and Chrome collapses the selection. Measured: a sweep from x=60 to x=300 across a line ending
// at x=219 selected nothing at all. So the sweep is driven here instead: each endpoint snaps to
// the nearest run, which is also what makes a sloppy diagonal drag still mark the line under it.

/** The page (with selectable text) nearest a client Y — the one a sweep is over. */
function pageNearY(clientY) {
  let best = null;
  let bestDist = Infinity;
  for (const pe of pageEls) {
    if (!pe.wrap.querySelector('.text-layer')) continue;
    const b = pe.wrap.getBoundingClientRect();
    const d = Math.max(b.top - clientY, clientY - b.bottom, 0);
    if (d < bestDist) { bestDist = d; best = pe; }
  }
  return best;
}

/** Caret range at a client point, or null when the point isn't over a run of page text. */
function caretOnRun(clientX, clientY) {
  const range = document.caretRangeFromPoint?.(clientX, clientY);
  return range?.startContainer?.parentElement?.dataset?.region ? range : null;
}

/** Caret {node, offset} nearest a client point, snapped onto the closest run of text. */
function caretNear(clientX, clientY) {
  const direct = caretOnRun(clientX, clientY);
  if (direct) return { node: direct.startContainer, offset: direct.startOffset };

  const layer = pageNearY(clientY)?.wrap.querySelector('.text-layer');
  if (!layer) return null;
  let best = null;
  let bestScore = Infinity;
  for (const el of layer.children) {
    const r = el.getBoundingClientRect();
    const dy = Math.max(r.top - clientY, clientY - r.bottom, 0);
    const dx = Math.max(r.left - clientX, clientX - r.right, 0);
    const score = dy * 1000 + dx; // the run's own line first, then the nearest run along it
    if (score < bestScore) { bestScore = score; best = { el, r }; }
  }
  if (!best) return null;
  // Re-ask for the caret from a point that is definitely inside the chosen run.
  const inside = caretOnRun(
    Math.min(Math.max(clientX, best.r.left + 0.5), best.r.right - 0.5),
    (best.r.top + best.r.bottom) / 2);
  if (inside) return { node: inside.startContainer, offset: inside.startOffset };
  const node = best.el.firstChild;
  return node ? { node, offset: clientX > best.r.right ? node.length : 0 } : null;
}

let sweepDrag = null; // { node, offset } anchor of the sweep in progress

pagesEl.addEventListener('pointerdown', (e) => {
  if (state.tool !== 'highlight' || !state.pdf || e.button !== 0) return;
  if (state.highlightMode !== 'sweep') return;   // box mode: the overlay drag handles it
  if (e.target.closest('.overlay')) return; // no text layer on this page: box-drag fallback
  const anchor = caretNear(e.clientX, e.clientY);
  if (!anchor) return;
  sweepDrag = anchor;
  window.getSelection()?.removeAllRanges();
  e.preventDefault(); // stop the browser starting its own (collapsing) selection drag
  pagesEl.setPointerCapture(e.pointerId); // keep the sweep alive past the edge of the page
});

pagesEl.addEventListener('pointermove', (e) => {
  if (!sweepDrag) return;
  const focus = caretNear(e.clientX, e.clientY);
  if (focus) window.getSelection()?.setBaseAndExtent(sweepDrag.node, sweepDrag.offset, focus.node, focus.offset);
});

pagesEl.addEventListener('pointerup', () => {
  if (!sweepDrag) return;
  sweepDrag = null;
  highlightSelection();
});

pagesEl.addEventListener('pointercancel', () => {
  if (!sweepDrag) return; // gesture taken over (scroll/zoom): drop the sweep, stamp nothing
  sweepDrag = null;
  window.getSelection()?.removeAllRanges();
});

// Preventing the default on pointerdown suppresses the compatibility mouse events, so this fires
// only for selections made some other way (a double-click word grab, an accessibility tool).
document.addEventListener('mouseup', () => {
  if (state.tool === 'highlight' && state.pdf) highlightSelection();
});

// ----------------------------------------------------------- draw (ink) tool

/** Converts captured CSS strokes for a page to PDF-space strokes and stamps them in. */
async function applyDrawing() {
  const pages = [...inkByPage.entries()].filter(([, s]) => s.length > 0);
  if (pages.length === 0) { toast('Draw something first.'); return; }
  try {
    setStatus('Applying drawing…', true);
    let pdfB64 = state.pdfB64;
    let total = 0;
    for (const [pageNum, strokes] of pages) {
      const pe = pageEls[pageNum - 1];
      const pdfStrokes = strokes.map((stroke) =>
        stroke.map((pt) => {
          const p = cssToPdf(pageNum, pe.img, pt.x, pt.y);
          return { x: p.x, y: p.y };
        }));
      const result = await host.call('add-drawing', {
        pdf: pdfB64, page: pageNum, strokes: pdfStrokes,
        color: state.drawColor, width: state.drawWidth, pdfPassword: state.password,
      });
      pdfB64 = result.pdf;      // chain each page's strokes onto the growing document
      total += strokes.length;
    }
    const affectedPages = pages.map(([pageNum]) => pageNum);
    clearDrawing();
    await applyContentEdit(pdfB64, affectedPages, `Added ${total} stroke${total === 1 ? '' : 's'}.`);
    setTool('draw');
  } catch (e) {
    fail(e);
  }
}

function clearDrawing() {
  inkByPage.clear();
  for (const pe of pageEls) {
    const svg = pe.overlay.querySelector('.ink-layer');
    if (svg) svg.remove();
  }
}

// ------------------------------------------------------------- signatures

let padDrawing = false;
let padDirty = false;

function initSignaturePad() {
  const pad = $('sign-pad');
  const ctx = pad.getContext('2d');
  ctx.lineWidth = 2.2;
  ctx.lineCap = 'round';
  ctx.strokeStyle = '#1a237e';
  const position = (e) => {
    const rect = pad.getBoundingClientRect();
    return {
      x: (e.clientX - rect.left) * (pad.width / rect.width),
      y: (e.clientY - rect.top) * (pad.height / rect.height),
    };
  };
  pad.addEventListener('pointerdown', (e) => {
    padDrawing = true;
    padDirty = true;
    const p = position(e);
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    pad.setPointerCapture(e.pointerId);
  });
  pad.addEventListener('pointermove', (e) => {
    if (!padDrawing) return;
    const p = position(e);
    ctx.lineTo(p.x, p.y);
    ctx.stroke();
  });
  pad.addEventListener('pointerup', () => { padDrawing = false; });
  $('sign-pad-clear').addEventListener('click', () => {
    ctx.clearRect(0, 0, pad.width, pad.height);
    padDirty = false;
  });
}

async function applyImageSignature() {
  const region = state.pendingSignRegion;
  if (!region) return;
  try {
    let pngB64;
    if (!$('sign-upload').hidden) {
      const file = $('sign-file').files[0];
      if (!file) { toast('Choose an image first.'); return; }
      pngB64 = bytesToBase64(new Uint8Array(await file.arrayBuffer()));
    } else {
      if (!padDirty) { toast('Draw a signature first.'); return; }
      pngB64 = $('sign-pad').toDataURL('image/png').split(',')[1];
    }
    setStatus('Placing signature…', true);
    const result = await host.call('sign-image', {
      pdf: state.pdfB64, region, png: pngB64, pdfPassword: state.password,
    });
    hidePanels();
    setTool('select');
    await applyResult(result.pdf, 'Signature placed.');
  } catch (e) {
    fail(e);
  }
}

async function digitallySign() {
  const choice = await promptDialog('Digital certificate signature', [
    { id: 'reason', label: 'Reason (optional)', placeholder: 'Approved' },
    { id: 'location', label: 'Location (optional)' },
    { id: 'password', label: 'Certificate password', type: 'password' },
  ], 'Continue');
  if (!choice) return;

  modal.innerHTML = '<h2>Certificate</h2>' +
    '<p class="muted">Pick an existing PKCS#12 certificate (.p12/.pfx) or create a ' +
    'self-signed one. A digital signature proves the document has not been altered ' +
    'since signing.</p>';
  const actions = document.createElement('div');
  actions.className = 'actions';
  const useFile = document.createElement('button');
  useFile.textContent = '📄 Use certificate file…';
  const create = document.createElement('button');
  create.textContent = '✨ Create self-signed';
  const cancel = document.createElement('button');
  cancel.textContent = 'Cancel';
  actions.append(useFile, create, cancel);
  modal.appendChild(actions);
  modal.showModal();

  const sign = async (pfxB64, pfxPassword) => {
    try {
      setStatus('Signing…', true);
      const result = await host.call('sign-digital', {
        pdf: state.pdfB64,
        pfx: pfxB64,
        pfxPassword,
        reason: choice.reason || undefined,
        location: choice.location || undefined,
        pdfPassword: state.password,
      });
      await applyResult(result.pdf, 'Document digitally signed.');
    } catch (e) {
      fail(e);
    }
  };

  useFile.addEventListener('click', () => {
    modal.close();
    $('pfx-input').onchange = async () => {
      const file = $('pfx-input').files[0];
      $('pfx-input').value = '';
      if (!file) return;
      await sign(bytesToBase64(new Uint8Array(await file.arrayBuffer())), choice.password);
    };
    $('pfx-input').click();
  });
  create.addEventListener('click', async () => {
    modal.close();
    const details = await promptDialog('Create self-signed certificate', [
      { id: 'name', label: 'Your name', placeholder: 'Jane Citizen' },
      { id: 'pw', label: 'New certificate password', type: 'password' },
    ], 'Create & sign');
    if (!details) return;
    try {
      setStatus('Creating certificate…', true);
      const cert = await host.call('create-cert', {
        name: details.name || 'reDACT User', password: details.pw,
      });
      const blob = new Blob([base64ToBytes(cert.pfx)], { type: 'application/x-pkcs12' });
      chrome.downloads.download({
        url: URL.createObjectURL(blob),
        filename: 'pdf-editor-certificate.p12',
        saveAs: false,
      });
      toast('Certificate saved to your downloads for future use.');
      await sign(cert.pfx, details.pw);
    } catch (e) {
      fail(e);
    }
  });
  cancel.addEventListener('click', () => modal.close());
}

// ------------------------------------------------------ merge and protect

/** Classifies a picked file as a pdf, image, or Word doc by type/extension. */
function mergeKind(file) {
  const name = (file.name || '').toLowerCase();
  if (file.type.startsWith('image/') || /\.(png|jpe?g|gif|bmp|tiff?|webp)$/.test(name)) return 'image';
  if (/\.docx?$/.test(name) ||
      file.type === 'application/vnd.openxmlformats-officedocument.wordprocessingml.document') return 'docx';
  return 'pdf';
}

const MERGE_ICON = { pdf: '📄', image: '🖼', docx: '📝' };

async function mergeFiles() {
  $('merge-input').onchange = async () => {
    const files = [...$('merge-input').files];
    $('merge-input').value = '';
    if (files.length === 0) return;
    // Build the ordered entry list: the current document first, then each picked file tagged with
    // its kind so the host can convert images/Word to PDF pages before concatenating.
    const entries = [{ label: 'This document', data: state.pdfB64, kind: 'pdf', base: true }];
    for (const file of files) {
      entries.push({
        label: file.name || 'file',
        data: bytesToBase64(new Uint8Array(await file.arrayBuffer())),
        kind: mergeKind(file),
      });
    }
    showMergeDialog(entries);
  };
  $('merge-input').click();
}

/** Lets the user arrange (and drop) files before combining them, then merges in that order. */
function showMergeDialog(entries) {
  let order = entries.map((_, i) => i);

  modal.innerHTML = '<h2>Merge &amp; arrange</h2>' +
    '<p class="muted">Drag to set the order the files are combined in, or remove any you don\'t ' +
    'want. The current document is included first by default.</p>';
  const list = document.createElement('ol');
  list.className = 'reorder-list';
  list.id = 'merge-list';
  modal.appendChild(list);

  const move = (from, to) => {
    if (to < 0 || to >= order.length) return;
    const [it] = order.splice(from, 1);
    order.splice(to, 0, it);
    render();
  };
  const remove = (pos) => { if (order.length > 1) { order.splice(pos, 1); render(); } };

  let dragPos = null;
  const render = () => {
    list.innerHTML = '';
    order.forEach((entryIndex, pos) => {
      const e = entries[entryIndex];
      const li = document.createElement('li');
      li.className = 'organize-item';
      li.draggable = true;
      li.dataset.pos = String(pos);

      const grip = document.createElement('span');
      grip.className = 'organize-grip';
      grip.textContent = '⠿';
      const label = document.createElement('span');
      label.className = 'organize-label';
      label.textContent = `${MERGE_ICON[e.kind] ?? '📄'} ${e.label}`;

      const up = actionBtn('▲', 'Move up', pos === 0, () => move(pos, pos - 1));
      const down = actionBtn('▼', 'Move down', pos === order.length - 1, () => move(pos, pos + 1));
      const del = actionBtn('🗑', 'Remove', order.length <= 1, () => remove(pos));
      del.classList.add('organize-del');

      li.append(grip, label, up, down, del);
      wireReorderDnD(li, pos, () => dragPos, (v) => { dragPos = v; }, move);
      list.appendChild(li);
    });
  };
  render();

  const actions = document.createElement('div');
  actions.className = 'actions';
  const ok = document.createElement('button');
  ok.textContent = 'Merge';
  ok.className = 'danger';
  const cancel = document.createElement('button');
  cancel.textContent = 'Cancel';
  actions.append(ok, cancel);
  modal.appendChild(actions);

  ok.addEventListener('click', async () => {
    modal.close();
    const chosen = order.map((i) => entries[i]);
    try {
      setStatus('Merging…', true);
      // The document being edited goes with its password, so a protected one can be merged into
      // (and keeps its encryption) rather than being refused for want of it.
      const result = await host.call('merge-files', {
        files: chosen.map((e) => (e.base
          ? { data: e.data, kind: e.kind, base: true, password: state.password ?? undefined }
          : { data: e.data, kind: e.kind })),
      });
      const added = chosen.filter((e) => !e.base).length;
      await applyResult(result.pdf, `Merged ${added} file${added === 1 ? '' : 's'} in.`);
    } catch (e) {
      fail(e);
    }
  });
  cancel.addEventListener('click', () => modal.close());
  modal.showModal();
}

/** A small toolbar-style button used inside reorder rows. */
function actionBtn(text, title, disabled, onClick) {
  const b = document.createElement('button');
  b.textContent = text;
  b.title = title;
  b.setAttribute('aria-label', title); // accessible name (the label is an emoji glyph)
  b.disabled = disabled;
  b.addEventListener('click', onClick);
  return b;
}

/** Wires HTML5 drag-and-drop reordering onto a row; getPos/setPos hold the dragged position. */
function wireReorderDnD(li, pos, getPos, setPos, move) {
  li.addEventListener('dragstart', (e) => {
    setPos(pos);
    li.classList.add('dragging');
    e.dataTransfer.effectAllowed = 'move';
  });
  li.addEventListener('dragend', () => { li.classList.remove('dragging'); setPos(null); });
  li.addEventListener('dragover', (e) => { e.preventDefault(); li.classList.add('drop-target'); });
  li.addEventListener('dragleave', () => li.classList.remove('drop-target'));
  li.addEventListener('drop', (e) => {
    e.preventDefault();
    li.classList.remove('drop-target');
    const from = getPos();
    if (from !== null && from !== pos) move(from, pos);
  });
}

// ------------------------------------------------------------- page organizer

/** Opens the organizer: a reorderable, deletable list of the document's pages. */
async function openOrganize() {
  if (!state.info) return;
  state.organizeOrder = Array.from({ length: state.info.pageCount }, (_, i) => i + 1);
  showPanel('panel-organize');
  renderOrganizeList();
}

function renderOrganizeList() {
  const list = $('organize-list');
  list.innerHTML = '';
  const move = (from, to) => {
    if (to < 0 || to >= state.organizeOrder.length) return;
    const [it] = state.organizeOrder.splice(from, 1);
    state.organizeOrder.splice(to, 0, it);
    renderOrganizeList();
  };
  state.organizeOrder.forEach((pageNum, index) => {
    const li = document.createElement('li');
    li.className = 'organize-item';
    li.draggable = true;
    li.dataset.index = String(index);

    const grip = document.createElement('span');
    grip.className = 'organize-grip';
    grip.textContent = '⠿';
    const img = document.createElement('img');
    img.alt = `Page ${pageNum}`;
    const label = document.createElement('span');
    label.className = 'organize-label';
    label.textContent = `Page ${pageNum}`;

    const up = actionBtn('▲', 'Move up', index === 0, () => move(index, index - 1));
    const down = actionBtn('▼', 'Move down', index === state.organizeOrder.length - 1,
      () => move(index, index + 1));
    const del = actionBtn('🗑', 'Remove page', state.organizeOrder.length <= 1, () => {
      if (state.organizeOrder.length > 1) { state.organizeOrder.splice(index, 1); renderOrganizeList(); }
    });
    del.classList.add('organize-del');

    li.append(grip, img, label, up, down, del);
    wireReorderDnD(li, index, () => organizeDragIndex, (v) => { organizeDragIndex = v; }, move);
    list.appendChild(li);

    renderThumbToCache(pageNum)
      .then((png) => { if (li.isConnected) img.src = `data:image/png;base64,${png}`; })
      .catch(() => {});
  });
}

let organizeDragIndex = null;

async function applyOrganize() {
  const order = state.organizeOrder;
  const original = Array.from({ length: state.info.pageCount }, (_, i) => i + 1);
  const unchanged = order.length === original.length && order.every((v, i) => v === original[i]);
  if (unchanged) { hidePanels(); toast('No page changes to apply.'); return; }
  try {
    setStatus('Reorganizing pages…', true);
    const result = await host.call('arrange-pages', {
      pdf: state.pdfB64, order, pdfPassword: state.password,
    });
    hidePanels();
    await applyResult(result.pdf, 'Pages reorganized.');
  } catch (e) {
    fail(e);
  }
}

async function protect() {
  const value = await promptDialog('Password-protect (AES-256 encryption)', [
    { id: 'user', label: 'Password to open the document', type: 'password' },
    { id: 'owner', label: 'Owner password (optional, defaults to the same)', type: 'password' },
  ], 'Encrypt');
  if (!value) return;
  if (!value.user) { toast('A password is required.'); return; }
  if (state.signatures.length > 0) {
    const confirmed = await promptDialog(
      'This document is digitally signed. Encrypting rewrites the file and breaks ' +
      'existing signatures — sign again afterwards. Type YES to continue.',
      [{ id: 'confirm', label: 'Confirmation' }], 'Continue');
    if (!confirmed || confirmed.confirm !== 'YES') return;
  }
  try {
    setStatus('Encrypting…', true);
    const result = await host.call('encrypt', {
      pdf: state.pdfB64,
      userPassword: value.user,
      ownerPassword: value.owner || undefined,
      pdfPassword: state.password,
    });
    state.password = value.user;
    await applyResult(result.pdf, 'Document encrypted. Keep the password safe!');
  } catch (e) {
    fail(e);
  }
}

/**
 * #25: strips password protection, producing an unencrypted copy. The password was captured when
 * the document was opened (state.password), so no re-prompt is needed in the normal case; we only
 * ask if it is somehow missing. state.password is cleared *after* applyResult so the undo snapshot
 * loadDocument pushes still pairs the original encrypted bytes with their password.
 */
async function removeEncryption() {
  if (!state.pdf) return;
  if (!(state.info?.encrypted || state.password)) {
    toast('This document is not encrypted.');
    return;
  }
  let password = state.password;
  if (!password) {
    const entered = await promptDialog('Enter the current password to remove encryption', [
      { id: 'pw', label: 'Password', type: 'password' },
    ], 'Remove');
    if (!entered) return;
    password = entered.pw;
  }
  if (state.signatures.length > 0) {
    const confirmed = await promptDialog(
      'This document is digitally signed. Removing encryption rewrites the file and breaks ' +
      'existing signatures — sign again afterwards. Type YES to continue.',
      [{ id: 'confirm', label: 'Confirmation' }], 'Continue');
    if (confirmed?.confirm !== 'YES') return;
  }
  try {
    setStatus('Removing encryption…', true);
    const result = await host.call('decrypt', { pdf: state.pdfB64, password });
    await applyResult(result.pdf, 'Encryption removed.');
    state.password = null;
    updateChrome(); // clear the 🔒 badge and disable "Remove encryption" now the doc is open
  } catch (e) {
    fail(e);
  }
}

/**
 * #30: stamps a diagonal text watermark across every page. The mark is baked into each page's
 * content stream by the host (not added as a removable annotation or layer), so it survives being
 * reopened in another viewer and can't be toggled off from a layers/annotations panel.
 */
async function addWatermark() {
  if (!state.pdf) return;
  const value = await promptDialog('Add watermark', [
    { id: 'text', label: 'Text', placeholder: 'CONFIDENTIAL' },
    { id: 'color', label: 'Colour', type: 'color', value: '#808080' },
    { id: 'opacity', label: 'Opacity (%)', type: 'number', value: '30' },
    { id: 'rotation', label: 'Rotation (°)', type: 'number', value: '45' },
  ], 'Apply');
  if (!value) return;
  if (!value.text.trim()) { toast('Watermark text is required.'); return; }

  // The dialog collects opacity as a 0–100 percentage; the host wants a 0–1 fraction.
  const opacity = Math.min(1, Math.max(0.05, (Number(value.opacity) || 30) / 100));
  const rotation = Number(value.rotation);
  try {
    setStatus('Adding watermark…', true);
    const result = await host.call('watermark', {
      pdf: state.pdfB64,
      text: value.text,
      color: value.color,
      opacity,
      rotation: Number.isFinite(rotation) ? rotation : 45,
      pdfPassword: state.password,
    });
    await applyResult(result.pdf, 'Watermark added.');
  } catch (e) {
    fail(e);
  }
}

/**
 * #27: stamps sequential Bates numbers (prefix + zero-padded counter + suffix) into a corner of
 * each page. Like the watermark, the number is baked into the page content by the host.
 */
async function addBates() {
  if (!state.pdf) return;
  const value = await promptDialog('Bates numbering', [
    { id: 'prefix', label: 'Prefix', placeholder: 'ACME' },
    { id: 'start', label: 'Start at', type: 'number', value: '1' },
    { id: 'digits', label: 'Digits (zero-padded)', type: 'number', value: '6' },
    { id: 'suffix', label: 'Suffix (optional)', placeholder: '' },
    { id: 'position', label: 'Position', type: 'select', value: 'bottom-right', options: [
      { value: 'bottom-right', label: 'Bottom right' },
      { value: 'bottom-center', label: 'Bottom centre' },
      { value: 'bottom-left', label: 'Bottom left' },
      { value: 'top-right', label: 'Top right' },
      { value: 'top-center', label: 'Top centre' },
      { value: 'top-left', label: 'Top left' },
    ] },
  ], 'Apply');
  if (!value) return;

  const start = Number.parseInt(value.start, 10);
  const digits = Number.parseInt(value.digits, 10);
  try {
    setStatus('Adding Bates numbers…', true);
    const result = await host.call('bates', {
      pdf: state.pdfB64,
      prefix: value.prefix,
      suffix: value.suffix,
      start: Number.isFinite(start) && start >= 0 ? start : 1,
      digits: Number.isFinite(digits) ? Math.min(12, Math.max(1, digits)) : 6,
      position: value.position,
      pdfPassword: state.password,
    });
    await applyResult(result.pdf, `Bates numbers added (${result.first} – ${result.last}).`);
  } catch (e) {
    fail(e);
  }
}

/**
 * #44: flattens interactive layers into static page content. The mode chooses how much: forms only,
 * annotations only, or everything. Flattened content prints identically everywhere and can no longer
 * be edited or removed as an object.
 */
async function flattenDocument() {
  if (!state.pdf) return;
  const value = await promptDialog('Flatten', [
    { id: 'mode', label: 'What to flatten', type: 'select', value: 'everything', options: [
      { value: 'everything', label: 'Everything (forms + annotations)' },
      { value: 'forms', label: 'Form fields only' },
      { value: 'annotations', label: 'Annotations only (comments, markup)' },
    ] },
  ], 'Flatten');
  if (!value) return;
  try {
    setStatus('Flattening…', true);
    const result = await host.call('flatten', {
      pdf: state.pdfB64, mode: value.mode, pdfPassword: state.password,
    });
    const parts = [];
    if (result.formFields > 0) parts.push(`${result.formFields} form field${result.formFields === 1 ? '' : 's'}`);
    if (result.annotations > 0) parts.push(`${result.annotations} annotation${result.annotations === 1 ? '' : 's'}`);
    await applyResult(result.pdf, parts.length ? `Flattened ${parts.join(' and ')}.` : 'Nothing to flatten.');
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------- find and replace

async function findReplace() {
  const value = await promptDialog('Find & replace across the document', [
    { id: 'find', label: 'Find' },
    { id: 'replace', label: 'Replace with' },
  ], 'Replace all');
  if (!value || !value.find) return;
  try {
    setStatus('Replacing…', true);
    const result = await host.call('replace-all', {
      pdf: state.pdfB64,
      phrase: value.find,
      replacement: value.replace,
      pdfPassword: state.password,
    });
    if (result.count === 0) {
      setStatus('');
      toast(`No matches for “${value.find}”.`);
      return;
    }
    await applyResult(result.pdf, `Replaced ${result.count} occurrence${result.count === 1 ? '' : 's'}.`);
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------------ open / save

/** Classifies a file as pdf/image/docx by name extension (used when no MIME type is available). */
function kindForName(name) {
  const n = (name || '').toLowerCase();
  if (/\.(png|jpe?g|gif|bmp|tiff?|webp)$/.test(n)) return 'image';
  if (/\.docx?$/.test(n)) return 'docx';
  return 'pdf';
}

async function openFromBytes(bytes, name, kind) {
  try {
    state.history = [];
    state.future = [];
    state.password = null;
    state.regions = [];
    state.keepActiveContent = false; // re-arm the strip-on-save default for each new document
    state.keepLinks = false;
    state.urlVerdicts = [];
    state.compareOther = null; // a new document invalidates any prior comparison

    // #26: an image (or Word doc) is opened by first converting it to a PDF page in the host, so it
    // can be edited, OCR'd and merged like any other document. The working name becomes <name>.pdf.
    let workingName = name;
    const fileKind = kind ?? kindForName(name);
    if (fileKind === 'image' || fileKind === 'docx') {
      activity.add('info', 'importing', `${name} (${fileKind})`);
      setStatus(`Importing ${fileKind === 'image' ? 'image' : 'document'}…`, true);
      let res;
      try {
        res = await host.call('import', { data: bytesToBase64(bytes), kind: fileKind });
      } catch (e) {
        // A native host installed before image support lacks this action; say so plainly rather
        // than surfacing a bare "unknown action".
        if (/unknown action/i.test(e?.message ?? '')) {
          throw new Error('Opening images needs an updated native host. Please reinstall the '
            + 'reDACT native host (it was installed before image support was added), then retry.');
        }
        throw e;
      }
      bytes = base64ToBytes(res.pdf);
      workingName = name.replace(/\.[^.]+$/, '') + '.pdf';
    }

    activity.add('info', 'opening document', `${workingName} (${bytes.length} bytes)`);
    await loadDocument(bytes, workingName);
    activity.add('info', 'document opened', `${workingName} — ${state.info.pageCount} page(s)`);
    toast(`Opened ${workingName}.`);
  } catch (e) {
    fail(e);
  }
}

async function openFilePicker() {
  $('file-input').onchange = async () => {
    const file = $('file-input').files[0];
    $('file-input').value = '';
    // Prefer the browser-supplied MIME type (mergeKind) and fall back to the name extension.
    if (file) await openFromBytes(new Uint8Array(await file.arrayBuffer()), file.name, mergeKind(file));
  };
  $('file-input').click();
}

/**
 * Lets a PDF or image be opened by dragging it onto the window. Without this, dropping a file makes
 * Chrome navigate away from the viewer — so a dragged image simply never opened. Only file drops are
 * handled; a drag that carries no files (e.g. selecting text) is left alone.
 */
function initDragDrop() {
  const carriesFiles = (e) => Array.from(e.dataTransfer?.types ?? []).includes('Files');
  // Chrome only leaves the file on the page (rather than navigating to it) if the default is
  // prevented on *every* drag event in the sequence — dragenter and dragover as well as drop.
  const allow = (e) => {
    if (!carriesFiles(e)) return false;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
    return true;
  };
  for (const type of ['dragenter', 'dragover']) {
    window.addEventListener(type, (e) => { if (allow(e)) document.body.classList.add('drag-over'); });
  }
  window.addEventListener('dragleave', (e) => {
    // relatedTarget is null when the pointer leaves the window entirely.
    if (e.relatedTarget === null) document.body.classList.remove('drag-over');
  });
  window.addEventListener('drop', async (e) => {
    if (!allow(e)) return;
    document.body.classList.remove('drag-over');
    const file = e.dataTransfer.files[0];
    if (!file) { toast('Drop a single PDF or image file.'); return; }
    activity.add('info', 'file dropped', `${file.name} (${file.type || 'unknown type'})`);
    await openFromBytes(new Uint8Array(await file.arrayBuffer()), file.name, mergeKind(file));
  });
}

// Blocks loopback/private/link-local hosts -- including the 169.254.169.254 cloud metadata
// address -- so a crafted `src=` param can't turn the credentialed fetch below into an SSRF
// probe of internal network services. Hostname string matching only (no DNS is done client
// side); this narrows the attack surface, it isn't a substitute for the server-side checks
// any real internal service should already have.
function isPrivateOrLocalHost(hostname) {
  const h = hostname.toLowerCase();
  if (h === 'localhost' || h === '0.0.0.0' || h === '::1' || h === '') return true;
  if (/^127\./.test(h)) return true; // loopback
  if (/^10\./.test(h)) return true; // RFC1918
  if (/^172\.(1[6-9]|2\d|3[01])\./.test(h)) return true; // RFC1918
  if (/^192\.168\./.test(h)) return true; // RFC1918
  if (/^169\.254\./.test(h)) return true; // link-local, incl. cloud metadata endpoints
  if (/^\[?f[cd][0-9a-f]{2}:/i.test(h) || h === '[::1]') return true; // IPv6 unique-local/loopback
  return false;
}

// Only http(s)/file URLs ending in .pdf, on a non-local/private host, are legitimate here --
// this page is opened with a `src=` query param that (in principle) reflects whatever the
// caller passed, so re-validate it ourselves rather than trusting that every caller already
// did (defense in depth; this must never become an arbitrary-URL-fetch-with-credentials
// primitive, nor a way to probe internal network services).
function looksLikePdfUrl(rawUrl) {
  try {
    const parsed = new URL(rawUrl);
    if (!/^https?:|^file:/.test(parsed.protocol)) return false;
    if (parsed.protocol !== 'file:' && isPrivateOrLocalHost(parsed.hostname)) return false;
    return parsed.pathname.toLowerCase().endsWith('.pdf');
  } catch (e) {
    // Unparseable input is a rejection, not a crash — but record what was rejected and why, so a
    // legitimate URL the viewer refuses can be diagnosed instead of guessed at (#72).
    activity.add('warn', 'rejected the src parameter', `${rawUrl} — ${e?.message ?? e}`);
    return false;
  }
}

async function openFromUrl(url) {
  if (!looksLikePdfUrl(url)) {
    fail(new Error('Refusing to open a non-PDF or unsupported URL.'));
    return;
  }
  try {
    setStatus(`Fetching ${url}…`, true);
    const response = await fetch(url, { credentials: 'include' });
    if (!response.ok) throw new Error(`Could not fetch the PDF (HTTP ${response.status}).`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    const name = decodeURIComponent(new URL(url).pathname.split('/').pop() || 'document.pdf');
    await openFromBytes(bytes, name);
  } catch (e) {
    fail(e);
  }
}

// ----------------------------------------------------- forms + JS/URL safety

/** Explains detected active content and lets the user keep it (default: strip on save). */
async function showSafetyDialog() {
  const s = state.safety;
  if (!s?.javaScriptCount) return;
  modal.innerHTML = '<h2>⚠ Embedded JavaScript</h2>';
  const p = document.createElement('p');
  p.className = 'muted';
  p.textContent = `This document contains ${s.javaScriptCount} embedded JavaScript ` +
    `action${s.javaScriptCount === 1 ? '' : 's'}, which can run when the file is opened in ` +
    'another PDF viewer. Nothing runs inside this editor. It is ' +
    (state.keepActiveContent ? 'currently kept.' : 'disabled and will be removed when you save.');
  modal.appendChild(p);

  const srcLabel = document.createElement('p');
  srcLabel.className = 'muted';
  srcLabel.textContent = 'Source:';
  modal.appendChild(srcLabel);
  const pre = document.createElement('pre');
  pre.className = 'safety-samples';
  pre.textContent = 'Loading…';
  modal.appendChild(pre);

  const actions = document.createElement('div');
  actions.className = 'actions';
  const toggle = document.createElement('button');
  toggle.className = state.keepActiveContent ? '' : 'danger';
  toggle.textContent = state.keepActiveContent ? 'Disable & strip on save' : 'Enable (keep) active content';
  const edit = document.createElement('button');
  edit.textContent = '⚙ Open in JavaScript editor';
  const close = document.createElement('button');
  close.textContent = 'Close';
  actions.append(toggle, edit, close);
  modal.appendChild(actions);
  modal.showModal();

  // Pull the full source of every detected script so the warning points at the actual code.
  try {
    const result = await host.call('js-sources', { pdf: state.pdfB64, pdfPassword: state.password });
    const sources = result.sources ?? [];
    // textContent — attacker-authored script text is shown inert, never executed or parsed as HTML.
    pre.textContent = sources.length
      ? sources.map((src, i) => `/* — script ${i + 1} — */\n${src}`).join('\n\n')
      : (s.samples?.join('\n') || '(source unavailable)');
  } catch (e) {
    // Falls back to the scan's samples. Worth recording: the dialog then shows a *partial* view
    // of the active content it is warning about, which is not what it appears to be (#72).
    activity.add('warn', 'full script sources unavailable',
      `${e?.message ?? e} — showing the scan's samples instead`);
    pre.textContent = s.samples?.join('\n') || '(source unavailable)';
  }

  toggle.addEventListener('click', () => {
    state.keepActiveContent = !state.keepActiveContent;
    modal.close();
    updateChrome();
    toast(state.keepActiveContent
      ? 'Active content will be kept when you save.'
      : 'Active content will be stripped when you save.');
  });
  edit.addEventListener('click', () => { modal.close(); openJavaScript(); });
  close.addEventListener('click', () => modal.close());
}

/** The "Run" control that locally simulates a field's attached script (see formScript.js). */
function runControl(field) {
  const run = document.createElement('button');
  run.type = 'button';
  run.className = 'form-field-run';
  run.textContent = 'Run';
  run.title = field.script
    ? "Simulates this field's script (calculations / show-hide only — see help)"
    : 'This field has no script attached';
  run.addEventListener('click', () => runFormButtonScript(field));
  return run;
}

async function openForms() {
  try {
    setStatus('Reading form fields…', true);
    const result = await host.call('form-fields', { pdf: state.pdfB64, pdfPassword: state.password });
    setStatus('');
    // The document on disk is the source of truth for the field *set*, but not for values the user
    // has already typed (on the page or here) and not yet applied — keep those.
    const fields = (result.fields ?? []).map((f) => {
      const edited = (state.formFields ?? []).find((s) => s.name === f.name);
      return edited ? { ...f, value: edited.value } : f;
    });
    state.formFields = fields;
    const list = $('forms-list');
    list.innerHTML = '';
    $('forms-empty').hidden = fields.length > 0;
    $('forms-flatten-row').hidden = fields.length === 0;
    $('forms-apply').disabled = fields.length === 0;
    for (const f of fields) {
      const row = document.createElement('div');
      row.className = 'form-field';
      row.dataset.fieldName = f.name; // lets a button's script show/hide this row
      const label = document.createElement('span');
      label.textContent = f.name; // textContent — field names come from the document
      row.appendChild(label);

      if (f.type === 'button') {
        // A push button has no fillable value — it triggers its click script instead. Buttons are
        // deliberately excluded from [data-field] so applyForms() never tries to "fill" them.
        row.appendChild(runControl(f));
        list.appendChild(row);
        continue;
      }

      let input;
      if (f.type === 'checkbox') {
        input = document.createElement('input');
        input.type = 'checkbox';
        input.dataset.on = (f.options || []).find((o) => o && o !== 'Off') || 'Yes';
        input.checked = !!f.value && f.value !== 'Off';
      } else if ((f.type === 'choice' || f.type === 'radio') && f.options?.length) {
        input = document.createElement('select');
        // Radio "options" include the implicit Off (unselected) state — don't offer it as a choice.
        for (const o of f.options.filter((o) => f.type !== 'radio' || o !== 'Off')) {
          const opt = document.createElement('option');
          opt.value = o;
          opt.textContent = o;
          input.appendChild(opt);
        }
        input.value = f.value;
      } else {
        input = document.createElement('input');
        input.type = 'text';
        input.value = f.value ?? '';
      }
      input.dataset.field = f.name;
      if (f.readOnly) input.disabled = true;
      // Keep the panel and the on-page control showing the same value, whichever one is edited.
      input.addEventListener('input', () => setFieldValue(f.name,
        input.type === 'checkbox' ? (input.checked ? (input.dataset.on ?? 'Yes') : 'Off') : input.value,
        input));
      row.appendChild(input);
      // A non-button field can carry a script too (on its widget's /AA /U), which fires when the
      // user activates it in a real reader. Offer the same local simulation button buttons get.
      if (f.script) row.appendChild(runControl(f));
      list.appendChild(row);
    }
    showPanel('panel-forms');
  } catch (e) {
    fail(e);
  }
}

/**
 * Simulates a form button's click script against the other fields currently shown in the Forms
 * panel — the viewer's stand-in for real PDF JavaScript execution (see extension/src/formScript.js
 * for exactly which patterns are supported: field-to-field calculations, show/hide, and reset).
 * Nothing here is saved until the user hits "Apply"; scripts outside that supported grammar are
 * reported rather than silently ignored or half-run (issues #18 and #22).
 */
function runFormButtonScript(f) {
  if (!f.script) {
    toast(`"${f.name}" has no script attached — nothing to run.`);
    return;
  }
  // Read from the model, not from one view's inputs — a script must see the current values whether
  // they were typed on the page or in the panel, and whether or not the panel is even open.
  const getValue = (name) =>
    (state.formFields ?? []).find((field) => field.name === name)?.value ?? '';

  const result = runFormScript(f.script, getValue);
  if (!result.ok) {
    toast(`⚠ "${f.name}" runs JavaScript this viewer can't simulate (only calculations, show/hide, ` +
      'alerts and resets are supported here) — it will run in full once the saved file is opened ' +
      'in Acrobat or Chrome.');
    return;
  }

  for (const { name, value } of result.sets) setFieldValue(name, value);
  for (const { name, hidden } of result.display) {
    const row = [...$('forms-list').children].find((el) => el.dataset.fieldName === name);
    if (row) row.hidden = hidden;
    for (const marker of document.querySelectorAll(
      `[data-page-field="${CSS.escape(name)}"]`)) marker.closest('.field-marker').hidden = hidden;
  }
  if (result.reset) {
    for (const field of state.formFields ?? []) {
      if (field.type !== 'button') setFieldValue(field.name, field.type === 'checkbox' ? 'Off' : '');
    }
  }
  toast(result.sets.length > 0
    ? `Ran "${f.name}"'s JavaScript — ${result.sets.length} field${result.sets.length === 1 ? '' : 's'} updated.`
    : `Ran "${f.name}"'s JavaScript.`);
  if (result.alerts.length > 0) showScriptAlert(f.name, result.alerts);
}

/** Shows an app.alert() message from a field's script, the way a real reader would. */
function showScriptAlert(fieldName, messages) {
  modal.innerHTML = '';
  const heading = document.createElement('h2');
  heading.textContent = 'This document says'; // mirrors the browser's own alert phrasing
  modal.appendChild(heading);
  for (const message of messages) {
    const p = document.createElement('p');
    p.textContent = message; // textContent — the message is document-authored, never HTML
    modal.appendChild(p);
  }
  const note = document.createElement('p');
  note.className = 'muted';
  note.textContent = `From the script on "${fieldName}".`;
  modal.appendChild(note);
  const actions = document.createElement('div');
  actions.className = 'actions';
  const ok = document.createElement('button');
  ok.textContent = 'OK';
  ok.addEventListener('click', () => modal.close());
  actions.appendChild(ok);
  modal.appendChild(actions);
  modal.showModal();
  ok.focus();
}

async function applyForms() {
  const values = {};
  // Start from the model so edits made on the page count even when the panel was never opened,
  // then let the panel's own inputs win for anything currently shown there.
  for (const f of state.formFields ?? []) {
    if (f.readOnly || f.type === 'button') continue;
    values[f.name] = f.value ?? '';
  }
  for (const input of $('forms-list').querySelectorAll('[data-field]')) {
    if (input.disabled) continue;
    if (input.type === 'checkbox') {
      values[input.dataset.field] = input.checked ? input.dataset.on : 'Off';
    } else {
      values[input.dataset.field] = input.value;
    }
  }
  const flatten = $('forms-flatten').checked;
  try {
    setStatus('Filling form…', true);
    const result = await host.call('fill-form', {
      pdf: state.pdfB64, values, flatten, pdfPassword: state.password,
    });
    hidePanels();
    await applyResult(result.pdf, flatten ? 'Form filled and flattened.' : 'Form filled.');
  } catch (e) {
    fail(e);
  }
}

// Region-overlay class per active tool; anything not listed (e.g. text editing) gets 'edit'.
const REGION_CLASS_BY_TOOL = { redact: '', sign: 'sign', highlight: 'highlight' };

const FIELD_LABELS = {
  text: 'Text field', multiline: 'Text area', checkbox: 'Checkbox', dropdown: 'Dropdown',
  radio: 'Option buttons', button: 'Button',
};
// Field types that need a list of options.
const OPTION_TYPES = new Set(['dropdown', 'radio']);

/** Shows only the extra inputs (options / caption+script) the chosen field type needs. */
function updateFieldTypeRows() {
  const type = $('field-type').value;
  $('field-options-row').hidden = !OPTION_TYPES.has(type);
  $('field-caption-row').hidden = type !== 'button'; // only a push button has a visible label
  // Every field type can carry a script: a button's goes on its /A activation action, everything
  // else's on the widget's /AA /U (mouse-up). So this row applies to all of them.
  $('field-script-row').hidden = false;
}

/** Enters "place a field" mode: the next box drawn on a page becomes a new form field. */
function beginPlaceField() {
  if (!state.pdf) return;
  const fieldType = $('field-type').value;
  const options = OPTION_TYPES.has(fieldType)
    ? $('field-options').value.split('\n').map((o) => o.trim()).filter(Boolean)
    : [];
  if (fieldType === 'dropdown' && options.length === 0) {
    toast('Add at least one dropdown option first.');
    return;
  }
  if (fieldType === 'radio' && options.length < 2) {
    toast('Add at least two options for an option group.');
    return;
  }
  state.pendingField = {
    fieldType, name: $('field-name').value.trim(), options,
    caption: fieldType === 'button' ? $('field-caption').value.trim() : '',
    script: $('field-script').value,
  };
  state.tool = 'field';
  for (const b of document.querySelectorAll('.tool')) b.classList.remove('active');
  for (const pe of pageEls) pe.overlay.classList.add('tool-active');
  pagesEl.classList.remove('select-mode'); // let the overlay capture the placement drag
  hidePanels();
  toast('Drag a box where the field should go.');
}

async function placeField(region) {
  const pf = state.pendingField;
  if (!pf) return;
  state.pendingField = null;
  try {
    setStatus('Adding field…', true);
    const result = await host.call('add-form-field', {
      pdf: state.pdfB64, region, fieldType: pf.fieldType,
      name: pf.name || undefined, options: pf.options?.length ? pf.options : undefined,
      caption: pf.caption || undefined, script: pf.script || undefined,
      pdfPassword: state.password,
    });
    // A button carrying a script is deliberately-authored active content — keep it on save, and
    // say so explicitly (rather than only lighting up the badge a moment later) so the user isn't
    // surprised the JavaScript survives the save (#22).
    if (pf.script) state.keepActiveContent = true;
    setTool('select');
    const message = pf.script
      ? `${FIELD_LABELS[pf.fieldType] ?? 'Field'} added — its JavaScript will be kept when you save.`
      : `${FIELD_LABELS[pf.fieldType] ?? 'Field'} added.`;
    // Refresh the document and the field list *before* toasting: openForms() drives its own
    // "Reading form fields…" status while it fetches, which would otherwise stomp the toast
    // if shown first.
    await loadDocument(base64ToBytes(result.pdf), null, { pushHistory: true });
    await openForms(); // show the updated field list (and let them add another)
    toast(message);
  } catch (e) {
    fail(e);
  }
}

// --------------------------------------------------------- document JavaScript

/** Opens the JavaScript editor: a ¾-screen window listing the document's scripts plus a code editor. */
async function openJavaScript() {
  if (!state.pdf) return;
  $('js-name').value = '';
  $('js-source').value = '';
  if (!$('js-dialog').open) $('js-dialog').showModal();
  try {
    setStatus('Reading scripts…', true);
    await refreshScripts();
    setStatus('');
  } catch (e) {
    fail(e);
  }
}

/** Reloads the document's script list into the panel without disturbing the status line. */
async function refreshScripts() {
  const result = await host.call('list-scripts', { pdf: state.pdfB64, pdfPassword: state.password });
  state.scripts = result.scripts ?? [];
  renderScriptList();
}

function renderScriptList() {
  const list = $('js-list');
  list.innerHTML = '';
  for (const s of state.scripts) {
    const li = document.createElement('li');
    li.className = 'organize-item';
    const label = document.createElement('span');
    label.className = 'organize-label';
    label.textContent = s.name; // textContent — names come from the document
    // Load a script into the editor for editing.
    li.addEventListener('click', (e) => {
      if (e.target.closest('button')) return;
      $('js-name').value = s.name;
      $('js-source').value = s.script;
      for (const el of list.querySelectorAll('.organize-item')) el.classList.remove('active');
      li.classList.add('active');
    });
    const del = actionBtn('🗑', 'Remove script', false, () => removeScript(s.name));
    del.classList.add('organize-del');
    li.append(label, del);
    list.appendChild(li);
  }
}

async function addScript() {
  const name = $('js-name').value.trim();
  const script = $('js-source').value;
  if (!name) { toast('Give the script a name.'); return; }
  if (!script.trim()) { toast('The script is empty.'); return; }
  try {
    setStatus('Adding script…', true);
    const result = await host.call('add-script', {
      pdf: state.pdfB64, name, script, pdfPassword: state.password,
    });
    state.keepActiveContent = true; // the user added this on purpose — keep it on save
    await applyResult(result.pdf, `Script “${name}” added.`);
    $('js-name').value = '';
    $('js-source').value = '';
    await refreshScripts(); // update the list, leaving the "added" status visible
  } catch (e) {
    fail(e);
  }
}

async function removeScript(name) {
  try {
    setStatus('Removing script…', true);
    const result = await host.call('remove-script', {
      pdf: state.pdfB64, name, pdfPassword: state.password,
    });
    await applyResult(result.pdf, `Script “${name}” removed.`);
    await refreshScripts();
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------------------------ OCR

/** Runs OCR over the document, replacing it with a searchable copy (image + invisible text). */
async function runOcr() {
  if (!state.pdf) return;
  try {
    // Check up front so we can show a helpful note instead of failing mid-operation.
    const { available } = await host.call('ocr-available', {});
    if (!available) { showOcrRequirement(); return; }
    setStatus('Recognising text (OCR)… this can take a moment.', true);
    const result = await host.call('ocr-searchable', {
      pdf: state.pdfB64, pdfPassword: state.password,
    });
    await applyResult(result.pdf, 'Document made searchable (OCR).');
  } catch (e) {
    fail(e);
  }
}

/** An in-app note explaining that OCR needs Tesseract installed, with per-platform commands. */
function showOcrRequirement() {
  modal.innerHTML =
    '<h2>OCR needs Tesseract</h2>' +
    '<p class="muted">“Make searchable (OCR)” recognises text in scanned pages using ' +
    'Tesseract OCR, which runs alongside the native host. It isn’t installed on this machine.</p>' +
    '<p class="muted">Install it, restart your browser, then try again:</p>' +
    '<ul class="muted note-list">' +
    '<li><b>Linux:</b> <code>sudo apt install tesseract-ocr tesseract-ocr-eng</code></li>' +
    '<li><b>macOS:</b> <code>brew install tesseract</code></li>' +
    '<li><b>Windows:</b> <code>winget install -e --id tesseract-ocr.tesseract</code></li>' +
    '</ul>';
  const actions = document.createElement('div');
  actions.className = 'actions';
  const ok = document.createElement('button');
  ok.textContent = 'Got it';
  ok.addEventListener('click', () => modal.close());
  actions.appendChild(ok);
  modal.appendChild(actions);
  modal.showModal();
}

// --------------------------------------------------------- document comparison

/** Opens the comparison panel and prompts for the other version to diff against. */
function openCompare() {
  if (!state.pdf) return;
  $('compare-summary').innerHTML = '';
  $('compare-list').innerHTML = '';
  showPanel('panel-compare');
  pickCompareFile();
}

function pickCompareFile() {
  $('compare-input').onchange = async () => {
    const file = $('compare-input').files[0];
    $('compare-input').value = '';
    if (!file) return;
    try {
      setStatus('Comparing…', true);
      const other = bytesToBase64(new Uint8Array(await file.arrayBuffer()));
      const report = await host.call('compare', {
        pdf: state.pdfB64, other, pdfPassword: state.password,
      });
      setStatus('');
      // Keep the compared bytes so the visual (pixel) diff can be requested per page (#46).
      state.compareOther = { b64: other, name: file.name };
      renderCompare(report, file.name);
    } catch (e) {
      fail(e);
    }
  };
  $('compare-input').click();
}

function renderCompare(report, otherName) {
  const summary = $('compare-summary');
  summary.innerHTML = '';
  const line = document.createElement('div');
  // otherName comes from the file the user picked — set as text, never HTML.
  line.append(`vs ${otherName}: `);
  if (report.identical) {
    line.append('no text differences.');
  } else {
    const added = document.createElement('span');
    added.className = 'stat added';
    added.textContent = `+${report.addedWords} added`;
    const removed = document.createElement('span');
    removed.className = 'stat removed';
    removed.textContent = `−${report.removedWords} removed`;
    const pages = document.createElement('span');
    pages.className = 'stat';
    pages.textContent = `${report.changedPages} page${report.changedPages === 1 ? '' : 's'} changed`;
    line.append(added, removed, pages);
  }
  summary.appendChild(line);

  const list = $('compare-list');
  list.innerHTML = '';
  for (const pg of report.pages ?? []) {
    const li = document.createElement('li');
    li.className = 'organize-item compare-page';
    const label = document.createElement('div');
    label.className = 'organize-label';
    const num = document.createElement('button');
    num.className = 'compare-page-num compare-jump';
    num.textContent = `Page ${pg.page}`;
    num.title = 'Jump to this page';
    num.addEventListener('click', () => goToPage(pg.page)); // #47: navigate to the changed page
    const words = document.createElement('div');
    words.className = 'compare-words';
    for (const w of pg.removed ?? []) {
      const s = document.createElement('span');
      s.className = 'w-del';
      s.textContent = w; // document text — never HTML
      words.appendChild(s);
    }
    for (const w of pg.added ?? []) {
      const s = document.createElement('span');
      s.className = 'w-add';
      s.textContent = w;
      words.appendChild(s);
    }
    label.append(num, words);
    const vbtn = document.createElement('button');
    vbtn.className = 'compare-visual';
    vbtn.textContent = '🖼 Visual diff';
    vbtn.title = 'Show a rendered pixel diff of this page';
    vbtn.addEventListener('click', () => showVisualDiff(pg.page));
    label.appendChild(vbtn);
    li.appendChild(label);
    list.appendChild(li);
  }

  // A visual diff can be run on ANY page, not only the text-changed ones — that is the point of a
  // pixel diff: it catches changes text extraction misses (#46). Offer a page picker for that.
  const vrow = document.createElement('div');
  vrow.className = 'compare-visual-row';
  vrow.append('Visual diff — page ');
  const pageInput = document.createElement('input');
  pageInput.type = 'number';
  pageInput.min = '1';
  pageInput.value = String(state.page);
  pageInput.className = 'compare-visual-page';
  pageInput.setAttribute('aria-label', 'Page to visually diff');
  const showBtn = document.createElement('button');
  showBtn.textContent = 'Show';
  showBtn.addEventListener('click', () => showVisualDiff(Number.parseInt(pageInput.value, 10) || 1));
  vrow.append(pageInput, showBtn);
  summary.appendChild(vrow);
}

/** #46: fetches and shows the rendered pixel diff for one page against the compared document. */
async function showVisualDiff(page) {
  if (!state.compareOther) { toast('Compare a document first.'); return; }
  try {
    setStatus('Rendering visual diff…', true);
    const res = await host.call('visual-diff', {
      pdf: state.pdfB64,
      other: state.compareOther.b64,
      page,
      pdfPassword: state.password,
    });
    setStatus('');

    modal.innerHTML = '<h2>Visual diff</h2>';
    const legend = document.createElement('p');
    legend.className = 'muted';
    legend.textContent = `Page ${res.page} · ${(res.changedFraction * 100).toFixed(1)}% of pixels `
      + 'changed. Red = added in this version, blue = removed, grey = unchanged.';
    modal.appendChild(legend);

    const img = document.createElement('img');
    img.className = 'visual-diff-img';
    img.alt = `Visual diff of page ${res.page}`;
    img.src = `data:image/png;base64,${res.png}`;
    modal.appendChild(img);

    const actions = document.createElement('div');
    actions.className = 'actions';
    const close = document.createElement('button');
    close.textContent = 'Close';
    actions.appendChild(close);
    modal.appendChild(actions);
    modal.showModal();
    close.addEventListener('click', () => modal.close());
    close.focus();
  } catch (e) {
    fail(e);
  }
}

// ----------------------------------------------------- remove hidden information

// The categories the sanitizer can strip, in display order: [optionKey, label].
const HIDDEN_CATEGORIES = [
  ['metadataFields', 'metadata', 'Document metadata (author, software, dates)'],
  ['attachments', 'attachments', 'Embedded file attachments'],
  ['scriptsAndActions', 'scriptsAndActions', 'JavaScript & actions'],
  ['annotations', 'annotations', 'Comments & markup annotations'],
  ['bookmarks', 'bookmarks', 'Bookmarks / outline'],
  ['hiddenLayers', 'hiddenLayers', 'Hidden layers'],
];

/** Opens the sanitiser: inspects the document and lists each category of hidden data found. */
async function openSanitize() {
  if (!state.pdf) return;
  showPanel('panel-sanitize');
  try {
    setStatus('Scanning for hidden data…', true);
    state.hidden = await host.call('inspect-hidden', { pdf: state.pdfB64, pdfPassword: state.password });
    setStatus('');
    renderSanitizeItems();
  } catch (e) {
    fail(e);
  }
}

function renderSanitizeItems() {
  const box = $('sanitize-items');
  box.innerHTML = '';
  const h = state.hidden ?? {};
  $('sanitize-clean').hidden = !!h.hasAny;
  $('sanitize-apply').disabled = !h.hasAny;
  for (const [countKey, optKey, label] of HIDDEN_CATEGORIES) {
    const count = h[countKey] ?? 0;
    const row = document.createElement('label');
    row.className = 'form-field';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.dataset.opt = optKey;
    cb.checked = count > 0;
    cb.disabled = count === 0; // nothing of this kind to remove
    const text = document.createElement('span');
    text.textContent = count > 0 ? `${label} — ${count} found` : `${label} — none`;
    row.append(cb, text);
    box.appendChild(row);
  }
}

async function applySanitize() {
  const options = {};
  for (const cb of $('sanitize-items').querySelectorAll('[data-opt]')) {
    options[cb.dataset.opt] = cb.checked && !cb.disabled;
  }
  if (!Object.values(options).some(Boolean)) { toast('Nothing selected to remove.'); return; }
  try {
    setStatus('Removing hidden information…', true);
    const result = await host.call('sanitize', {
      pdf: state.pdfB64, ...options, pdfPassword: state.password,
    });
    hidePanels();
    await applyResult(result.pdf, 'Hidden information removed.');
  } catch (e) {
    fail(e);
  }
}

/** Lets Tab indent inside a code editor instead of moving focus out of it. */
function enableCodeEditorTab(textarea) {
  textarea.addEventListener('keydown', (e) => {
    if (e.key !== 'Tab') return;
    e.preventDefault();
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    textarea.value = textarea.value.slice(0, start) + '  ' + textarea.value.slice(end);
    textarea.selectionStart = textarea.selectionEnd = start + 2;
  });
}

// ---------------------------------------------------------------- links / URLs

const FINDING_LINKS = 'Finding links…';

// Human labels for non-URL link actions (URL links show the URL instead).
const LINK_KIND_LABELS = {
  javascript: 'JavaScript action',
  goto: 'Go to a place in this document',
  'remote-goto': 'Open another document',
  launch: 'Open a file or program',
  named: 'Named action',
  submit: 'Submit form data',
  link: 'Link',
};

// Only the newest link run may touch link state or the progress indicator. A run is stale as soon
// as the document changes (state.version) or a newer run of the same pipeline starts, and a stale
// run's results are dropped — otherwise a scan begun on document A repaints document B's overlay
// and panel, which is exactly how #24 (the panel showing the previous document's links) would come
// back. The overlay and the panel are counted separately: they are fetched independently and run
// concurrently, so one must not cancel the other.
const linkRuns = { overlay: 0, panel: 0 };

/**
 * Starts a link run of one pipeline ('overlay' or 'panel'). Every `await` in a link pipeline has
 * to be followed by `stale()` before anything is written to state or the DOM. `current()` is the
 * weaker test used for the progress indicator: only a superseded run must keep its hands off it,
 * or a run cut short by an edit (which bumps the version without starting a new run) would leave
 * the spinner turning forever.
 */
function beginLinkRun(kind) {
  const run = ++linkRuns[kind];
  const version = state.version;
  return {
    stale: () => run !== linkRuns[kind] || version !== state.version,
    current: () => run === linkRuns[kind],
  };
}

/**
 * On load, fetch every link annotation (with hotspot rects), rate the web ones, and draw them.
 *
 * Runs in the background in two phases so a link-heavy document is usable immediately: the
 * hotspots are drawn as soon as the annotations are listed (rated "unknown"), and recoloured when
 * the risk scan — which may reach out to Cloudflare and take seconds — finally comes back.
 */
async function refreshLinks() {
  if (!URL_SCANNING_ENABLED) {
    state.linkHotspots = [];
    state.urlVerdicts = [];
    drawLinks();
    return;
  }
  const link = beginLinkRun('overlay');
  setBackgroundStatus('Reading links…');
  try {
    const result = await host.call('list-link-hotspots', { pdf: state.pdfB64, pdfPassword: state.password });
    if (link.stale()) return;
    state.linkHotspots = result.links ?? [];
    state.urlVerdicts = [];
    drawLinks(); // phase 1: show the hotspots now, unrated, rather than after the scan
    const uriCount = state.linkHotspots.filter((l) => l.kind === 'uri').length;
    if (uriCount === 0) return;
    setBackgroundStatus(`Checking ${uriCount} link${uriCount === 1 ? '' : 's'}…`);
    try {
      const creds = await chrome.storage.local.get({ cfAccountId: '', cfApiToken: '' });
      if (link.stale()) return;
      const scan = await host.call('scan-urls', {
        pdf: state.pdfB64, pdfPassword: state.password,
        cfAccountId: creds.cfAccountId, cfApiToken: creds.cfApiToken,
      });
      if (link.stale()) return;
      state.urlVerdicts = scan.verdicts ?? [];
      drawLinks(); // phase 2: recolour by risk
    } catch (e) {
      // Every hotspot's colour falls back to "unknown" — which reads as "not yet rated", not as
      // "rating failed". Say which it was (#72).
      activity.add('warn', 'link rating failed',
        `${e?.message ?? e} — links stay shown as unrated`);
    }
  } catch (e) {
    // Links are a nicety; never block on them. But with no hotspots the document looks like it
    // simply has none, which is the same class of silent lie as an empty forms panel (#72).
    activity.add('warn', 'links could not be read',
      `${e?.message ?? e} — the document will appear to contain no links`);
  } finally {
    // Clears on failure as well as on success — but only for the run that is still current, so a
    // superseded run cannot wipe the newer document's indicator.
    if (link.current()) setBackgroundStatus('');
  }
}

/** (Re)draws the clickable, risk-coloured link hotspots on every page that has been rendered. */
function drawLinks() {
  for (const pe of pageEls) {
    // Pages that have not rendered yet get their overlay built by renderPageEl when they do;
    // building it here as well would cost a layout pass per page for nothing.
    if (pe.wrap.isConnected && pe.renderedKey) buildLinkLayer(pe, Number(pe.wrap.dataset.page));
  }
}

// Link lookups are memoised on the identity of the arrays they index (both are replaced wholesale,
// never mutated), because the overlay is rebuilt per page: scanning every hotspot and every verdict
// for each page made drawing a link-heavy document quadratic in the number of links.
let hotspotsByPage = { source: null, map: new Map() };
let verdictsByKey = { source: null, map: new Map() };

/** The drawable hotspots on one page. */
function hotspotsOnPage(pageNum) {
  const list = state.linkHotspots ?? [];
  if (hotspotsByPage.source !== list) {
    const map = new Map();
    for (const l of list) {
      if (!(l.width > 0 && l.height > 0)) continue;
      const onPage = map.get(l.page);
      if (onPage) onPage.push(l);
      else map.set(l.page, [l]);
    }
    hotspotsByPage = { source: list, map };
  }
  return hotspotsByPage.map.get(pageNum) ?? [];
}

/** The risk verdict for one link, or undefined when it has not been rated. */
function verdictForLink(page, url) {
  const list = state.urlVerdicts ?? [];
  if (verdictsByKey.source !== list) {
    const map = new Map();
    for (const v of list) map.set(`${v.page}|${v.url}`, v);
    verdictsByKey = { source: list, map };
  }
  return verdictsByKey.map.get(`${page}|${url}`);
}

/** Lays link hotspots over one page, each tinted + dotted by its risk rating. */
function buildLinkLayer(pe, pageNum) {
  pe.wrap.querySelector('.link-layer')?.remove();
  const links = hotspotsOnPage(pageNum);
  if (links.length === 0) return;
  // Measure the page image once: the removal above invalidates layout, so reading it per hotspot
  // forces a reflow per link and makes a link-heavy page take seconds to draw.
  const size = { w: pe.img.clientWidth, h: pe.img.clientHeight };
  const layer = document.createElement('div');
  layer.className = 'link-layer';
  for (const link of links) {
    const css = pdfRectToCss(
      pageNum, pe.img, { x: link.x, y: link.y, width: link.width, height: link.height }, size);
    if (css.width <= 0 || css.height <= 0) continue;
    const isUri = link.kind === 'uri' && !!link.url;
    const verdict = isUri ? verdictForLink(link.page, link.url) : null;
    const level = isUri ? (verdict?.level ?? 'unknown') : 'unknown';
    // A web link becomes a real, clickable anchor only once the user has enabled links; until then
    // (and for in-document actions) it's shown, risk-coloured, but inert — never auto-navigable.
    const navigable = isUri && state.keepLinks;
    const el = document.createElement(navigable ? 'a' : 'div');
    el.className = `link-hotspot risk-${level}${!navigable && isUri ? ' link-inert' : ''}`;
    if (navigable) {
      el.href = link.url; // set as a property, never interpolated into HTML
      el.target = '_blank';
      el.rel = 'noreferrer nofollow';
    }
    el.style.cssText = `left:${css.left}px;top:${css.top}px;width:${css.width}px;height:${css.height}px;`;
    // Rollover popup: the URL + risk for web links, or the action kind for the rest.
    el.addEventListener('mouseenter', () => showLinkPopup(el, link, level, verdict, navigable));
    el.addEventListener('mouseleave', hideLinkPopup);
    const dot = document.createElement('span');
    dot.className = `link-risk-dot ${level}`;
    el.appendChild(dot);
    layer.appendChild(el);
  }
  pe.wrap.insertBefore(layer, pe.overlay); // above the text layer, below the tool overlay
}

/** The risk-line text shown in the link rollover popup. */
function linkRiskLabel(isUri, level, verdict) {
  if (!isUri) return '● In-document action — opens nothing on the web';
  if (level === 'unknown') return '● Not rated';
  const category = verdict?.category ? ` · ${verdict.category}` : '';
  const source = verdict?.source === 'cloudflare' ? ' (Cloudflare)' : '';
  return `● ${level.toUpperCase()}${category}${source}`;
}

/** Shows the rollover popup with a link's URL/action and risk rating, positioned near the hotspot. */
function showLinkPopup(anchor, link, level, verdict, navigable) {
  const pop = $('link-popup');
  pop.innerHTML = '';
  const isUri = link.kind === 'uri' && !!link.url;
  const urlEl = document.createElement('div');
  urlEl.className = 'lp-url';
  // textContent — URL / action label is document data, never HTML.
  urlEl.textContent = isUri ? link.url : (LINK_KIND_LABELS[link.kind] ?? 'Link');
  pop.appendChild(urlEl);
  const risk = document.createElement('div');
  risk.className = `lp-risk ${level}`;
  risk.textContent = linkRiskLabel(isUri, level, verdict);
  pop.appendChild(risk);
  if (isUri && !navigable) {
    const note = document.createElement('div');
    note.className = 'lp-note';
    note.textContent = 'Disabled — enable links to open';
    pop.appendChild(note);
  }

  placePopupNear(pop, anchor);
}

/** Positions the shared popup just above (or below) an anchor element, kept on-screen. */
function placePopupNear(pop, anchor) {
  pop.hidden = false;
  const r = anchor.getBoundingClientRect();
  const pw = pop.offsetWidth, ph = pop.offsetHeight;
  let y = r.top - ph - 6;          // prefer above...
  if (y < 4) y = r.bottom + 6;     // ...otherwise below
  const x = Math.min(Math.max(4, r.left), window.innerWidth - pw - 6);
  pop.style.left = `${x}px`;
  pop.style.top = `${y}px`;
}

function hideLinkPopup() { $('link-popup').hidden = true; }

// ------------------------------------------------------- form-field markers

const FIELD_ICONS = {
  text: 'abc', multiline: '¶', checkbox: '☑', choice: '▾', radio: '◉',
  button: 'btn', signature: '✍',
};

/** Fetches the document's form fields (with their on-page rectangles) and outlines them. */
async function refreshFormFields() {
  try {
    const result = await host.call('form-fields', { pdf: state.pdfB64, pdfPassword: state.password });
    state.formFields = result.fields ?? [];
  } catch (e) {
    // The named case in #72: an empty list makes the panel say "This document has no fillable
    // form fields", which is a claim about the document, not about a failed call. The panel's
    // behaviour is unchanged (there is nothing to fill either way) but the reason is now on
    // record instead of being invented.
    activity.add('error', 'form fields could not be listed',
      `${e?.message ?? e} — the document will appear to have no fillable fields`);
    state.formFields = [];
  }
  drawFormFields();
}

function drawFormFields() {
  for (const pe of pageEls) {
    if (pe.wrap.isConnected) buildFieldLayer(pe, Number(pe.wrap.dataset.page));
  }
}

/**
 * Builds the real, editable control for a field so it can be filled in place on the page — the
 * way a native PDF viewer presents it — rather than only from the side panel. Returns null for a
 * type with no on-page control.
 */
function fieldControl(field) {
  if (field.type === 'button') {
    // Left deliberately empty and transparent: the page image already draws the button's own
    // appearance stream (its caption, border and fill), so this only needs to catch the click.
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'field-input field-input-button';
    button.setAttribute('aria-label', field.name || 'Button');
    button.title = field.script ? "Runs this button's script" : 'This button has no script attached';
    button.addEventListener('click', (e) => { e.stopPropagation(); runFormButtonScript(field); });
    return button;
  }

  let input;
  if (field.type === 'checkbox') {
    input = document.createElement('input');
    input.type = 'checkbox';
    input.dataset.on = (field.options || []).find((o) => o && o !== 'Off') || 'Yes';
    input.checked = !!field.value && field.value !== 'Off';
  } else if ((field.type === 'choice' || field.type === 'radio') && field.options?.length) {
    input = document.createElement('select');
    for (const o of field.options.filter((o) => field.type !== 'radio' || o !== 'Off')) {
      const opt = document.createElement('option');
      opt.value = o;
      opt.textContent = o;
      input.appendChild(opt);
    }
    input.value = field.value;
  } else {
    input = document.createElement('input');
    input.type = 'text';
    input.value = field.value ?? '';
  }
  input.className = 'field-input';
  input.dataset.pageField = field.name;
  if (field.readOnly) input.disabled = true;
  const read = () => (input.type === 'checkbox'
    ? (input.checked ? (input.dataset.on ?? 'Yes') : 'Off')
    : input.value);
  input.addEventListener('input', () => setFieldValue(field.name, read(), input));
  input.addEventListener('change', () => {
    setFieldValue(field.name, read(), input);
    // A field's own script fires on activation in a real reader; mirror that here.
    if (field.script) runFormButtonScript(field);
  });
  input.addEventListener('click', (e) => e.stopPropagation()); // don't start a page-tool drag
  return input;
}

/**
 * Records a field's new value and mirrors it into whichever of the two views didn't originate it
 * (the on-page control and the side panel's row), so both always agree and Apply sees the edit
 * regardless of where it was made.
 */
function setFieldValue(name, value, source) {
  const field = (state.formFields ?? []).find((f) => f.name === name);
  if (field) field.value = value;
  for (const el of document.querySelectorAll(
    `[data-page-field="${CSS.escape(name)}"], #forms-list [data-field="${CSS.escape(name)}"]`)) {
    if (el === source) continue;
    if (el.type === 'checkbox') el.checked = !!value && value !== 'Off';
    else el.value = value;
  }
}

/** Renders each form field on one page as an editable control positioned over the page image. */
function buildFieldLayer(pe, pageNum) {
  pe.wrap.querySelector('.field-layer')?.remove();
  const fields = (state.formFields ?? []).filter((f) => f.page === pageNum && f.width > 0 && f.height > 0);
  if (fields.length === 0) return;
  const layer = document.createElement('div');
  layer.className = 'field-layer';
  for (const field of fields) {
    const css = pdfRectToCss(pageNum, pe.img, { x: field.x, y: field.y, width: field.width, height: field.height });
    if (css.width <= 0 || css.height <= 0) continue;
    const marker = document.createElement('div');
    marker.className = 'field-marker';
    marker.style.cssText = `left:${css.left}px;top:${css.top}px;width:${css.width}px;height:${css.height}px;`;
    const tag = document.createElement('span');
    tag.className = 'field-tag';
    tag.textContent = FIELD_ICONS[field.type] ?? '▭';
    marker.appendChild(tag);
    const control = fieldControl(field);
    if (control) marker.appendChild(control);
    marker.addEventListener('mouseenter', () => showFieldPopup(marker, field));
    marker.addEventListener('mouseleave', hideLinkPopup);
    layer.appendChild(marker);
  }
  pe.wrap.insertBefore(layer, pe.overlay); // above the text/link layers, below the tool overlay
}

/** Rollover popup for a form field: its name, type, and current value. */
function showFieldPopup(marker, field) {
  const pop = $('link-popup');
  pop.innerHTML = '';
  const name = document.createElement('div');
  name.className = 'lp-url';
  name.textContent = field.name || '(unnamed field)'; // textContent — document data, never HTML
  pop.appendChild(name);
  const meta = document.createElement('div');
  meta.className = 'lp-risk unknown';
  const value = (field.value ?? '').trim();
  const truncated = value.length > 60 ? value.slice(0, 60) + '…' : value;
  meta.textContent = `${field.type}${field.readOnly ? ' · read-only' : ''} · ` +
    (value ? `“${truncated}”` : 'empty');
  pop.appendChild(meta);
  placePopupNear(pop, marker);
}

async function openLinks() {
  const link = beginLinkRun('panel');
  try {
    setStatus(FINDING_LINKS, true);
    const result = await host.call('list-urls', { pdf: state.pdfB64, pdfPassword: state.password });
    // The document may have been replaced while the list was being fetched; filling the panel with
    // the previous document's URLs is #24 all over again, so drop the answer instead.
    if (link.stale()) { clearBusyStatus(FINDING_LINKS); return; }
    setStatus('');
    state.links = result.links ?? [];
    $('links-enable').checked = state.keepLinks;
    showPanel('panel-links');
    if (state.urlVerdicts.length === 0) await scanLinks(link);
    else { renderLinks(); drawLinks(); }
  } catch (e) {
    if (link.stale()) { clearBusyStatus(FINDING_LINKS); return; }
    fail(e);
  }
}

function renderLinks() {
  const list = $('links-list');
  list.innerHTML = '';
  const has = state.links.length > 0;
  $('links-empty').hidden = has;
  $('links-enable-row').hidden = !has;
  $('links-hint').hidden = !has || state.keepLinks;
  $('links-rescan').hidden = !has || !state.keepLinks;

  for (const link of state.links) {
    const li = document.createElement('li');
    if (!state.keepLinks) li.className = 'link-disabled';
    const row = document.createElement('div');
    row.className = 'link-row';
    const verdict = state.keepLinks ? verdictForLink(link.page, link.url) : null;

    const dot = document.createElement('span');
    dot.className = `link-dot ${verdict ? verdict.level : 'unknown'}`;
    const body = document.createElement('div');
    body.className = 'link-url';
    // Show the URL as inert text unless links are enabled; never auto-navigate.
    if (state.keepLinks) {
      const a = document.createElement('a');
      a.href = link.url;
      a.target = '_blank';
      a.rel = 'noreferrer nofollow';
      a.textContent = link.url;
      body.appendChild(a);
    } else {
      body.textContent = link.url;
    }
    const meta = document.createElement('div');
    meta.className = 'link-meta';
    let verdictSuffix = '';
    if (verdict) {
      const cloudflareTag = verdict.source === 'cloudflare' ? ' (Cloudflare)' : '';
      verdictSuffix = ` · ${verdict.level.toUpperCase()} · ${verdict.category}${cloudflareTag}`;
    }
    meta.textContent = `page ${link.page}${verdictSuffix}`;
    body.appendChild(meta);
    if (verdict?.detail) { body.title = verdict.detail; }
    row.append(dot, body);
    li.appendChild(row);
    list.appendChild(li);
  }
}

/** Rates the panel's URLs. `link` continues an existing run (from openLinks); otherwise a new one. */
async function scanLinks(link = beginLinkRun('panel')) {
  if (state.links.length === 0) { renderLinks(); return; }
  const creds = await chrome.storage.local.get({ cfAccountId: '', cfApiToken: '' });
  if (link.stale()) return;
  const usingCf = !!(creds.cfAccountId && creds.cfApiToken);
  const busyText = usingCf ? 'Scanning links with Cloudflare…' : 'Rating links…';
  try {
    setStatus(busyText, true);
    const result = await host.call('scan-urls', {
      pdf: state.pdfB64, pdfPassword: state.password,
      cfAccountId: creds.cfAccountId, cfApiToken: creds.cfApiToken,
    });
    // Verdicts for a document that is no longer open must not colour the one that is.
    if (link.stale()) { clearBusyStatus(busyText); return; }
    state.urlVerdicts = result.verdicts ?? [];
    setStatus('');
    renderLinks();
    drawLinks();
    if (!usingCf) toast('Rated links offline. Add a Cloudflare token in Options for live scanning.');
  } catch (e) {
    if (link.stale()) { clearBusyStatus(busyText); return; }
    fail(e);
    renderLinks();
  }
}

async function toggleLinks() {
  state.keepLinks = $('links-enable').checked;
  if (state.keepLinks && state.urlVerdicts.length === 0) await scanLinks();
  else renderLinks();
  drawLinks();      // enabling/disabling flips the on-page hotspots between clickable and inert
  updateChrome();
}

/**
 * Hands the current document to the browser's own print flow so its print dialog — including the
 * "Save as PDF" destination — prints the real, vector PDF. The PDF is loaded into an off-screen
 * iframe whose print is triggered directly; if the browser won't print the embedded PDF
 * programmatically (or never loads it), it's opened in a new tab so the user can print from the
 * browser's built-in PDF viewer.
 */
function printDocument() {
  if (!state.pdf) return;
  const url = URL.createObjectURL(new Blob([state.pdf], { type: 'application/pdf' }));
  let handled = false;

  const frame = document.createElement('iframe');
  frame.setAttribute('aria-hidden', 'true');
  frame.style.cssText = 'position:fixed;right:0;bottom:0;width:1px;height:1px;border:0;opacity:0;';

  const cleanup = () => setTimeout(() => { URL.revokeObjectURL(url); frame.remove(); }, 60000);
  const openInTab = () => {
    if (handled) return;
    handled = true;
    window.open(url, '_blank', 'noopener');
    toast('Opened the document in a new tab — use your browser to print or save as PDF.');
    cleanup();
  };

  frame.addEventListener('load', () => {
    if (handled) return;
    handled = true;
    try {
      frame.contentWindow.focus();
      frame.contentWindow.print();
      toast('Opening the browser print dialog…');
      cleanup();
    } catch (e) {
      activity.add('warn', 'in-page printing was blocked',
        `${e?.message ?? e} — opening the document in a new tab instead`);
      handled = false; // let the tab fallback take over
      openInTab();
    }
  });
  // If the embedded PDF never loads (some browsers block plugin printing), fall back to a tab.
  setTimeout(openInTab, 3000);

  frame.src = url;
  document.body.appendChild(frame);
}

async function save() {
  let bytes = state.pdf;
  activity.add('info', 'saving document', state.fileName);
  await state.safetyScan; // what to strip is only known once the scan of this version is in
  const stripJs = state.safety?.javaScriptCount > 0 && !state.keepActiveContent;
  // URL scanning is off for now: leave link URLs untouched on save.
  const stripUrls = URL_SCANNING_ENABLED && state.safety?.urlCount > 0 && !state.keepLinks;
  // Strip embedded JavaScript and/or link URLs unless the user chose to keep them.
  if (stripJs || stripUrls) {
    try {
      setStatus('Removing active content…', true);
      const stripped = await host.call('strip-active', {
        pdf: state.pdfB64, javaScript: stripJs, urls: stripUrls, pdfPassword: state.password,
      });
      bytes = base64ToBytes(stripped.pdf);
      setStatus('');
    } catch (e) {
      fail(e);
      return;
    }
  }
  const suggested = state.fileName.replace(/\.pdf$/i, '') + '-edited.pdf';
  const blob = new Blob([bytes], { type: 'application/pdf' });
  try {
    if (window.showSaveFilePicker) {
      const handle = await window.showSaveFilePicker({
        suggestedName: suggested,
        types: [{ description: 'PDF document', accept: { 'application/pdf': ['.pdf'] } }],
      });
      const writable = await handle.createWritable();
      await writable.write(blob);
      await writable.close();
      activity.add('info', 'document saved', handle.name);
      toast(`Saved ${handle.name}.`);
      return;
    }
  } catch (e) {
    if (e.name === 'AbortError') {
      activity.add('info', 'save cancelled', state.fileName);
      return;
    }
    // fall through to the downloads API
    activity.add('warn', 'the save dialog failed',
      `${e?.message ?? e} — falling back to the downloads API`);
  }
  chrome.downloads.download({
    url: URL.createObjectURL(blob),
    filename: suggested,
    saveAs: true,
  });
  activity.add('info', 'document saved via downloads', suggested);
  toast('Saving via downloads…');
}

/** Snapshot of the current working document, for the undo/redo stacks. */
function snapshot() {
  return { pdf: state.pdf, pdfB64: state.pdfB64, info: state.info, password: state.password };
}

/** Restores a previously captured snapshot and re-renders. */
async function restore(snap, message) {
  setWorkingPdf(snap.pdf, snap.pdfB64);
  state.info = snap.info;
  state.password = snap.password;
  state.page = Math.min(state.page, state.info.pageCount);
  state.regions = state.regions.filter((r) => r.page <= state.info.pageCount);
  state.signatures = [];
  state.safety = null;
  await showDocument();
  updateChrome();
  startScans().then(updateChrome);
  toast(message);
}

function undo() {
  const previous = state.history.pop();
  if (!previous) return;
  state.future.push(snapshot());       // remember where we were so Redo can return
  restore(previous, 'Undid last change.');
}

function redo() {
  const next = state.future.pop();
  if (!next) return;
  state.history.push(snapshot());
  if (state.history.length > 10) state.history.shift();
  restore(next, 'Redid change.');
}

// ------------------------------------------------------------------ wiring

function wire() {
  $('btn-open').addEventListener('click', openFilePicker);
  $('btn-open-empty').addEventListener('click', openFilePicker);
  $('btn-save').addEventListener('click', save);
  $('btn-print').addEventListener('click', printDocument);
  $('btn-undo').addEventListener('click', undo);
  $('btn-redo').addEventListener('click', redo);
  $('btn-sidebar').addEventListener('click', () => toggleSidebar());

  $('btn-rotate-left').addEventListener('click', () => rotateCurrentPage(-90));
  $('btn-rotate-right').addEventListener('click', () => rotateCurrentPage(90));

  $('page-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); jumpToTypedPage(); }
  });
  $('page-input').addEventListener('blur', () => {
    $('page-input').value = String(state.page); // discard an unsubmitted edit
  });

  $('tool-select').addEventListener('click', () => setTool('select'));
  $('tool-text').addEventListener('click', () => setTool('text'));
  $('tool-draw').addEventListener('click', () => setTool('draw'));
  $('tool-highlight').addEventListener('click', () => setTool('highlight'));
  $('tool-edit').addEventListener('click', () => setTool('edit'));
  $('tool-move').addEventListener('click', () => setTool('move'));
  $('tool-redact').addEventListener('click', () => setTool('redact'));
  $('tool-sign').addEventListener('click', () => setTool('sign'));

  // The live selection is tinted in the chosen colour, so a sweep previews what it will stamp.
  $('highlight-color').addEventListener('input', () => {
    state.highlightColor = $('highlight-color').value;
    pagesEl.style.setProperty('--sweep', state.highlightColor);
  });
  for (const radio of document.querySelectorAll('input[name="highlight-mode"]')) {
    radio.addEventListener('change', () => {
      if (!radio.checked) return;
      state.highlightMode = radio.value;
      activity.add('info', 'highlight mode', radio.value);
      applyHighlightMode();
    });
  }
  $('highlight-done').addEventListener('click', () => setTool('select'));

  $('draw-color').addEventListener('input', () => { state.drawColor = $('draw-color').value; redrawInk(); });
  $('draw-width').addEventListener('input', () => {
    state.drawWidth = Number.parseFloat($('draw-width').value) || 2.5; redrawInk();
  });
  $('draw-apply').addEventListener('click', applyDrawing);
  $('draw-clear').addEventListener('click', clearDrawing);
  $('draw-cancel').addEventListener('click', () => setTool('select'));

  $('btn-forms').addEventListener('click', openForms);
  $('forms-apply').addEventListener('click', applyForms);
  $('forms-cancel').addEventListener('click', () => hidePanels());
  $('field-place').addEventListener('click', beginPlaceField);
  $('field-type').addEventListener('change', updateFieldTypeRows);
  updateFieldTypeRows(); // sync the type-specific rows to the initial selection, not just on change
  enableCodeEditorTab($('field-script'));

  $('btn-organize').addEventListener('click', openOrganize);
  $('organize-apply').addEventListener('click', applyOrganize);
  $('organize-reset').addEventListener('click', openOrganize); // rebuild the original order
  $('organize-cancel').addEventListener('click', () => hidePanels());

  $('btn-js').addEventListener('click', openJavaScript);
  $('js-add').addEventListener('click', addScript);
  $('js-clear').addEventListener('click', () => {
    $('js-name').value = ''; $('js-source').value = '';
    for (const el of $('js-list').querySelectorAll('.organize-item')) el.classList.remove('active');
  });
  $('js-close').addEventListener('click', () => $('js-dialog').close());
  enableCodeEditorTab($('js-source'));

  $('btn-sanitize').addEventListener('click', openSanitize);
  $('sanitize-apply').addEventListener('click', applySanitize);
  $('sanitize-cancel').addEventListener('click', () => hidePanels());

  $('btn-compare').addEventListener('click', openCompare);
  $('compare-pick').addEventListener('click', pickCompareFile);
  $('compare-close').addEventListener('click', () => hidePanels());

  $('btn-ocr').addEventListener('click', runOcr);

  $('btn-links').hidden = !URL_SCANNING_ENABLED; // URL scanning disabled for now
  $('btn-links').addEventListener('click', openLinks);
  $('links-enable').addEventListener('change', toggleLinks);
  $('links-rescan').addEventListener('click', () => scanLinks());
  $('links-close').addEventListener('click', () => hidePanels());

  $('btn-find').addEventListener('click', findReplace);
  $('btn-merge').addEventListener('click', mergeFiles);
  $('btn-protect').addEventListener('click', protect);
  $('btn-decrypt').addEventListener('click', removeEncryption);
  $('btn-watermark').addEventListener('click', addWatermark);
  $('btn-bates').addEventListener('click', addBates);
  $('btn-flatten').addEventListener('click', flattenDocument);
  $('btn-digital').addEventListener('click', digitallySign);

  $('btn-prev').addEventListener('click', () => goToPage(state.page - 1));
  $('btn-next').addEventListener('click', () => goToPage(state.page + 1));
  $('btn-zoom-in').addEventListener('click', () => setZoom(state.zoom + 0.25));
  $('btn-zoom-out').addEventListener('click', () => setZoom(state.zoom - 0.25));

  $('redact-preview').addEventListener('click', previewRedaction);
  $('redact-apply').addEventListener('click', () => applyRedaction());
  $('redact-clear').addEventListener('click', () => { state.regions = []; drawRegions(); });
  // Privacy intensity: 0 exact, 1 merge adjacent boxes, 2 round widths, 3 full line, 4 textured.
  // The slider governs every marked area, not just the ones drawn after it moves. Snapshotting the
  // level at mark time left the common order of work — mark the areas, then decide how private the
  // boxes should be — applying the old, weaker level while the control read the new one, so the
  // boxes came back wrapping the text exactly and the merging and rounding appeared not to work.
  $('redact-intensity').addEventListener('input', (e) => {
    const level = Number(e.target.value);
    $('redact-intensity-label').textContent = REDACT_INTENSITY[level] ?? 'Exact';
    for (const r of state.regions) r.intensity = level;
    drawRegions();
  });
  $('redact-search-btn').addEventListener('click', searchAndMarkRedactions);
  $('redact-search-text').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); searchAndMarkRedactions(); }
  });

  $('edit-apply').addEventListener('click', applyTextEdit);
  $('edit-cancel').addEventListener('click', () => { hidePanels(); setTool('select'); });
  $('edit-bold').addEventListener('click', () => $('edit-bold').classList.toggle('active'));
  $('edit-italic').addEventListener('click', () => $('edit-italic').classList.toggle('active'));

  $('sign-tab-draw').addEventListener('click', () => {
    $('sign-tab-draw').classList.add('active');
    $('sign-tab-upload').classList.remove('active');
    $('sign-draw').hidden = false;
    $('sign-upload').hidden = true;
  });
  $('sign-tab-upload').addEventListener('click', () => {
    $('sign-tab-upload').classList.add('active');
    $('sign-tab-draw').classList.remove('active');
    $('sign-draw').hidden = true;
    $('sign-upload').hidden = false;
  });
  $('sign-apply').addEventListener('click', applyImageSignature);
  $('sign-cancel').addEventListener('click', () => { hidePanels(); setTool('select'); });

  initMenus();
  initConsole();
  initSignaturePad();
  initDragDrop();
  window.addEventListener('resize', drawRegions);
}

/** Wires the Reading/Editing dropdown menus: click the trigger to toggle, click away to close. */
function initMenus() {
  for (const menu of document.querySelectorAll('.menu-group')) {
    const trigger = menu.querySelector('.menu-trigger');
    trigger.addEventListener('click', (e) => {
      e.stopPropagation();
      const open = menu.classList.contains('open');
      closeAllMenus();
      if (!open) { menu.classList.add('open'); trigger.setAttribute('aria-expanded', 'true'); }
    });
    // Choosing an item runs its own handler and then closes the menu.
    for (const item of menu.querySelectorAll('.menu-item')) {
      item.addEventListener('click', () => closeAllMenus());
    }
  }
  document.addEventListener('click', closeAllMenus);
}

function closeAllMenus() {
  for (const menu of document.querySelectorAll('.menu-group.open')) {
    menu.classList.remove('open');
    menu.querySelector('.menu-trigger')?.setAttribute('aria-expanded', 'false');
  }
}

/** Condenses the (long) user-agent string to the browser + version, for bug reports. */
function browserSummary() {
  const ua = navigator.userAgent || '';
  const m = /(Edg|Chrome|Chromium)\/([\d.]+)/.exec(ua);
  if (!m) return ua || 'unknown';
  const name = m[1] === 'Edg' ? 'Edge' : m[1];
  return `${name} ${m[2]}`;
}

/** #103: shows version, build commit and build time, plus the browser, in a modal. */
async function showAbout() {
  const manifest = chrome.runtime.getManifest();
  const meta = await loadBuildMeta((p) => chrome.runtime.getURL(p));
  const info = formatBuildInfo({
    version: manifest.version,
    commit: meta.commit,
    builtAt: meta.builtAt,
  });

  // Static markup via innerHTML, every dynamic value via textContent — see #73/#74.
  modal.innerHTML = '<h2>About</h2>';

  const name = document.createElement('p');
  name.className = 'about-name';
  name.textContent = manifest.name || 'reDACT PDF Editor';
  modal.appendChild(name);

  const tagline = document.createElement('p');
  tagline.className = 'tagline';
  tagline.textContent = 'Cut the fat. Keep the signal.';
  modal.appendChild(tagline);

  if (manifest.description) {
    const desc = document.createElement('p');
    desc.className = 'muted';
    desc.textContent = manifest.description;
    modal.appendChild(desc);
  }

  const dl = document.createElement('dl');
  dl.className = 'about-list';
  const row = (label, value, mono = false) => {
    const dt = document.createElement('dt');
    dt.textContent = label;
    const dd = document.createElement('dd');
    dd.textContent = value;
    if (mono) dd.classList.add('about-mono');
    dl.append(dt, dd);
  };
  row('Version', info.version);
  row('Build', info.commit, info.isRelease);
  row('Built', info.builtAt);
  row('Browser', browserSummary());
  modal.appendChild(dl);

  const actions = document.createElement('div');
  actions.className = 'actions';
  const close = document.createElement('button');
  close.textContent = 'Close';
  actions.appendChild(close);
  modal.appendChild(actions);

  modal.showModal();
  close.addEventListener('click', () => modal.close());
  close.focus();
}

/**
 * Paints one guide — a missing host or a mismatched one, both carry the same {headline, detail,
 * steps} shape — into the empty state's host line: what is wrong, and the first step towards
 * fixing it, with the full instructions one click away in the options page.
 * Built from DOM nodes only; the browser's error string is untrusted text (#74).
 */
function renderHostGuide(statusEl, guide, buttonLabel) {
  statusEl.replaceChildren();
  const headline = document.createElement('strong');
  headline.textContent = `⚠ ${guide.headline}`;
  statusEl.appendChild(headline);
  statusEl.appendChild(document.createElement('br'));
  statusEl.appendChild(document.createTextNode(guide.detail));

  const first = guide.steps[0];
  if (first) {
    statusEl.appendChild(document.createElement('br'));
    statusEl.appendChild(document.createTextNode(first.text));
    if (first.code) {
      const pre = document.createElement('pre');
      pre.className = 'host-help-code';
      pre.textContent = first.code;
      statusEl.appendChild(pre);
    }
  }

  const openOptions = document.createElement('button');
  openOptions.type = 'button';
  openOptions.textContent = buttonLabel;
  openOptions.addEventListener('click', () => chrome.runtime.openOptionsPage());
  statusEl.appendChild(openOptions);
}

/**
 * Fills the empty state's host line in. Without the host nothing in this page can do any work, so
 * when it is unreachable this says which of the three things went wrong — never installed,
 * installed but not allowed to talk to this extension, or installed but crashing — and when it is
 * reachable but the wrong version, says that instead of a reassuring tick.
 */
async function reportHostStatus() {
  const statusEl = $('host-status');
  const probe = await probeHost(host);

  if (probe.state === HOST_STATE.CONNECTED) {
    // Connected is not the same as compatible. A host from the previous release answers `ping` and
    // then fails, or silently no-ops, on any action added since it was built — so the user would
    // see "✓ connected" here and a feature that does nothing there. Name the mismatch instead.
    const versionGuide = hostVersionGuide({
      hostVersion: probe.version,
      extensionVersion: chrome.runtime.getManifest().version,
      platform: detectPlatform(navigator.userAgent),
    });
    if (!versionGuide) {
      statusEl.textContent = '✓ Native host connected.';
      return;
    }
    for (const line of hostVersionGuideLines(versionGuide)) {
      activity.add('warn', 'native host', line);
    }
    renderHostGuide(statusEl, versionGuide, 'Full update instructions…');
    return;
  }

  const guide = hostInstallGuide({
    state: probe.state,
    platform: detectPlatform(navigator.userAgent),
    extensionId: chrome.runtime.id,
    error: probe.error,
  });
  // Everything the guide says also goes into the activity log, so a downloaded log is enough for
  // someone else to see what the user was told.
  for (const line of hostInstallGuideLines(guide)) activity.add('warn', 'native host', line);

  renderHostGuide(statusEl, guide, 'Full install instructions…');
}

async function start() {
  wire();
  await restoreConsoleState();
  activity.add('info', 'viewer started');
  await reportHostStatus();
  const src = new URLSearchParams(location.search).get('src');
  if (src) await openFromUrl(src);
}

await start();
