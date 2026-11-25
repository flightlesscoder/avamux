// avamux terminal page: one xterm.js instance per terminal session, each bridged to its pty over a WebSocket.
(function () {
  'use strict';
  const token = new URLSearchParams(location.search).get('t') || '';
  const root = document.getElementById('root');
  const empty = document.getElementById('empty');
  const menu = document.getElementById('menu');
  const sessions = new Map();
  const isMac = /Mac/i.test(navigator.platform);
  let active = null;
  let opts = {
    scrollback: 10000, fontSize: 13,
    fontFamily: '"JetBrainsMono Nerd Font Mono", "JetBrainsMono Nerd Font", "Symbols Nerd Font Mono", "Symbols Nerd Font", Menlo, "DejaVu Sans Mono", monospace', cursorBlink: true, copyOnSelect: false,
    background: '#11131a', foreground: '#d8dee9', renderer: 'dom'
  };

  // Settings are embedded by the server (options.js), so the very first render already uses the right font.
  if (window.__avamuxOptions) opts = Object.assign(opts, window.__avamuxOptions);

  // WebKit never falls back to system fonts for private-use code points (powerline, Nerd Font icons), so the
  // bundled symbols font is always appended as the last family.
  function fonts() { return (opts.fontFamily || 'monospace') + ', "avamux-symbols"'; }

  const ansiNames = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white',
    'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite'];
  const defaultAnsi = ['#1c1f2b', '#e06c75', '#98c379', '#e5c07b', '#61afef', '#c678dd', '#56b6c2', '#c8ccd4',
    '#5c6370', '#ff7b86', '#b1e18b', '#ffd68a', '#7cc0ff', '#dd91f2', '#6fd3df', '#ffffff'];

  // Colours come from the app's theme (options.js, then avamux.options on every change).
  function theme() {
    const t = {
      background: opts.background, foreground: opts.foreground, cursor: opts.cursor || opts.foreground,
      selectionBackground: opts.selection || '#4c566a', selectionInactiveBackground: opts.selection || '#3b4252'
    };
    // The heartbeat cursor is our own element; xterm's is made invisible, and the character on a filled
    // cursor is drawn in the background colour so it stays readable.
    if (opts.pulse) { t.cursor = '#00000000'; t.cursorAccent = opts.pulseShape === 'block' ? opts.background : opts.foreground; }
    const ansi = Array.isArray(opts.ansi) && opts.ansi.length === 16 ? opts.ansi : defaultAnsi;
    ansiNames.forEach((n, i) => { t[n] = ansi[i]; });
    return t;
  }

  // Sets a style property, class or CSS variable only when it differs from what was last set: this runs on every
  // render, and touching the DOM there (even with the same value) costs a style pass per frame of output.
  function put(s, name, value) {
    if (s.put[name] === value) return;
    s.put[name] = value;
    if (name === 'class') s.pulse.className = value;
    else if (name.startsWith('--')) s.pulse.style.setProperty(name, value);
    else s.pulse.style[name] = value;
  }

  // The size of one cell. xterm has already measured it; reading it from the layout would force one mid-render.
  function cell(s) {
    try { const c = s.term._core._renderService.dimensions.css.cell; if (c.width > 0 && c.height > 0) return c; } catch { }
    const screen = s.pulse.parentNode;
    return { width: screen.clientWidth / s.term.cols, height: screen.clientHeight / s.term.rows };
  }

  // Places the heartbeat cursor over the cell xterm's cursor is in, or hides it when there is none to show.
  function pulse(s) {
    const term = s.term, buf = term.buffer.active, on = !!opts.pulse;
    if (s.put.on !== on) { s.put.on = on; s.el.classList.toggle('pulse-on', on); }
    if (!on) { put(s, 'class', 'pulse-cursor'); return; }
    let hidden = false;
    try { hidden = !!term._core.coreService.isCursorHidden; } catch { }
    const row = buf.baseY + buf.cursorY - buf.viewportY;
    if (!s.pulse.parentNode) {
      const screen = s.el.querySelector('.xterm-screen');
      if (screen) screen.insertBefore(s.pulse, screen.firstChild);
    }
    if (hidden || !s.pulse.parentNode || row < 0 || row >= term.rows) { put(s, 'class', 'pulse-cursor'); return; }
    const c = cell(s), w = c.width, h = c.height;
    const shape = opts.pulseShape || 'block';
    const focused = document.activeElement === term.textarea && document.hasFocus();
    put(s, 'class', 'pulse-cursor on ' + shape + (opts.pulseDouble ? ' double' : '') + (focused ? '' : ' idle'));
    // Moved with a transform so that following the cursor needs no layout.
    put(s, 'transform', 'translate(' + Math.min(buf.cursorX, term.cols - 1) * w + 'px,' + row * h + 'px)');
    put(s, 'width', (shape === 'bar' ? Math.max(2, Math.round(w / 5)) : w) + 'px');
    put(s, 'height', h + 'px');
    put(s, '--pc', opts.pulseColor || opts.cursor || opts.foreground);
    put(s, '--pg', String((opts.pulseGlow == null ? 60 : opts.pulseGlow) / 100));
    put(s, '--pd', (60 / (opts.pulseRate || 30)) + 's');
  }

  // Draws the CRT overlay (scanlines, a vignette and darkened edges) into its canvas. Called when the effect is
  // switched, its strength changes or the view is resized; nothing is drawn while output scrolls.
  const crt = document.getElementById('crt');
  let crtDrawn = '';
  function drawCrt() {
    if (!opts.crt) { crtDrawn = ''; return; }
    const ci = (opts.crtIntensity == null ? 50 : opts.crtIntensity) / 100;
    const dpr = window.devicePixelRatio || 1, w = Math.round(innerWidth * dpr), h = Math.round(innerHeight * dpr);
    const key = w + 'x' + h + '@' + ci;
    if (key === crtDrawn || !w || !h) return;
    crtDrawn = key;
    crt.width = w; crt.height = h;
    const g = crt.getContext('2d');
    g.clearRect(0, 0, w, h);
    // Scanlines: one dark line in every three.
    g.fillStyle = 'rgba(0,0,0,' + ci * .32 + ')';
    const line = Math.max(1, Math.round(dpr)), period = Math.max(3, Math.round(3 * dpr));
    for (let y = 0; y < h; y += period) g.fillRect(0, y, w, line);
    // Vignette: an ellipse reaching the corners, clear in the middle.
    g.save();
    g.translate(w / 2, h / 2); g.scale(w / 2 * Math.SQRT2, h / 2 * Math.SQRT2);
    const v = g.createRadialGradient(0, 0, 0, 0, 0, 1);
    v.addColorStop(.55, 'rgba(0,0,0,0)'); v.addColorStop(1, 'rgba(0,0,0,' + ci * .55 + ')');
    g.fillStyle = v; g.fillRect(-1, -1, 2, 2);
    g.restore();
    // Edges: a shadow falling inwards from each side.
    const d = ci * 50 * dpr, edge = 'rgba(0,0,0,' + ci * .3 + ')', mid = 'rgba(0,0,0,' + ci * .09 + ')';
    for (const [x0, y0, x1, y1, rx, ry, rw, rh] of [[0, 0, d, 0, 0, 0, d, h], [w, 0, w - d, 0, w - d, 0, d, h], [0, 0, 0, d, 0, 0, w, d], [0, h, 0, h - d, 0, h - d, w, d]]) {
      const e = g.createLinearGradient(x0, y0, x1, y1);
      e.addColorStop(0, edge); e.addColorStop(.4, mid); e.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = e; g.fillRect(rx, ry, rw, rh);
    }
  }

  function chrome() {
    document.body.style.background = opts.background;
    document.body.classList.toggle('crt-on', !!opts.crt);
    document.body.classList.toggle('crt-flicker', !!opts.crt && !!opts.crtFlicker);
    document.body.classList.toggle('crt-glow', !!opts.crt && opts.crtGlow !== 'off' && opts.crtGlow !== 'colour');
    document.body.classList.toggle('crt-glow-colour', !!opts.crt && opts.crtGlow === 'colour');
    document.documentElement.style.setProperty('--ci', String((opts.crtIntensity == null ? 50 : opts.crtIntensity) / 100));
    drawCrt();
    const r = document.documentElement.style;
    r.setProperty('--panel', opts.panel || '#1f2330');
    r.setProperty('--line', opts.line || '#3b4252');
    r.setProperty('--text', opts.text || '#d8dee9');
    r.setProperty('--dim', opts.dim || '#7b8496');
    r.setProperty('--accent', opts.accent || opts.cursor || '#61afef');
  }

  function post(o) { if (window.invokeCSharpAction) window.invokeCSharpAction(JSON.stringify(o)); }

  function send(s, msg) {
    if (s.ws && s.ws.readyState === WebSocket.OPEN) s.ws.send(JSON.stringify(msg));
  }

  function connect(s) {
    if (s.gone) return;
    const ws = new WebSocket(`ws://${location.host}/ws/term/${encodeURIComponent(s.id)}?t=${encodeURIComponent(token)}`);
    ws.binaryType = 'arraybuffer';
    s.ws = ws;
    ws.onopen = () => {
      s.term.reset(); // the server replays its buffer on every connect
      s.replaying = true; // until the server's "replayed" marker has been through the parser
      s.stat.unacked = 0;
      send(s, { t: 'r', cols: s.term.cols, rows: s.term.rows });
      send(s, { t: 'vis', hidden: document.hidden });
    };
    ws.onmessage = (ev) => {
      if (typeof ev.data !== 'string') { write(s, new Uint8Array(ev.data)); return; }
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      // Writes are parsed in order, so this callback runs once the whole history before the marker has been.
      if (m.t === 'replayed') { s.term.write('', () => { if (s.ws === ws) s.replaying = false; }); }
      else if (m.t === 'paste') { if (m.text) s.term.paste(m.text); }
      else if (m.t === 'dump') send(s, { t: 'dump', n: m.n, text: m.stats ? JSON.stringify(statsOf(s)) : m.screen ? screenOf(s) : textOf(s, m.lines) });
      else if (m.t === 'exit') { s.exited = true; s.term.write(`\r\n\x1b[2m[process exited with code ${m.code}]\x1b[0m\r\n`); }
    };
    ws.onclose = () => { if (!s.gone && !s.exited) setTimeout(() => connect(s), 700); };
  }

  // ---- output: flow control, and what the view is doing (avamux terminal stats) ----
  // Parsed output is acknowledged to the server (every 32 KB, and whenever the view has caught up): it stops reading
  // from the program while the view is too far behind, which keeps Ctrl+C and typing from waiting behind a backlog.
  const ACK = 32 * 1024;
  // While output pours in (more than FLOOD bytes a second: find, a build log, cat), the CRT glow is put aside: it
  // blurs the whole text layer on every frame, which made such output three times slower to show. Screens redrawn
  // by full-screen programs (nvim) are far smaller and keep it.
  const FLOOD = 512 * 1024;
  let floodBytes = 0, floodSince = 0, floodTimer = null;
  function flooding(n) {
    const now = performance.now();
    if (now - floodSince > 1000) { floodSince = now; floodBytes = 0; }
    floodBytes += n;
    if (floodBytes < FLOOD) return;
    document.body.classList.add('flooding');
    clearTimeout(floodTimer);
    floodTimer = setTimeout(() => document.body.classList.remove('flooding'), 600);
  }
  function write(s, data) {
    const st = s.stat, ws = s.ws;
    st.recv += data.length;
    if (opts.crt) flooding(data.length);
    s.term.write(data, () => {
      st.parsed += data.length;
      if (st.keyAt) st.keyOut = true;
      if (s.ws !== ws) return; // written before a reconnect: the new connection counts afresh
      st.unacked += data.length;
      if (st.unacked >= ACK || st.parsed === st.recv) { send(s, { t: 'a', n: st.unacked }); st.unacked = 0; }
    });
  }

  // For `avamux terminal stats`: bytes received and parsed (the difference is output waiting to be shown), and how
  // long recent key presses took to show: from the key to the frame after the first output that followed it.
  function keyed(s) {
    const st = s.stat;
    if (!st.keyAt) { st.keyAt = performance.now(); st.keyOut = false; }
  }

  function rendered(s) {
    const st = s.stat;
    if (!st.keyAt || !st.keyOut) return;
    const t0 = st.keyAt;
    st.keyAt = 0;
    // The frame is painted after its animation callbacks have run; the timeout runs once it has been.
    requestAnimationFrame(() => setTimeout(() => { st.lat.push(Math.round(performance.now() - t0)); if (st.lat.length > 200) st.lat.shift(); }, 0));
  }

  function statsOf(s) {
    const st = s.stat;
    const renderer = s.gl ? 'webgl' : 'dom';
    return { received: st.recv, parsed: st.parsed, behind: st.recv - st.parsed, keyLatencyMs: st.lat.slice(), renderer, cols: s.term.cols, rows: s.term.rows,
             visible: document.visibilityState, crt: !!opts.crt, pulse: !!opts.pulse };
  }

  function textOf(s, maxLines) {
    const buf = s.term.buffer.active;
    const out = [];
    let cur = '';
    for (let i = 0; i < buf.length; i++) {
      const line = buf.getLine(i);
      if (!line) continue;
      const next = buf.getLine(i + 1);
      const wraps = next && next.isWrapped;
      cur += line.translateToString(!wraps);
      if (!wraps) { out.push(cur); cur = ''; }
    }
    if (cur) out.push(cur);
    while (out.length && out[out.length - 1] === '') out.pop();
    return (maxLines ? out.slice(-maxLines) : out).join('\n');
  }

  function screenOf(s) {
    const buf = s.term.buffer.active;
    const out = [];
    for (let i = 0; i < s.term.rows; i++) {
      const line = buf.getLine(buf.viewportY + i);
      out.push(line ? line.translateToString(true) : '');
    }
    return out.join('\n');
  }

  function copy(s) {
    const text = s.term.getSelection();
    if (text) send(s, { t: 'copy', text });
    return !!text;
  }

  // The boss key (Settings → Window), written like "Ctrl+Shift+B" or "F12".
  function isBossKey(ev) {
    const spec = (opts.bossKey || '').trim().toLowerCase();
    if (!spec) return false;
    const parts = spec.split('+').map(p => p.trim());
    const key = parts.pop();
    const want = { ctrl: parts.includes('ctrl') || parts.includes('control'), shift: parts.includes('shift'), alt: parts.includes('alt'), meta: parts.includes('meta') || parts.includes('cmd') };
    return ev.ctrlKey === want.ctrl && ev.shiftKey === want.shift && ev.altKey === want.alt && ev.metaKey === want.meta && ev.key.toLowerCase() === key;
  }

  function onKey(s, ev) {
    if (ev.type !== 'keydown') return true;
    const k = ev.key.toLowerCase();
    const copyCombo = (ev.ctrlKey && ev.shiftKey && k === 'c') || (isMac && ev.metaKey && k === 'c') || (ev.ctrlKey && ev.key === 'Insert');
    const pasteCombo = (ev.ctrlKey && ev.shiftKey && k === 'v') || (isMac && ev.metaKey && k === 'v') || (ev.shiftKey && ev.key === 'Insert');
    if (copyCombo) { copy(s); ev.preventDefault(); return false; }
    if (pasteCombo) { send(s, { t: 'paste' }); ev.preventDefault(); return false; }
    if (ev.ctrlKey && ev.shiftKey && (k === 't' || k === 'w')) { post({ t: k === 't' ? 'new' : 'close' }); ev.preventDefault(); return false; }
    if (isBossKey(ev)) { post({ t: 'boss' }); ev.preventDefault(); return false; }
    // Ctrl+Shift+L: the browser beside this tab (open one, or show / hide it).
    if (ev.ctrlKey && ev.shiftKey && k === 'l') { post({ t: 'browser' }); ev.preventDefault(); return false; }
    if (ev.ctrlKey && (ev.key === 'PageUp' || ev.key === 'PageDown')) { post({ t: ev.key === 'PageUp' ? 'prev' : 'next' }); ev.preventDefault(); return false; }
    keyed(s);
    return true; // everything else (Ctrl+B, Ctrl+C, Alt+…) goes to the program in the terminal
  }

  function open(id) {
    if (sessions.has(id)) return sessions.get(id);
    const el = document.createElement('div');
    el.className = 'term';
    root.appendChild(el);
    const term = new Terminal({
      allowProposedApi: true, scrollback: opts.scrollback, fontFamily: fonts(), fontSize: opts.fontSize,
      cursorBlink: opts.cursorBlink && !opts.pulse, theme: theme(), macOptionIsMeta: true, rightClickSelectsWord: false,
      allowTransparency: false, drawBoldTextInBrightColors: false, rescaleOverlappingGlyphs: true
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    try { term.loadAddon(new Unicode11Addon.Unicode11Addon()); term.unicode.activeVersion = '11'; } catch (e) { console.warn(e); }
    try { term.loadAddon(new WebLinksAddon.WebLinksAddon((e, uri) => send(s, { t: 'open', url: uri, mod: !!(e && (e.ctrlKey || e.metaKey)) }))); } catch (e) { console.warn(e); }
    term.open(el);
    const s = { id, el, term, fit, ws: null, exited: false, gone: false, replaying: false, typed: false, pulse: document.createElement('div'), put: {},
                gl: null, stat: { recv: 0, parsed: 0, unacked: 0, keyAt: 0, keyOut: false, lat: [] } };
    if (opts.renderer === 'webgl') {
      try { const gl = new WebglAddon.WebglAddon(); gl.onContextLoss(() => { gl.dispose(); s.gl = null; }); term.loadAddon(gl); s.gl = gl; } catch (e) { console.warn('webgl unavailable', e); }
    }
    // xterm raises this just before onData for keys and pastes, and not for the answers it sends to queries in the
    // output. That is how answers to queries in replayed history are told apart from the user typing meanwhile.
    try { term._core.coreService.onUserInput(() => { s.typed = true; }); } catch { }
    sessions.set(id, s);
    term.onRender(() => { pulse(s); rendered(s); });
    term.onScroll(() => pulse(s));
    term.textarea.addEventListener('focus', () => { pulse(s); focused(s); });
    term.textarea.addEventListener('blur', () => pulse(s));
    try { fit.fit(); } catch { }
    term.onData(d => {
      const typed = s.typed;
      s.typed = false;
      if (s.replaying && !typed) return; // an answer to a query in the history
      send(s, { t: 'i', d });
    });
    term.onBinary(d => send(s, { t: 'b', d: btoa(d) }));
    term.onResize(({ cols, rows }) => send(s, { t: 'r', cols, rows }));
    term.onTitleChange(title => send(s, { t: 'title', title }));
    term.onBell(() => { if (!s.replaying) send(s, { t: 'bell' }); }); // an old bell would mark an agent as waiting
    term.onSelectionChange(() => { if (opts.copyOnSelect && term.hasSelection()) copy(s); });
    term.attachCustomKeyEventHandler(ev => onKey(s, ev));
    el.addEventListener('contextmenu', ev => {
      // Programs that enable mouse reporting (tmux, nvim) get the click; otherwise show our menu.
      if (term.modes.mouseTrackingMode !== 'none' && !ev.shiftKey) return;
      ev.preventDefault();
      menuFor = s;
      for (const x of menu.querySelectorAll('.extra')) x.remove();
      menuAt = { x: ev.clientX, y: ev.clientY, time: Date.now() };
      post({ t: 'menu', id: s.id, sel: s.term.getSelection() || '' });
      menu.style.display = 'block';
      menu.style.left = Math.min(ev.clientX, innerWidth - menu.offsetWidth - 4) + 'px';
      menu.style.top = Math.min(ev.clientY, innerHeight - menu.offsetHeight - 4) + 'px';
    });
    connect(s);
    return s;
  }

  // Items the app adds to the open menu (plugins: AI Actions …): [{k, h, e, t, c: [...]} | {s: 1}].
  let menuAt = null;
  function addItems(container, items) {
    for (const item of items) {
      if (item.s) { const hr = document.createElement('hr'); hr.className = 'extra'; container.appendChild(hr); continue; }
      const d = document.createElement('div');
      d.className = 'extra' + (item.e === false ? ' off' : '');
      d.textContent = item.h;
      if (item.t) d.title = item.t;
      if (item.c && item.c.length) {
        d.classList.add('sub');
        const box = document.createElement('section');
        addItems(box, item.c);
        d.appendChild(box);
      } else if (item.e !== false) d.dataset.k = item.k;
      container.appendChild(d);
    }
  }
  window.addMenuItems = items => {
    if (!menuAt || menu.style.display !== 'block' || !items || !items.length || Date.now() - menuAt.time > 2000) return;
    const hr = document.createElement('hr'); hr.className = 'extra'; menu.appendChild(hr);
    addItems(menu, items);
    menu.style.left = Math.min(menuAt.x, innerWidth - menu.offsetWidth - 4) + 'px';
    menu.style.top = Math.max(4, Math.min(menuAt.y, innerHeight - menu.offsetHeight - 4)) + 'px';
  };
  menu.addEventListener('mousedown', ev => {
    const k = ev.target.closest('div[data-k]');
    if (k) { ev.preventDefault(); menu.style.display = 'none'; post({ t: 'menuItem', k: k.dataset.k }); return; }
    if (ev.target.closest('div.sub, div.off')) { ev.preventDefault(); return; }
    const a = ev.target.closest('div[data-a]');
    const s = menuFor && !menuFor.gone ? menuFor : sessions.get(active);
    menu.style.display = 'none';
    if (!a || !s) return;
    ev.preventDefault();
    const act = a.dataset.a;
    if (act === 'copy') copy(s);
    else if (act === 'paste') send(s, { t: 'paste' });
    else if (act === 'selectAll') s.term.selectAll();
    else if (act === 'clear') s.term.clear();
    s.term.focus();
  });
  document.addEventListener('mousedown', ev => { if (!menu.contains(ev.target)) menu.style.display = 'none'; });
  // Any press in the page closes the app's own menus (a tab's right-click menu): the click never reaches its window.
  document.addEventListener('pointerdown', () => post({ t: 'press' }), true);

  function refit(s) {
    try { s.fit.fit(); } catch { }
  }

  // ---- what is shown: one terminal, or the split panes of a tab ----
  // A layout is {id} for one terminal, or {dir: 'right'|'down', ratio, a, b}: a split giving `a` (left or top)
  // `ratio` of the room. Panes are placed in pixels (xterm fits itself to its element), with a divider in the gap
  // between the two sides of every split. Dragging a divider moves a guide line; the panes are resized (and the
  // programs in them told their new size) once, on release, rather than on every move.
  const GAP = 6, MIN = 60;
  let tree = null;
  let menuFor = null;
  const dividers = [];

  function leaves(n) { return n.id != null ? [String(n.id)] : leaves(n.a).concat(leaves(n.b)); }

  function place() {
    for (const d of dividers) d.remove();
    dividers.length = 0;
    if (!tree) return;
    root.classList.toggle('split', tree.id == null);
    (function walk(n, x, y, w, h, path) {
      if (n.id != null) {
        const s = sessions.get(String(n.id));
        if (!s) return;
        // The same margins as a lone terminal has (the .term inset): room on the left and above the text.
        const st = s.el.style;
        st.left = (x + 6) + 'px'; st.top = (y + 4) + 'px';
        st.width = Math.max(0, w - 6) + 'px'; st.height = Math.max(0, h - 4) + 'px';
        return;
      }
      const right = n.dir !== 'down', total = right ? w : h;
      const at = Math.round(Math.max(MIN, Math.min(total - MIN, total * n.ratio)));
      const d = document.createElement('div');
      d.className = 'divider ' + (right ? 'col' : 'row');
      d.title = 'Drag to resize; double-click to even out';
      Object.assign(d.style, right
        ? { left: (x + at - GAP / 2) + 'px', top: y + 'px', width: GAP + 'px', height: h + 'px' }
        : { left: x + 'px', top: (y + at - GAP / 2) + 'px', width: w + 'px', height: GAP + 'px' });
      d.addEventListener('pointerdown', ev => drag(ev, d, n, path, right, right ? x : y, total));
      d.addEventListener('dblclick', () => { n.ratio = 0.5; relayout(); post({ t: 'ratio', path, ratio: 0.5 }); });
      root.appendChild(d);
      dividers.push(d);
      if (right) { walk(n.a, x, y, at - GAP / 2, h, path + 'a'); walk(n.b, x + at + GAP / 2, y, w - at - GAP / 2, h, path + 'b'); }
      else { walk(n.a, x, y, w, at - GAP / 2, path + 'a'); walk(n.b, x, y + at + GAP / 2, w, h - at - GAP / 2, path + 'b'); }
    })(tree, 0, 0, root.clientWidth, root.clientHeight, '');
  }

  function drag(ev, d, n, path, right, start, total) {
    if (ev.button !== 0) return;
    ev.preventDefault();
    d.setPointerCapture(ev.pointerId);
    d.classList.add('dragging');
    let ratio = n.ratio;
    const move = e => {
      const r = root.getBoundingClientRect();
      const at = Math.max(MIN, Math.min(total - MIN, (right ? e.clientX - r.left : e.clientY - r.top) - start));
      ratio = at / total;
      d.style[right ? 'left' : 'top'] = (start + at - GAP / 2) + 'px';
    };
    const up = () => {
      d.removeEventListener('pointermove', move);
      d.removeEventListener('pointerup', up);
      d.removeEventListener('pointercancel', up);
      n.ratio = ratio;
      relayout();
      post({ t: 'ratio', path, ratio });
      const s = sessions.get(active);
      if (s) s.term.focus();
    };
    d.addEventListener('pointermove', move);
    d.addEventListener('pointerup', up);
    d.addEventListener('pointercancel', up);
  }

  function shown() { return tree ? leaves(tree).map(id => sessions.get(id)).filter(Boolean) : []; }

  function relayout() {
    place();
    for (const s of shown()) { refit(s); pulse(s); }
  }

  // The pane with the keyboard; the app is told, so its tab shows that pane and its menu acts on it.
  function focused(s) {
    for (const o of sessions.values()) o.el.classList.toggle('focused', o === s);
    if (active === s.id) return;
    active = s.id;
    if (tree && tree.id == null) post({ t: 'focus', id: s.id });
  }

  function layout(t, focus) {
    tree = t;
    const ids = leaves(t);
    for (const id of ids) open(id);
    const on = new Set(ids);
    for (const o of sessions.values()) o.el.classList.toggle('active', on.has(o.id));
    focus = on.has(String(focus)) ? String(focus) : ids[0];
    active = focus;
    for (const o of sessions.values()) o.el.classList.toggle('focused', o.id === focus);
    empty.style.display = 'none';
    relayout();
    sessions.get(focus).term.focus();
    return true;
  }

  function show(id) { return layout({ id: String(id) }, String(id)); }

  function remove(id) {
    const s = sessions.get(String(id));
    if (!s) return;
    s.gone = true;
    try { s.ws && s.ws.close(); } catch { }
    s.term.dispose();
    s.el.remove();
    sessions.delete(s.id);
    if (active === s.id) active = null;
    if (!sessions.size) empty.style.display = 'flex';
    return true;
  }

  function options(o) {
    opts = Object.assign(opts, o || {});
    chrome();
    for (const s of sessions.values()) {
      s.term.options.scrollback = opts.scrollback;
      s.term.options.fontFamily = fonts();
      s.term.options.fontSize = opts.fontSize;
      s.term.options.cursorBlink = opts.cursorBlink && !opts.pulse;
      s.term.options.theme = theme();
      refit(s);
      pulse(s);
    }
    return true;
  }

  let resizeTimer = null;
  new ResizeObserver(() => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => { relayout(); drawCrt(); }, 30);
  }).observe(root);
  if (document.fonts) {
    // Load the symbols font up front; xterm measures and paints as soon as a terminal opens.
    try { document.fonts.load('13px "avamux-symbols"', '\ue0b0').catch(() => { }); } catch { }
    if (document.fonts.ready) document.fonts.ready.then(() => { for (const s of sessions.values()) refit(s); });
    document.fonts.addEventListener && document.fonts.addEventListener('loadingdone', () => {
      for (const s of sessions.values()) { refit(s); s.term.refresh(0, s.term.rows - 1); }
    });
  }

  document.addEventListener('visibilitychange', () => { for (const s of sessions.values()) send(s, { t: 'vis', hidden: document.hidden }); });
  window.addEventListener('focus', () => { for (const s of sessions.values()) pulse(s); });
  window.addEventListener('blur', () => { for (const s of sessions.values()) pulse(s); });
  chrome();

  window.avamux = {
    show, layout, remove, options,
    open(id) { open(String(id)); return true; },
    focus() { const s = sessions.get(active); if (s) s.term.focus(); return true; },
    text(id, lines) { const s = sessions.get(String(id)); return s ? textOf(s, lines) : null; },
    screen(id) { const s = sessions.get(String(id)); return s ? screenOf(s) : null; },
    selection(id) { const s = sessions.get(String(id || active)); return s ? s.term.getSelection() : ''; },
    select(id, col, row, length) { const s = sessions.get(String(id)); if (s) s.term.select(col, row, length); },
    ids() { return Array.from(sessions.keys()); },
    active() { return active; }
  };
  post({ t: 'ready' });
})();
