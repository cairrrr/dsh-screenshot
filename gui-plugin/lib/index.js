// dsh-shot-button — server side (cordis plugin, runs inside the DSH web process)
//
// Job: inject a small client script into the GUI's index.html, serve that script, and
// proxy capture requests to the resident screenshot service (shotd.exe) on loopback.
//
// The two routes that matter:
//   GET /dsh-shot/client.js         the injected browser script (re-read from disk every
//                                   request, so editing it only needs a page refresh)
//   GET /dsh-shot/capture?mode=...  -> image/png of a fresh screenshot, or 204 if the
//                                   region drag was cancelled
//
// config.json and lib/client.js are read per request on purpose: this file itself is
// cached by the ESM loader (changing it needs a dsh web restart), but everything it
// serves can be changed live.

import http from 'node:http'
import { spawn } from 'node:child_process'
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const name = 'dsh-shot-button'
const inject = ['webServer']

const PACKAGE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const CONFIG_FILE = path.join(PACKAGE_ROOT, 'config.json')
const CLIENT_FILE = path.join(PACKAGE_ROOT, 'lib', 'client.js')

const DEFAULTS = {
  port: 38901,
  alwaysInsertReference: false,
  referencePrefix: 'shots/',
  buttonTitle: '截屏并插入（全屏 / 窗口 / 框选）',
  // Fallback capture path used when the resident service is not running.
  // null = derive it from this package's location, i.e. <repo>/tools/shot.exe
  shotExe: null,
}

const MODES = ['Full', 'Window', 'Region']

function readConfig() {
  let cfg = { ...DEFAULTS }
  try {
    if (existsSync(CONFIG_FILE)) {
      cfg = { ...cfg, ...JSON.parse(readFileSync(CONFIG_FILE, 'utf8')) }
    }
  } catch (err) {
    // a broken config.json must not take the GUI down
  }
  if (!cfg.shotExe) cfg.shotExe = path.resolve(PACKAGE_ROOT, '..', 'tools', 'shot.exe')
  return cfg
}

/** Ask the resident service for a screenshot. */
function captureViaDaemon(port, mode, timeoutMs, hide) {
  const hideQuery = hide ? '&hide=' + encodeURIComponent(hide) : ''
  return new Promise((resolve, reject) => {
    const req = http.get(
      { host: '127.0.0.1', port, path: '/shot?mode=' + encodeURIComponent(mode) + hideQuery, timeout: timeoutMs },
      (res) => {
        if (res.statusCode === 204) {
          res.resume()
          resolve({ cancelled: true })
          return
        }
        if (res.statusCode !== 200) {
          res.resume()
          reject(new Error('shot service responded HTTP ' + res.statusCode))
          return
        }
        const chunks = []
        res.on('data', (c) => chunks.push(c))
        res.on('end', () => resolve({
          bytes: Buffer.concat(chunks),
          fileName: res.headers['x-shot-name'] || ('shot-' + Date.now() + '.png'),
          source: 'daemon',
        }))
      },
    )
    req.on('timeout', () => req.destroy(new Error('shot service timed out')))
    req.on('error', reject)
  })
}

/** Fallback: run shot.exe once and pick up the file it wrote. */
function captureViaSpawn(exe, mode, timeoutMs) {
  return new Promise((resolve, reject) => {
    if (!exe || !existsSync(exe)) {
      reject(new Error('shot.exe not found: ' + exe))
      return
    }

    const shotsDir = path.join(path.dirname(path.dirname(exe)), 'shots')
    const child = spawn(exe, [mode], { windowsHide: true })
    const timer = setTimeout(() => {
      try { child.kill() } catch (err) { /* ignore */ }
      reject(new Error('shot.exe timed out'))
    }, timeoutMs)

    child.on('error', (err) => { clearTimeout(timer); reject(err) })
    child.on('close', (code) => {
      clearTimeout(timer)
      if (code !== 0) {
        reject(new Error('shot.exe exited with code ' + code))
        return
      }
      try {
        const newest = readdirSync(shotsDir)
          .filter((f) => f.startsWith('shot-') && f.endsWith('.png'))
          .map((f) => ({ f, t: statSync(path.join(shotsDir, f)).mtimeMs }))
          .sort((a, b) => b.t - a.t)[0]
        if (!newest) {
          reject(new Error('no screenshot file appeared in ' + shotsDir))
          return
        }
        resolve({
          bytes: readFileSync(path.join(shotsDir, newest.f)),
          fileName: newest.f,
          source: 'spawn',
        })
      } catch (err) {
        reject(err)
      }
    })
  })
}

