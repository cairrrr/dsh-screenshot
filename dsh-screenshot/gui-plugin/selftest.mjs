// selftest.mjs — run the plugin outside DSH with a stubbed ctx.
//
// It registers the plugin's routes on a throwaway local HTTP server and exercises every
// one of them for real, so the plugin can be verified *before* it is installed into the
// DSH profile. Needs the resident shot service (shotd.exe) running for the capture test.
//
//   node gui-plugin/selftest.mjs
//
// Exit code 0 = all checks passed.

import http from 'node:http'
import { pathToFileURL } from 'node:url'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const plugin = await import(pathToFileURL(path.join(HERE, 'lib', 'index.js')).href)

const routes = new Map()
let tapIndex = null
const disposers = []

const ctx = {
  webServer: {
    register(spec) {
      routes.set(spec.path, spec.handler)
      return () => routes.delete(spec.path)
    },
    tapIndex(fn) {
      tapIndex = fn
      return () => { tapIndex = null }
    },
  },
  effect(fn) {
    const d = fn()
    if (typeof d === 'function') disposers.push(d)
  },
}

plugin.apply(ctx)

const checks = []
function check(label, ok, detail) {
  checks.push({ label, ok })
  console.log((ok ? 'PASS  ' : 'FAIL  ') + label + (detail ? '   ' + detail : ''))
}

console.log('plugin name   :', plugin.name)
console.log('plugin inject :', JSON.stringify(plugin.inject))
console.log('routes        :', [...routes.keys()].join(', '))
console.log('')

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1')
  const handler = routes.get(url.pathname)
  if (!handler) {
    res.writeHead(404, { 'Content-Type': 'text/plain' })
    res.end('no route')
    return
  }
  handler(req, res)
})

await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
const base = 'http://127.0.0.1:' + server.address().port

// 1) client.js is served from disk, uncached
{
  const res = await fetch(base + '/dsh-shot/client.js')
  const text = await res.text()
  check('client.js served',
    res.status === 200 && text.indexOf('__dshShotButtonLoaded') !== -1,
    res.status + '  ' + text.length + ' bytes')
  check('client.js no-store', res.headers.get('cache-control') === 'no-store')
}

// 2) diagnostics route
{
  const res = await fetch(base + '/dsh-shot/status')
  const json = await res.json()
  check('status route', res.status === 200 && json.plugin === 'dsh-shot-button',
    'port=' + json.config.port + ' clientBytes=' + json.clientBytes)
}

// 3) index.html injection
{
  const html = tapIndex('<html><body><div id="app"></div></body></html>')
  check('tapIndex injects client script', html.indexOf('/dsh-shot/client.js') !== -1)
  check('tapIndex injects config', html.indexOf('__DSH_SHOT_CONFIG__') !== -1)
  check('tapIndex is idempotent', tapIndex(html) === html)
}

// 4) capture — the real thing
{
  const res = await fetch(base + '/dsh-shot/capture?mode=Full')
  const buf = Buffer.from(await res.arrayBuffer())
  const isPng = buf.length > 8 && buf.slice(1, 4).toString() === 'PNG'
  const w = isPng ? buf.readUInt32BE(16) : 0
  const h = isPng ? buf.readUInt32BE(20) : 0
  check('capture returns a PNG',
    res.status === 200 && isPng,
    res.status + '  ' + w + 'x' + h + '  ' + buf.length + ' bytes')
  check('capture names the file', !!res.headers.get('x-shot-name'), res.headers.get('x-shot-name'))
  check('capture reports its source', !!res.headers.get('x-shot-source'), res.headers.get('x-shot-source'))
}

// 5) unknown mode falls back to Full instead of erroring
{
  const res = await fetch(base + '/dsh-shot/capture?mode=../../etc/passwd')
  const buf = Buffer.from(await res.arrayBuffer())
  check('bogus mode falls back to Full', res.status === 200 && buf.slice(1, 4).toString() === 'PNG')
}

// 6) teardown
{
  for (const d of disposers) d()
  check('disposers unregister everything', routes.size === 0 && tapIndex === null)
}

server.close()

const failed = checks.filter((c) => !c.ok)
console.log('')
console.log(failed.length === 0
  ? 'all ' + checks.length + ' checks passed'
  : failed.length + ' of ' + checks.length + ' checks FAILED')
process.exit(failed.length === 0 ? 0 : 1)
