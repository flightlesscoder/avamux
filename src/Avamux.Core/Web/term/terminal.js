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

  // Places the heartbeat cursor over the cell xterm's cursor is in, or hides it when there is none to show.
  function pulse(s) {
    const p = s.pulse, term = s.term, buf = term.buffer.active;
    s.el.classList.toggle('pulse-on', !!opts.pulse);
    let hidden = false;
    try { hidden = !!term._core.coreService.isCursorHidden; } catch { }
    const row = buf.baseY + buf.cursorY - buf.viewportY;
    const screen = s.el.querySelector('.xterm-screen');
    if (!opts.pulse || hidden || !screen || row < 0 || row >= term.rows) { p.className = 'pulse-cursor'; return; }
    if (p.parentNode !== screen) screen.insertBefore(p, screen.firstChild);
    const w = screen.clientWidth / term.cols, h = screen.clientHeight / term.rows;
    const shape = opts.pulseShape || 'block';
    const focused = document.hasFocus() && document.activeElement === term.textarea;
    p.className = 'pulse-cursor on ' + shape + (opts.pulseDouble ? ' double' : '') + (focused ? '' : ' idle');
    p.style.left = Math.min(buf.cursorX, term.cols - 1) * w + 'px';
    p.style.top = row * h + 'px';
    p.style.width = (shape === 'bar' ? Math.max(2, Math.round(w / 5)) : w) + 'px';
    p.style.height = h + 'px';
    p.style.setProperty('--pc', opts.pulseColor || opts.cursor || opts.foreground);
    p.style.setProperty('--pg', String((opts.pulseGlow == null ? 60 : opts.pulseGlow) / 100));
    p.style.setProperty('--pd', (60 / (opts.pulseRate || 30)) + 's');
  }

  function chrome() {
    document.body.style.background = opts.background;
    document.body.classList.toggle('crt-on', !!opts.crt);
    document.body.classList.toggle('crt-flicker', !!opts.crt && !!opts.crtFlicker);
    document.documentElement.style.setProperty('--ci', String((opts.crtIntensity == null ? 50 : opts.crtIntensity) / 100));
    const r = document.documentElement.style;
    r.setProperty('--panel', opts.panel || '#1f2330');
    r.setProperty('--line', opts.line || '#3b4252');
    r.setProperty('--text', opts.text || '#d8dee9');
    r.setProperty('--dim', opts.dim || '#7b8496');
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
      send(s, { t: 'r', cols: s.term.cols, rows: s.term.rows });
    };
    ws.onmessage = (ev) => {
      if (typeof ev.data !== 'string') { s.term.write(new Uint8Array(ev.data)); return; }
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      if (m.t === 'paste') { if (m.text) s.term.paste(m.text); }
      else if (m.t === 'dump') send(s, { t: 'dump', n: m.n, text: m.screen ? screenOf(s) : textOf(s, m.lines) });
      else if (m.t === 'exit') { s.exited = true; s.term.write(`\r\n\x1b[2m[process exited with code ${m.code}]\x1b[0m\r\n`); }
    };
    ws.onclose = () => { if (!s.gone && !s.exited) setTimeout(() => connect(s), 700); };
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

  function onKey(s, ev) {
    if (ev.type !== 'keydown') return true;
    const k = ev.key.toLowerCase();
    const copyCombo = (ev.ctrlKey && ev.shiftKey && k === 'c') || (isMac && ev.metaKey && k === 'c') || (ev.ctrlKey && ev.key === 'Insert');
    const pasteCombo = (ev.ctrlKey && ev.shiftKey && k === 'v') || (isMac && ev.metaKey && k === 'v') || (ev.shiftKey && ev.key === 'Insert');
    if (copyCombo) { copy(s); ev.preventDefault(); return false; }
    if (pasteCombo) { send(s, { t: 'paste' }); ev.preventDefault(); return false; }
    if (ev.ctrlKey && ev.shiftKey && (k === 't' || k === 'w')) { post({ t: k === 't' ? 'new' : 'close' }); ev.preventDefault(); return false; }
    if (ev.ctrlKey && (ev.key === 'PageUp' || ev.key === 'PageDown')) { post({ t: ev.key === 'PageUp' ? 'prev' : 'next' }); ev.preventDefault(); return false; }
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
    try { term.loadAddon(new WebLinksAddon.WebLinksAddon((e, uri) => send(s, { t: 'open', url: uri }))); } catch (e) { console.warn(e); }
    term.open(el);
    if (opts.renderer === 'webgl') {
      try { const gl = new WebglAddon.WebglAddon(); gl.onContextLoss(() => gl.dispose()); term.loadAddon(gl); } catch (e) { console.warn('webgl unavailable', e); }
    }
    const s = { id, el, term, fit, ws: null, exited: false, gone: false, pulse: document.createElement('div') };
    sessions.set(id, s);
    term.onRender(() => pulse(s));
    term.onScroll(() => pulse(s));
    term.textarea.addEventListener('focus', () => pulse(s));
    term.textarea.addEventListener('blur', () => pulse(s));
    try { fit.fit(); } catch { }
    term.onData(d => send(s, { t: 'i', d }));
    term.onBinary(d => send(s, { t: 'b', d: btoa(d) }));
    term.onResize(({ cols, rows }) => send(s, { t: 'r', cols, rows }));
    term.onTitleChange(title => send(s, { t: 'title', title }));
    term.onBell(() => send(s, { t: 'bell' }));
    term.onSelectionChange(() => { if (opts.copyOnSelect && term.hasSelection()) copy(s); });
    term.attachCustomKeyEventHandler(ev => onKey(s, ev));
    el.addEventListener('contextmenu', ev => {
      // Programs that enable mouse reporting (tmux, nvim) get the click; otherwise show our menu.
      if (term.modes.mouseTrackingMode !== 'none' && !ev.shiftKey) return;
      ev.preventDefault();
      menu.style.display = 'block';
      menu.style.left = Math.min(ev.clientX, innerWidth - menu.offsetWidth - 4) + 'px';
      menu.style.top = Math.min(ev.clientY, innerHeight - menu.offsetHeight - 4) + 'px';
    });
    connect(s);
    return s;
  }

  menu.addEventListener('mousedown', ev => {
    const a = ev.target.closest('div[data-a]');
    const s = sessions.get(active);
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

  function refit(s) {
    try { s.fit.fit(); } catch { }
  }

  function show(id) {
    id = String(id);
    const s = open(id);
    active = id;
    for (const o of sessions.values()) o.el.classList.toggle('active', o === s);
    empty.style.display = 'none';
    refit(s);
    s.term.focus();
    return true;
  }

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
    resizeTimer = setTimeout(() => { const s = sessions.get(active); if (s) refit(s); }, 30);
  }).observe(root);
  if (document.fonts) {
    // Load the symbols font up front; xterm measures and paints as soon as a terminal opens.
    try { document.fonts.load('13px "avamux-symbols"', '\ue0b0').catch(() => { }); } catch { }
    if (document.fonts.ready) document.fonts.ready.then(() => { for (const s of sessions.values()) refit(s); });
    document.fonts.addEventListener && document.fonts.addEventListener('loadingdone', () => {
      for (const s of sessions.values()) { refit(s); s.term.refresh(0, s.term.rows - 1); }
    });
  }

  window.addEventListener('focus', () => { for (const s of sessions.values()) pulse(s); });
  window.addEventListener('blur', () => { for (const s of sessions.values()) pulse(s); });
  chrome();

  window.avamux = {
    show, remove, options,
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
