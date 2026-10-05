// selftest-client.mjs — run the injected client script against a minimal fake DOM.
//
// This does not need a browser: it checks that the script boots without throwing and
// builds exactly what config.json asks for (two buttons, right ids/colour, three-item
// menu). It catches assembly mistakes — wrong config plumbing, a broken closure, a
// button that never gets positioned — which are otherwise only visible in the GUI.
//
//   node gui-plugin/selftest-client.mjs

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const code = readFileSync(path.join(HERE, 'lib', 'client.js'), 'utf8')
const config = JSON.parse(readFileSync(path.join(HERE, 'config.json'), 'utf8'))

const created = []

function makeEl(tag) {
  const el = {
    tagName: String(tag || 'div').toUpperCase(),
    id: '',
    className: '',
    type: '',
    title: '',
    innerHTML: '',
    textContent: '',
    value: '',
    disabled: false,
    readOnly: false,
    files: null,
    style: {},
    dataset: {},
    children: [],
    appendChild(child) { this.children.push(child); return child },
    addEventListener() {},
    removeEventListener() {},
    setAttribute() {},
    getAttribute() { return null },
    closest() { return null },
    contains() { return false },
    focus() {},
    dispatchEvent() { return true },
    getBoundingClientRect() {
      return { width: 780, height: 120, top: 700, left: 300, right: 1080, bottom: 820, x: 300, y: 700 }
    },
    querySelectorAll() { return [] }
  }
  created.push(el)
  return el
}

const body = makeEl('body')
const head = makeEl('head')
const composer = makeEl('div')
composer.contentEditable = 'true'
const fileInput = makeEl('input')
fileInput.type = 'file'
fileInput.setAttribute('accept', 'image/*')

const byId = new Map()
const findById = (id) => {
  for (const el of created) if (el.id === id) return el
  return byId.get(id) || null
}

const document = {
  readyState: 'complete',
  head,
  body,
  createElement: (tag) => {
    const el = makeEl(tag)
    // the script assigns .id after creation; index lazily through `created`
    return el
  },
  getElementById: findById,
  querySelectorAll: (sel) => {
    if (sel === 'textarea') return []
    if (sel.indexOf('contenteditable') !== -1) return [composer]
    if (sel.indexOf('input[type=file]') !== -1) return [fileInput]
    return []
  },
  addEventListener() {},
  execCommand() { return true }
}

const window = {
  innerWidth: 1600,
  innerHeight: 1000,
  addEventListener() {},
  getComputedStyle() { return { visibility: 'visible', display: 'block', opacity: '1' } }
}

const location = { pathname: '/' }
const navigator = { clipboard: { writeText: () => Promise.resolve() } }
const fetchStub = () => Promise.reject(new Error('offline in selftest'))
const consoleStub = { log() {}, warn() {}, error() {} }

const checks = []
function check(label, ok, detail) {
  checks.push({ label, ok })
  console.log((ok ? 'PASS  ' : 'FAIL  ') + label + (detail ? '   ' + detail : ''))
}

let threw = null
try {
  const run = new Function('window', 'document', 'location', 'navigator', 'fetch', 'console', code)
  run(window, document, location, navigator, fetchStub, consoleStub)
} catch (err) {
  threw = err
}

check('script boots without throwing', threw === null, threw ? String(threw.message) : '')

const buttons = created.filter((el) => String(el.className).indexOf('dsh-shot-btn') !== -1 && el.tagName === 'BUTTON')
check('two buttons created', buttons.length === 2, 'got ' + buttons.length)

const plain = buttons.find((b) => b.id === 'dsh-shot-button')
const blue = buttons.find((b) => b.id === 'dsh-shot-button-hide')
check('plain button present', !!plain)
check('blue button present', !!blue)
check('blue button is blue', !!blue && blue.style.color === (config.hideButton.color || '#2f7bf6'),
  blue ? String(blue.style.color) : '-')
check('blue button styled by class', !!blue && String(blue.className).indexOf('dsh-shot-btn-blue') !== -1)
check('buttons positioned side by side', !!plain && !!blue && plain.style.left !== blue.style.left,
  plain && blue ? 'plain.left=' + plain.style.left + '  blue.left=' + blue.style.left : '-')
check('buttons are visible once a composer is found',
  !!plain && plain.style.display === 'flex', plain ? String(plain.style.display) : '-')

const menu = findById('dsh-shot-menu')
check('menu created', !!menu)
if (menu) {
  const items = menu.children.filter((c) => c.className === 'dsh-shot-item')
  check('menu has three modes', items.length === 3, 'got ' + items.length)
  const head = menu.children.find((c) => c.className === 'dsh-shot-head')
  check('menu has a mode header', !!head)
}

const style = findById('dsh-shot-style')
check('style injected once', !!style)

await new Promise((r) => setTimeout(r, 100))

const failed = checks.filter((c) => !c.ok)
console.log('')
console.log(failed.length === 0
  ? 'all ' + checks.length + ' checks passed'
  : failed.length + ' of ' + checks.length + ' checks FAILED')
process.exit(failed.length === 0 ? 0 : 1)
