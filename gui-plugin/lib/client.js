// dsh-shot-button — client side (injected into the DSH Web GUI as a plain script).
//
// Two buttons sit side by side at the composer's top-right, both offering full screen /
// window / region:
//
//   [ blue capture ]  [ plain capture ]      (plain keeps the right-hand spot)
//
// The blue one asks the shot service to hide the window in front first, so the capture
// shows what was behind it (QQ-style). The plain one captures as-is. Which button hides
// is decided by config.json (hidesWindow), not hardcoded here.
//
// After a capture it tries, in order:
//   1. push the PNG into the GUI's own file input -> appears as a normal attachment
//   2. otherwise insert an "@shots/<file>" workspace reference into the composer
//   3. if there is no composer at all, copy that reference to the clipboard instead
//
// The buttons are never hidden just because the composer could not be identified: they
// fall back to a fixed spot, so the feature stays reachable.
//
// Every stage reports to the local shot service (/beacon), which appends to
// shots\beacon.log — that is how a missing button can be diagnosed from the outside.
//
// Everything is wrapped in try/catch on purpose: this runs inside someone else's React
// app and must never be able to break the composer.

;(function () {
  'use strict'

  if (window.__dshShotButtonLoaded) return
  window.__dshShotButtonLoaded = true

  var CFG = window.__DSH_SHOT_CONFIG__ || {}
  var SIZE = CFG.buttonSize || 30
  var GAP = CFG.buttonsGap == null ? 6 : CFG.buttonsGap
  var OFF_RIGHT = CFG.offsetRight == null ? 8 : CFG.offsetRight
  var OFF_TOP = CFG.offsetTop == null ? 8 : CFG.offsetTop
  var PREFIX = CFG.referencePrefix || 'shots/'
  var PORT = CFG.port || 38901
  var DIRECT = 'http://127.0.0.1:' + PORT
  // null = work it out from the font size (four Chinese characters), never less than
  // what the buttons themselves occupy. A number pins it to that many pixels.
  var PAD_RIGHT = CFG.composerPaddingRight === undefined ? null : CFG.composerPaddingRight

  // Frame appearance, offered right in this menu so the tray icon never has to be found.
  var FRAME_COLORS = [
    { hex: '#ff4040', name: '红' },
    { hex: '#ff0000', name: '正红' },
    { hex: '#ff8f00', name: '橙' },
    { hex: '#ffc400', name: '黄' },
    { hex: '#00c853', name: '绿' },
    { hex: '#00bcd4', name: '青' },
    { hex: '#2f7bf6', name: '蓝' },
    { hex: '#a855f7', name: '紫' },
    { hex: '#ff5fa2', name: '粉' },
    { hex: '#ffffff', name: '白' }
  ]
  var FRAME_WIDTHS = [
    { value: 2, name: '细' },
    { value: 4, name: '中' },
    { value: 6, name: '粗' }
  ]
  var frameSettings = { outlineWidth: 2, regionColor: '#ff4040', windowColor: '#ff4040' }

  var MENU_ID = 'dsh-shot-menu'
  var TOAST_ID = 'dsh-shot-toast'
  var STYLE_ID = 'dsh-shot-style'

  var CAMERA_SVG =
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ' +
    'stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z"/>' +
    '<circle cx="12" cy="13" r="4"/></svg>'

  // Rightmost first: the plain button keeps the spot it has always had, the blue one
  // is added to its left.
  function buildButtonSpecs() {
    var specs = []
    var plain = CFG.plainButton || {}
    var hide = CFG.hideButton || {}

    if (plain.enabled !== false) {
      specs.push({
        key: 'plain',
        id: 'dsh-shot-button',
        cls: 'dsh-shot-btn',
        color: plain.color || null,
        title: plain.title || '截屏并插入（全屏 / 窗口 / 框选）',
        hidesWindow: plain.hidesWindow === true
      })
    }
    if (hide.enabled !== false) {
      specs.push({
        key: 'hide',
        id: 'dsh-shot-button-hide',
        cls: 'dsh-shot-btn dsh-shot-btn-blue',
        color: hide.color || '#2f7bf6',
        title: hide.title || '截屏并插入 · 先隐藏当前窗口再截（QQ 式）',
        hidesWindow: hide.hidesWindow !== false
      })
    }
    return specs
  }

  var buttons = []          // [{ spec, el }]
  var menu = null
  var activeButton = null   // the button whose menu is open

  var ITEMS = [
    { mode: 'Full', label: '全屏截图', hint: 'Ctrl+Alt+F1' },
    { mode: 'Window', label: '窗口截图', hint: 'Ctrl+Alt+F2' },
    { mode: 'Region', label: '框选截图', hint: 'Ctrl+Alt+F3' }
  ]

  function log() {
    try {
      var args = [].slice.call(arguments)
      args.unshift('[dsh-shot]')
      console.log.apply(console, args)
    } catch (e) { /* ignore */ }
  }

  // --- reporting -----------------------------------------------------------

  function beacon(event, extra) {
    try {
      var url = DIRECT + '/beacon?event=' + encodeURIComponent(event) +
        '&path=' + encodeURIComponent(location.pathname) +
        (extra ? '&' + extra : '')
      fetch(url, { cache: 'no-store', mode: 'cors' }).catch(function () { /* service down */ })
    } catch (e) { /* ignore */ }
  }

  // --- locating the composer ----------------------------------------------

  function isOurs(el) {
    try {
      if (!el) return false
      if (el.closest && el.closest('#' + MENU_ID + ', #' + TOAST_ID + ', .dsh-shot-btn')) return true
      for (var i = 0; i < buttons.length; i++) {
        if (buttons[i].el === el) return true
      }
      return false
    } catch (e) { return false }
  }

  function isUsable(el) {
    try {
      if (!el) return false
      if (isOurs(el)) return false
      if (el.disabled || el.readOnly) return false
      var r = el.getBoundingClientRect()
      if (r.width < 150 || r.height < 24) return false
      if (r.bottom < 0 || r.top > window.innerHeight) return false
      var style = window.getComputedStyle(el)
      if (style.visibility === 'hidden' || style.display === 'none' || style.opacity === '0') return false
      return true
    } catch (e) { return false }
  }

  // Returns { el, kind } — kind is 'textarea' or 'editable'.
  function findComposer() {
    try {
      var best = null
      var bestArea = 0

      var areas = document.querySelectorAll('textarea')
      for (var i = 0; i < areas.length; i++) {
        var t = areas[i]
        if (!isUsable(t)) continue
        var r = t.getBoundingClientRect()
        var area = r.width * r.height
        if (area > bestArea) { bestArea = area; best = { el: t, kind: 'textarea' } }
      }

      var editables = document.querySelectorAll('[contenteditable="true"], [contenteditable=""]')
      for (var j = 0; j < editables.length; j++) {
        var d = editables[j]
        if (!isUsable(d)) continue
        var r2 = d.getBoundingClientRect()
        var area2 = r2.width * r2.height
        // an editable box has to look like an input, not a huge scroll container
        if (area2 > window.innerWidth * window.innerHeight * 0.6) continue
        if (area2 > bestArea) { bestArea = area2; best = { el: d, kind: 'editable' } }
      }

      return best
    } catch (e) { return null }
  }

  function findFileInput() {
    try {
      var list = document.querySelectorAll('input[type=file]')
      var fallback = null
      for (var i = 0; i < list.length; i++) {
        var el = list[i]
        if (el.disabled) continue
        var accept = (el.getAttribute('accept') || '').toLowerCase()
        if (accept.indexOf('image') !== -1) return el
        if (!fallback) fallback = el
      }
      return fallback
    } catch (e) { return null }
  }

  // --- ui ------------------------------------------------------------------

  function injectStyle() {
    if (document.getElementById(STYLE_ID)) return
    var css = [
      '.dsh-shot-btn{position:fixed;z-index:2147483000;display:none;align-items:center;',
      'justify-content:center;padding:0;margin:0;box-sizing:border-box;color:inherit;cursor:pointer;',
      'border:1px solid rgba(127,127,127,.38);border-radius:8px;background:rgba(127,127,127,.14);',
      'transition:background .12s ease,opacity .12s ease}',
      '.dsh-shot-btn:hover{background:rgba(127,127,127,.30)}',
      '.dsh-shot-btn[data-busy="1"]{opacity:.5;cursor:progress}',
      '.dsh-shot-btn svg{width:17px;height:17px;display:block;pointer-events:none}',
      '.dsh-shot-btn-blue{border-color:rgba(47,123,246,.5);background:rgba(47,123,246,.15);color:#2f7bf6}',
      '.dsh-shot-btn-blue:hover{background:rgba(47,123,246,.3)}',
      '#' + MENU_ID + '{position:fixed;z-index:2147483001;display:none;padding:4px;',
      'background:rgba(30,34,44,.97);border:1px solid rgba(127,127,127,.32);border-radius:9px;',
      'box-shadow:0 10px 28px rgba(0,0,0,.42);font-size:13px;min-width:158px;',
      'color:#e7e9ef;font-family:inherit}',
      '#' + MENU_ID + ' .dsh-shot-head{padding:4px 10px 6px;font-size:11.5px;opacity:.62;',
      'white-space:nowrap;border-bottom:1px solid rgba(127,127,127,.22);margin-bottom:4px}',
      '#' + MENU_ID + ' .dsh-shot-item{display:flex;justify-content:space-between;gap:14px;',
      'padding:6px 10px;border-radius:6px;cursor:pointer;white-space:nowrap}',
      '#' + MENU_ID + ' .dsh-shot-item:hover{background:rgba(127,127,127,.26)}',
      '#' + MENU_ID + ' .dsh-shot-hint{opacity:.5;font-size:11.5px}',
      '#' + MENU_ID + ' .dsh-shot-sep{height:1px;background:rgba(127,127,127,.22);margin:5px 2px}',
      '#' + MENU_ID + ' .dsh-shot-row{display:flex;align-items:center;justify-content:space-between;',
      'gap:10px;padding:5px 10px;white-space:nowrap}',
      '#' + MENU_ID + ' .dsh-shot-rowlabel{opacity:.62;font-size:11.5px}',
      '#' + MENU_ID + ' .dsh-shot-swatches{display:flex;gap:3px}',
      '#' + MENU_ID + ' .dsh-shot-swatch{width:14px;height:14px;border-radius:4px;cursor:pointer;',
      'border:1px solid rgba(0,0,0,.4);box-sizing:border-box}',
      '#' + MENU_ID + ' .dsh-shot-swatch[data-current="1"]{box-shadow:0 0 0 2px #fff}',
      '#' + MENU_ID + ' .dsh-shot-widths{display:flex;gap:4px}',
      '#' + MENU_ID + ' .dsh-shot-width{padding:0 7px;border-radius:5px;cursor:pointer;font-size:11.5px;',
      'border:1px solid rgba(127,127,127,.35)}',
      '#' + MENU_ID + ' .dsh-shot-width[data-current="1"]{background:rgba(47,123,246,.4);',
      'border-color:rgba(47,123,246,.8)}'
    ].join('')
    var s = document.createElement('style')
    s.id = STYLE_ID
    s.textContent = css
    document.head.appendChild(s)
  }

  function createButtons() {
    var specs = buildButtonSpecs()
    for (var i = 0; i < specs.length; i++) {
      var spec = specs[i]
      var el = document.createElement('button')
      el.id = spec.id
      el.type = 'button'
      el.className = spec.cls
      el.title = spec.title
      el.setAttribute('aria-label', spec.key === 'hide' ? '截屏（先隐藏窗口）' : '截屏')
      el.style.width = SIZE + 'px'
      el.style.height = SIZE + 'px'
      if (spec.color) el.style.color = spec.color
      el.innerHTML = CAMERA_SVG

      // one entry object per button, shared by the DOM node, the click handler and the
      // repositioning loop, so identity comparisons stay meaningful
      var entry = { spec: spec, el: el }

      el.addEventListener('mousedown', function (e) { e.preventDefault() })
      el.addEventListener('click', (function (en) {
        return function (e) {
          e.preventDefault()
          e.stopPropagation()
          toggleMenu(en)
        }
      })(entry))
      document.body.appendChild(el)
      buttons.push(entry)
    }
  }

  function createMenu() {
    menu = document.createElement('div')
    menu.id = MENU_ID

    var head = document.createElement('div')
    head.className = 'dsh-shot-head'
    head.id = 'dsh-shot-head'
    menu.appendChild(head)

    for (var i = 0; i < ITEMS.length; i++) {
      ;(function (item) {
        var row = document.createElement('div')
        row.className = 'dsh-shot-item'
        var label = document.createElement('span')
        label.textContent = item.label
        var hint = document.createElement('span')
        hint.className = 'dsh-shot-hint'
        hint.textContent = item.hint
        row.appendChild(label)
        row.appendChild(hint)
        row.addEventListener('mousedown', function (e) { e.preventDefault() })
        row.addEventListener('click', function (e) {
          e.preventDefault()
          e.stopPropagation()
          var entry = activeButton
          hideMenu()
          if (entry) capture(item.mode, entry.spec.hidesWindow, entry.spec.key)
        })
        menu.appendChild(row)
      })(ITEMS[i])
    }

    menu.appendChild(makeSeparator())
    menu.appendChild(makeColorRow())
    menu.appendChild(makeWidthRow())

    document.body.appendChild(menu)
  }

  // --- frame appearance, straight from this menu ---------------------------

  function makeSeparator() {
    var sep = document.createElement('div')
    sep.className = 'dsh-shot-sep'
    return sep
  }

  function makeRow(labelText, controlId, controlClass) {
    var row = document.createElement('div')
    row.className = 'dsh-shot-row'
    var label = document.createElement('span')
    label.className = 'dsh-shot-rowlabel'
    label.textContent = labelText
    var box = document.createElement('span')
    box.id = controlId
    box.className = controlClass
    row.appendChild(label)
    row.appendChild(box)
    return { row: row, box: box }
  }

  function makeColorRow() {
    var built = makeRow('框线颜色', 'dsh-shot-swatches', 'dsh-shot-swatches')
    for (var i = 0; i < FRAME_COLORS.length; i++) {
      ;(function (c) {
        var sw = document.createElement('span')
        sw.className = 'dsh-shot-swatch'
        sw.title = c.name + ' ' + c.hex
        sw.style.background = c.hex
        sw.setAttribute('data-hex', c.hex)
        sw.addEventListener('mousedown', function (e) { e.preventDefault(); e.stopPropagation() })
        sw.addEventListener('click', function (e) {
          e.preventDefault()
          e.stopPropagation()
          applyFrameSetting('regionColor=' + encodeURIComponent(c.hex) +
            '&windowColor=' + encodeURIComponent(c.hex))
        })
        built.box.appendChild(sw)
      })(FRAME_COLORS[i])
    }
    return built.row
  }

  function makeWidthRow() {
    var built = makeRow('框线粗细', 'dsh-shot-widths', 'dsh-shot-widths')
    for (var i = 0; i < FRAME_WIDTHS.length; i++) {
      ;(function (w) {
        var chip = document.createElement('span')
        chip.className = 'dsh-shot-width'
        chip.textContent = w.name
        chip.title = w.name + ' ' + w.value + ' 像素'
        chip.setAttribute('data-value', String(w.value))
        chip.addEventListener('mousedown', function (e) { e.preventDefault(); e.stopPropagation() })
        chip.addEventListener('click', function (e) {
          e.preventDefault()
          e.stopPropagation()
          applyFrameSetting('outlineWidth=' + w.value)
        })
        built.box.appendChild(chip)
      })(FRAME_WIDTHS[i])
    }
    return built.row
  }

  function loadFrameSettings() {
    try {
      fetch(DIRECT + '/config', { cache: 'no-store', mode: 'cors' })
        .then(function (res) { return res.ok ? res.json() : null })
        .then(function (cfg) { if (cfg) { frameSettings = cfg; renderFrameControls() } })
        .catch(function () { /* service down: keep whatever we have */ })
    } catch (e) { /* ignore */ }
  }

  function applyFrameSetting(query) {
    try {
      fetch(DIRECT + '/set?' + query, { cache: 'no-store', mode: 'cors' })
        .then(function (res) {
          if (!res.ok) throw new Error('HTTP ' + res.status)
          return res.json()
        })
        .then(function (cfg) {
          frameSettings = cfg
          renderFrameControls()
          beacon('frame-setting', 'q=' + encodeURIComponent(query))
        })
        .catch(function (err) { toast('设置失败：' + (err && err.message ? err.message : err), true) })
    } catch (e) { /* ignore */ }
  }

  function renderFrameControls() {
    try {
      var box = document.getElementById('dsh-shot-swatches')
      if (box) {
        var current = String(frameSettings.regionColor || '').toLowerCase()
        for (var i = 0; i < box.children.length; i++) {
          var sw = box.children[i]
          var isCurrent = String(sw.getAttribute('data-hex')).toLowerCase() === current
          sw.setAttribute('data-current', isCurrent ? '1' : '0')
        }
      }
      var widths = document.getElementById('dsh-shot-widths')
      if (widths) {
        for (var j = 0; j < widths.children.length; j++) {
          var chip = widths.children[j]
          var same = Number(chip.getAttribute('data-value')) === Number(frameSettings.outlineWidth)
          chip.setAttribute('data-current', same ? '1' : '0')
        }
      }
    } catch (e) { /* ignore */ }
  }

  function positionMenu(anchorEl) {
    try {
      if (!anchorEl || !menu) return
      var b = anchorEl.getBoundingClientRect()
      var mh = menu.offsetHeight || 140
      var mw = menu.offsetWidth || 158
      var top = b.top - mh - 8
      if (top < 4) top = b.bottom + 8
      var left = b.right - mw
      if (left < 4) left = 4
      menu.style.left = Math.round(left) + 'px'
      menu.style.top = Math.round(top) + 'px'
    } catch (e) { /* ignore */ }
  }

  function toggleMenu(entry) {
    if (!menu) return
    if (menu.style.display === 'block' && activeButton === entry) { hideMenu(); return }
    activeButton = entry
    var head = document.getElementById('dsh-shot-head')
    if (head) {
      head.textContent = entry.spec.hidesWindow
        ? '截图前隐藏当前窗口（QQ 式）'
        : '普通截图（不隐藏窗口）'
    }
    menu.style.display = 'block'
    renderFrameControls()
    loadFrameSettings()
    positionMenu(entry.el)
    beacon('menu-open', 'button=' + entry.spec.key)
  }

  function hideMenu() {
    if (menu) menu.style.display = 'none'
  }

  function toast(text, isError) {
    try {
      var el = document.getElementById(TOAST_ID)
      if (!el) {
        el = document.createElement('div')
        el.id = TOAST_ID
        el.style.cssText =
          'position:fixed;z-index:2147483002;right:18px;bottom:110px;max-width:360px;' +
          'padding:8px 12px;border-radius:8px;font-size:12.5px;line-height:1.5;color:#fff;' +
          'background:rgba(28,32,42,.95);box-shadow:0 6px 20px rgba(0,0,0,.35);' +
          'opacity:0;transition:opacity .18s ease;pointer-events:none;word-break:break-all'
        document.body.appendChild(el)
      }
      el.textContent = text
      el.style.background = isError ? 'rgba(152,44,44,.96)' : 'rgba(28,32,42,.95)'
      el.style.opacity = '1'
      clearTimeout(el.__dshShotTimer)
      el.__dshShotTimer = setTimeout(function () { el.style.opacity = '0' }, isError ? 5200 : 2400)
    } catch (e) { /* ignore */ }
  }

  // --- keeping the buttons on the composer --------------------------------

  // Push the composer's right edge inwards so the buttons never sit on top of the text.
  // Requested as "four Chinese characters"; the buttons themselves need a little more
  // than that, so the wider of the two wins unless config.json pins a number.
  function applyComposerPadding(el) {
    try {
      if (PAD_RIGHT === 0) return
      var pad = PAD_RIGHT
      if (pad == null) {
        var fontSize = parseFloat(window.getComputedStyle(el).fontSize) || 16
        var fourChars = Math.round(fontSize * 4)
        var buttonArea = SIZE * buttons.length + GAP * Math.max(0, buttons.length - 1) + OFF_RIGHT + 10
        pad = Math.max(fourChars, buttonArea)
      }
      var want = Math.round(pad) + 'px'
      if (el.style.paddingRight !== want) el.style.paddingRight = want
    } catch (e) { /* ignore */ }
  }

  function reposition() {
    try {
      if (!buttons.length) return
      var found = findComposer()
      var right, top

      if (found) {
        var r = found.el.getBoundingClientRect()
        right = Math.round(r.right - OFF_RIGHT)
        top = Math.round(r.top + OFF_TOP)
        applyComposerPadding(found.el)
      }
      else {
        right = Math.round(window.innerWidth - 22)
        top = Math.round(window.innerHeight - SIZE - 150)
      }

      if (top < 4) top = 4

      for (var i = 0; i < buttons.length; i++) {
        var el = buttons[i].el
        var left = right - SIZE * (i + 1) - GAP * i
        if (left < 4) left = 4
        el.style.display = 'flex'
        el.style.left = left + 'px'
        el.style.top = top + 'px'
      }
      if (menu && menu.style.display === 'block' && activeButton) positionMenu(activeButton.el)
    } catch (e) { /* ignore */ }
  }

  // --- getting the shot into the composer ---------------------------------

  function attachAsFile(blob, fileName) {
    try {
      var input = findFileInput()
      if (!input) {
        log('no file input found')
        return false
      }
      var dt = new DataTransfer()
      dt.items.add(new File([blob], fileName, { type: 'image/png' }))
      input.files = dt.files
      input.dispatchEvent(new Event('change', { bubbles: true }))
      log('pushed', fileName, 'into', input)
      return true
    } catch (e) {
      log('attachment injection failed', e)
      return false
    }
  }

  function insertReference(ref) {
    try {
      var found = findComposer()
      if (!found) return false

      var el = found.el
      var text = '@' + ref + ' '
      el.focus()

      if (found.kind === 'textarea') {
        var proto = el.tagName === 'INPUT' ? window.HTMLInputElement.prototype : window.HTMLTextAreaElement.prototype
        var setter = Object.getOwnPropertyDescriptor(proto, 'value').set
        var current = el.value || ''
        var trimmed = current.replace(/\s+$/, '')
        setter.call(el, (trimmed ? trimmed + ' ' : '') + text)
        el.dispatchEvent(new Event('input', { bubbles: true }))
        return true
      }

      // contenteditable: execCommand still notifies the app's own input handlers,
      // which a raw textContent assignment would not.
      var ok = false
      try { ok = document.execCommand('insertText', false, text) } catch (e) { ok = false }
      if (!ok) {
        el.textContent = (el.textContent || '') + text
        el.dispatchEvent(new InputEvent('input', { bubbles: true }))
      }
      return true
    } catch (e) {
      log('reference insert failed', e)
      return false
    }
  }

  function copyReference(ref) {
    try {
      return navigator.clipboard.writeText('@' + ref).then(
        function () { return true },
        function () { return false }
      )
    } catch (e) { return Promise.resolve(false) }
  }

  function setBusy(on) {
    try {
      for (var i = 0; i < buttons.length; i++) {
        buttons[i].el.dataset.busy = on ? '1' : '0'
        buttons[i].el.disabled = !!on
      }
    } catch (e) { /* ignore */ }
  }

  // Talks to the local shot service directly (it allows this GUI's loopback origin) so
  // the per-request hide flag is always honoured, and falls back to the same-origin
  // plugin route if that port is unreachable.
  function requestShot(mode, hidesWindow) {
    var hide = hidesWindow ? '1' : '0'
    var directUrl = DIRECT + '/shot?mode=' + encodeURIComponent(mode) + '&hide=' + hide
    return fetch(directUrl, { cache: 'no-store', mode: 'cors' })
      .catch(function () {
        return fetch('/dsh-shot/capture?mode=' + encodeURIComponent(mode) + '&hide=' + hide,
          { cache: 'no-store' })
      })
  }

  function capture(mode, hidesWindow, buttonKey) {
    setBusy(true)
    toast(hidesWindow ? '截图中（先隐藏窗口）…' : '截图中…')
    beacon('capture', 'mode=' + mode + '&button=' + buttonKey + '&hide=' + (hidesWindow ? 1 : 0))

    requestShot(mode, hidesWindow)
      .then(function (res) {
        if (res.status === 204) { toast('已取消'); beacon('capture-cancelled'); return null }
        if (!res.ok) {
          toast('截图失败：HTTP ' + res.status, true)
          beacon('capture-failed', 'status=' + res.status)
          return null
        }
        var fileName = res.headers.get('X-Shot-Name') || ('shot-' + Date.now() + '.png')
        return res.blob().then(function (blob) { return { blob: blob, fileName: fileName } })
      })
      .then(function (shot) {
        if (!shot) return
        var attached = attachAsFile(shot.blob, shot.fileName)
        var ref = PREFIX + shot.fileName

        if (attached && !CFG.alwaysInsertReference) {
          toast('已插入附件 ' + shot.fileName)
          beacon('capture-ok', 'attached=1')
          return
        }

        if (insertReference(ref)) {
          toast(attached ? ('已插入附件与引用 @' + ref) : ('已插入引用 @' + ref))
          beacon('capture-ok', 'attached=' + (attached ? 1 : 0) + '&inserted=1')
          return
        }

        copyReference(ref).then(function (copied) {
          toast(copied
            ? ('没找到输入框，路径已复制：@' + ref + '  —— 直接 Ctrl+V 即可')
            : ('截图已保存：' + ref), !copied)
          beacon('capture-ok', 'attached=' + (attached ? 1 : 0) + '&inserted=0&copied=' + (copied ? 1 : 0))
        })
      })
      .catch(function (err) {
        toast('截图失败：' + (err && err.message ? err.message : err), true)
        beacon('capture-error', 'msg=' + encodeURIComponent(String((err && err.message) || err)))
      })
      .then(function () { setBusy(false) })
  }

  // --- boot ----------------------------------------------------------------

  function onDocumentMouseDown(e) {
    try {
      if (!menu || menu.style.display !== 'block') return
      if (menu.contains(e.target)) return
      for (var i = 0; i < buttons.length; i++) {
        if (buttons[i].el.contains(e.target)) return
      }
      hideMenu()
    } catch (err) { /* ignore */ }
  }

  var reported = false

  function boot() {
    try {
      if (!document.body) return
      injectStyle()
      createButtons()
      createMenu()
      document.addEventListener('mousedown', onDocumentMouseDown, true)
      window.addEventListener('resize', reposition)
      window.addEventListener('scroll', reposition, true)
      setInterval(reposition, 400)
      reposition()
      log('ready; buttons =', buttons.map(function (b) { return b.spec.key }).join(','), CFG)

      if (!reported) {
        reported = true
        setTimeout(function () {
          try {
            var found = findComposer()
            beacon('boot',
              'buttons=' + buttons.map(function (b) { return b.spec.key + ':' + (b.spec.hidesWindow ? 'hide' : 'plain') }).join(',') +
              '&composer=' + (found ? found.kind : 'none') +
              '&textareas=' + document.querySelectorAll('textarea').length +
              '&editables=' + document.querySelectorAll('[contenteditable="true"]').length +
              '&fileInputs=' + document.querySelectorAll('input[type=file]').length)
          } catch (e) { beacon('boot-error') }
        }, 1200)
      }
    } catch (e) {
      log('setup failed', e)
      try { beacon('setup-failed', 'msg=' + encodeURIComponent(String((e && e.message) || e))) } catch (e2) { /* ignore */ }
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot)
  } else {
    boot()
  }
})()