function sendJson(res, code, value) {
  const body = Buffer.from(JSON.stringify(value, null, 2), 'utf8')
  res.writeHead(code, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': String(body.length),
    'Cache-Control': 'no-store',
  })
  res.end(body)
}

function sendText(res, code, text) {
  const body = Buffer.from(text, 'utf8')
  res.writeHead(code, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Content-Length': String(body.length),
    'Cache-Control': 'no-store',
  })
  res.end(body)
}

function apply(ctx) {
  const disposers = []

  // --- the injected browser script ------------------------------------------
  disposers.push(ctx.webServer.register({
    kind: 'exact',
    path: '/dsh-shot/client.js',
    handler: (req, res) => {
      try {
        const body = readFileSync(CLIENT_FILE)
        res.writeHead(200, {
          'Content-Type': 'application/javascript; charset=utf-8',
          'Content-Length': String(body.length),
          'Cache-Control': 'no-store',
        })
        res.end(body)
      } catch (err) {
        sendText(res, 500, 'client.js unavailable: ' + String((err && err.message) || err))
      }
    },
  }))

  // --- capture -------------------------------------------------------------
  disposers.push(ctx.webServer.register({
    kind: 'exact',
    path: '/dsh-shot/capture',
    handler: (req, res) => {
      const cfg = readConfig()
      const url = new URL(req.url, 'http://127.0.0.1')
      let mode = url.searchParams.get('mode') || 'Full'
      if (!MODES.includes(mode)) mode = 'Full'
      // "hide the window in front before capturing" is decided by the shot service;
      // this route only forwards the client's request for it.
      const hide = url.searchParams.get('hide')

      captureViaDaemon(cfg.port, mode, 180000, hide)
        .catch((err) => {
          // service not running (or crashed): fall back to a one-shot process
          console.warn('[dsh-shot-button] daemon capture failed, falling back to shot.exe:', String(err && err.message || err))
          return captureViaSpawn(cfg.shotExe, mode, 60000)
        })
        .then((result) => {
          if (result.cancelled) {
            res.writeHead(204, { 'Cache-Control': 'no-store' })
            res.end()
            return
          }
          res.writeHead(200, {
            'Content-Type': 'image/png',
            'Content-Length': String(result.bytes.length),
            'Cache-Control': 'no-store',
            'X-Shot-Name': result.fileName,
            'X-Shot-Source': result.source,
          })
          res.end(result.bytes)
        })
        .catch((err) => {
          sendJson(res, 503, { error: String((err && err.message) || err) })
        })
    },
  }))

  // --- diagnostics ---------------------------------------------------------
  disposers.push(ctx.webServer.register({
    kind: 'exact',
    path: '/dsh-shot/status',
    handler: (req, res) => {
      const cfg = readConfig()
      let clientBytes = 0
      try { clientBytes = readFileSync(CLIENT_FILE).length } catch (err) { /* ignore */ }
      sendJson(res, 200, {
        plugin: name,
        packageRoot: PACKAGE_ROOT,
        config: cfg,
        clientBytes,
        endpoints: ['/dsh-shot/client.js', '/dsh-shot/capture?mode=Full|Window|Region', '/dsh-shot/status'],
      })
    },
  }))

  // --- inject into index.html ----------------------------------------------
  disposers.push(ctx.webServer.tapIndex((html) => {
    if (html.indexOf('/dsh-shot/client.js') !== -1) return html
    const cfg = readConfig()
    const tag =
      '<script>window.__DSH_SHOT_CONFIG__=' + JSON.stringify(cfg).replace(/</g, '\\u003c') + ';</script>' +
      '<script defer src="/dsh-shot/client.js"></script>'
    if (html.indexOf('</body>') !== -1) return html.replace('</body>', tag + '</body>')
    return html + tag
  }))

  ctx.effect(() => () => {
    for (const d of disposers) {
      try { d() } catch (err) { /* ignore */ }
    }
  })
}

export { name, inject, apply }
