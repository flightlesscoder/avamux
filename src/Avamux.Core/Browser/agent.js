// avamux browser agent. Runs inside pages shown in avamux browser panes: it records the console, errors and network
// requests from the start of the page, and gives the `avamux browser` commands their snapshot, refs and actions.
// It is added at document start where the platform allows (WebKitGTK) and otherwise on first use.
(function () {
  'use strict';
  var VERSION = '__AVX_VERSION__';
  var KEEP_ALIVE = __AVX_KEEP_ALIVE__;
  if (window.__avx && window.__avx.v === VERSION) { if (KEEP_ALIVE) window.__avx.keepAlive(true); return; }

  var A = { v: VERSION };
  var _setTimeout = window.setTimeout.bind(window);
  var _now = function () { return Date.now(); };
  var isTop = window === window.top;

  // ---- messages to the app ----------------------------------------------------------------------------------
  var handler = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.postAvWebViewMessage;
  var invoke = typeof window.invokeCSharpAction === 'function' ? window.invokeCSharpAction : null;
  function post(msg) {
    try {
      msg.avx = 1; msg.top = isTop;
      var s = JSON.stringify(msg);
      if (handler) handler.postMessage(s); else if (invoke) invoke(s);
    } catch (e) { /* the page is going away */ }
  }
  A.post = post;

  var queue = [], flushing = false, dropped = 0;
  function emit(kind, rec) {
    var ring = A[kind];
    ring.push(rec);
    if (ring.length > 1000) ring.splice(0, ring.length - 1000);
    if (queue.length < 300) queue.push([kind, rec]); else dropped++;
    if (!flushing) { flushing = true; _setTimeout(flush, 150); }
  }
  function flush() {
    flushing = false;
    if (!queue.length && !dropped) return;
    var batch = queue; queue = [];
    post({ k: 'log', items: batch, dropped: dropped, url: location.href });
    dropped = 0;
  }

  // ---- console and errors -----------------------------------------------------------------------------------
  A.console = []; A.errors = []; A.net = []; A.messages = [];
  function fmt(v, depth) {
    try {
      if (v === undefined) return 'undefined';
      if (v === null) return 'null';
      if (typeof v === 'string') return v;
      if (typeof v === 'function') return 'function ' + (v.name || '') + '()';
      if (v instanceof Error) return (v.stack && String(v.stack).indexOf(v.message) >= 0 ? v.stack : v.name + ': ' + v.message + (v.stack ? '\n' + v.stack : ''));
      if (typeof Element !== 'undefined' && v instanceof Element) return '<' + v.tagName.toLowerCase() + (v.id ? '#' + v.id : '') + (v.className && typeof v.className === 'string' ? '.' + v.className.trim().split(/\s+/).join('.') : '') + '>';
      if (typeof v === 'object') {
        var seen = [];
        var s = JSON.stringify(v, function (k, x) {
          if (typeof x === 'object' && x !== null) { if (seen.indexOf(x) >= 0) return '[Circular]'; seen.push(x); }
          if (typeof x === 'function') return '[Function]';
          return x;
        });
        return s === undefined ? String(v) : s;
      }
      return String(v);
    } catch (e) { try { return String(v); } catch (e2) { return '[unprintable]'; } }
  }
  function clip(s, n) { s = String(s); return s.length > n ? s.slice(0, n) + '…' : s; }
  ['log', 'info', 'warn', 'error', 'debug', 'trace', 'assert'].forEach(function (level) {
    var orig = console[level];
    if (typeof orig !== 'function') return;
    console[level] = function () {
      var args = Array.prototype.slice.call(arguments);
      try {
        if (level === 'assert') { if (args[0]) return orig.apply(console, args); args = ['Assertion failed:'].concat(args.slice(1)); }
        emit('console', { level: level === 'assert' ? 'error' : level, text: clip(args.map(function (a) { return fmt(a); }).join(' '), 4000), ts: _now() });
      } catch (e) { }
      return orig.apply(console, arguments);
    };
  });
  window.addEventListener('error', function (e) {
    var t = e.target;
    if (t && t !== window && t.tagName) {
      var src = t.src || t.href || t.currentSrc || '';
      emit('errors', { kind: 'resource', text: 'Failed to load <' + t.tagName.toLowerCase() + '> ' + src, ts: _now() });
      emit('net', { type: t.tagName.toLowerCase(), method: 'GET', url: src, failed: true, ts: _now() });
      return;
    }
    emit('errors', { kind: 'error', text: clip((e.error ? fmt(e.error) : e.message) || 'Script error', 4000), source: (e.filename || '') + (e.lineno ? ':' + e.lineno + ':' + e.colno : ''), ts: _now() });
  }, true);
  window.addEventListener('unhandledrejection', function (e) {
    emit('errors', { kind: 'rejection', text: clip('Unhandled promise rejection: ' + fmt(e.reason), 4000), ts: _now() });
  });

  // ---- messages posted to the main page --------------------------------------------------------------------
  // Commands cannot reach into a cross-origin iframe, but an init script running there can postMessage what it
  // finds to the main page; `avamux browser messages` reads them. Recorded only, never stopped or changed.
  if (isTop) window.addEventListener('message', function (e) {
    var from = 'window', frame = null;
    try {
      if (e.source === window) from = 'self';
      else if (e.source) {
        var frames = document.querySelectorAll('iframe, frame');
        for (var i = 0; i < frames.length; i++) {
          if (frames[i].contentWindow !== e.source) continue;
          var f = frames[i];
          var esc = window.CSS && CSS.escape ? CSS.escape : function (t) { return String(t).replace(/["\\]/g, '\\$&'); };
          frame = f.id ? '#' + esc(f.id) : f.name ? f.tagName.toLowerCase() + '[name="' + esc(f.name) + '"]' : clip(f.getAttribute('src') || f.tagName.toLowerCase(), 300);
          from = 'frame';
          break;
        }
        if (!frame && e.source.top === window) from = 'frame';
      }
    } catch (x) { }
    var rec = { origin: e.origin || '', from: from, data: clip(fmt(e.data), 4000), ts: _now() };
    if (frame) rec.frame = frame;
    emit('messages', rec);
  }, true);

  // ---- network -----------------------------------------------------------------------------------------------
  var inflight = 0, lastNet = _now();
  function netStart() { inflight++; lastNet = _now(); }
  function netEnd() { inflight = Math.max(0, inflight - 1); lastNet = _now(); }
  if (typeof window.fetch === 'function') {
    var origFetch = window.fetch;
    window.fetch = function (input, init) {
      var url = typeof input === 'string' ? input : (input && input.url) || String(input);
      var method = ((init && init.method) || (input && input.method) || 'GET').toUpperCase();
      var t0 = _now();
      netStart();
      try { url = new URL(url, location.href).href; } catch (e) { }
      return origFetch.apply(this, arguments).then(function (r) {
        netEnd();
        emit('net', { type: 'fetch', method: method, url: url, status: r.status, ok: r.ok, ms: _now() - t0, ts: t0 });
        return r;
      }, function (err) {
        netEnd();
        emit('net', { type: 'fetch', method: method, url: url, failed: true, error: String(err && err.message || err), ms: _now() - t0, ts: t0 });
        throw err;
      });
    };
  }
  if (window.XMLHttpRequest) {
    var XO = XMLHttpRequest.prototype.open, XS = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function (method, url) {
      this.__avx = { method: String(method || 'GET').toUpperCase(), url: String(url) };
      try { this.__avx.url = new URL(this.__avx.url, location.href).href; } catch (e) { }
      return XO.apply(this, arguments);
    };
    XMLHttpRequest.prototype.send = function () {
      var x = this, info = x.__avx || { method: 'GET', url: '' }, t0 = _now();
      netStart();
      x.addEventListener('loadend', function () {
        netEnd();
        emit('net', { type: 'xhr', method: info.method, url: info.url, status: x.status, ok: x.status >= 200 && x.status < 400, failed: x.status === 0, ms: _now() - t0, ts: t0 });
      });
      return XS.apply(this, arguments);
    };
  }
  try {
    var po = new PerformanceObserver(function (list) {
      list.getEntries().forEach(function (e) {
        if (e.initiatorType === 'fetch' || e.initiatorType === 'xmlhttprequest') return;
        lastNet = _now();
        var rec = { type: e.initiatorType || 'resource', method: 'GET', url: e.name, ms: Math.round(e.duration), size: e.transferSize || undefined, ts: Math.round(performance.timeOrigin + e.startTime) };
        if (e.responseStatus) rec.status = e.responseStatus;
        emit('net', rec);
      });
    });
    po.observe({ type: 'resource', buffered: true });
  } catch (e) { }

  // ---- keep alive: the page always believes it is visible and focused ----------------------------------------
  var keepAlive = false;
  function stopIfKept(e) { if (keepAlive && (e.type !== 'blur' || e.target === window)) e.stopImmediatePropagation(); }
  window.addEventListener('visibilitychange', stopIfKept, true);
  window.addEventListener('webkitvisibilitychange', stopIfKept, true);
  window.addEventListener('blur', stopIfKept, true);
  window.addEventListener('pagehide', function (e) { if (keepAlive && e.persisted === false) { /* real navigation: let it through */ } }, true);
  var realHasFocus = Document.prototype.hasFocus;
  A.keepAlive = function (on) {
    keepAlive = !!on;
    try {
      var d = document;
      if (keepAlive) {
        Object.defineProperty(d, 'hidden', { configurable: true, get: function () { return false; } });
        Object.defineProperty(d, 'visibilityState', { configurable: true, get: function () { return 'visible'; } });
        Object.defineProperty(d, 'webkitHidden', { configurable: true, get: function () { return false; } });
        Object.defineProperty(d, 'webkitVisibilityState', { configurable: true, get: function () { return 'visible'; } });
        d.hasFocus = function () { return true; };
      } else {
        delete d.hidden; delete d.visibilityState; delete d.webkitHidden; delete d.webkitVisibilityState;
        d.hasFocus = realHasFocus;
      }
    } catch (e) { }
    return keepAlive;
  };
  if (KEEP_ALIVE) A.keepAlive(true);

  // ---- keys the app handles (the page has the keyboard, so it tells the app) ---------------------------------
  window.addEventListener('keydown', function (e) {
    if (!isTop) return;
    var k = e.key, ctrl = e.ctrlKey || e.metaKey, act = null;
    if (k === 'F12' || (ctrl && e.shiftKey && (k === 'I' || k === 'i'))) act = 'devtools';
    else if (ctrl && !e.shiftKey && (k === 'l' || k === 'L')) act = 'address';
    else if (k === 'F5' || (ctrl && (k === 'r' || k === 'R'))) act = e.shiftKey || (ctrl && k === 'F5') ? 'hardreload' : 'reload';
    else if (e.altKey && k === 'ArrowLeft') act = 'back';
    else if (e.altKey && k === 'ArrowRight') act = 'forward';
    else if (ctrl && (k === '=' || k === '+')) act = 'zoomin';
    else if (ctrl && k === '-') act = 'zoomout';
    else if (ctrl && k === '0') act = 'zoomreset';
    else if (ctrl && e.shiftKey && (k === 'C' || k === 'c')) act = 'pick';
    else if (ctrl && e.shiftKey && (k === 'L' || k === 'l')) act = 'hide';
    else if (k === 'Escape' && picking) act = 'pickstop';
    if (!act) return;
    e.preventDefault(); e.stopPropagation();
    post({ k: 'key', action: act });
  }, true);
  window.addEventListener('mouseup', function (e) {
    if (!isTop) return;
    if (e.button === 3) post({ k: 'key', action: 'back' }); else if (e.button === 4) post({ k: 'key', action: 'forward' });
  }, true);

  // ---- errors handed back to the CLI ------------------------------------------------------------------------
  function fail(code, message) { var e = new Error(message); e.code = code; return e; }

  // ---- the document the commands work on (the main page, or a frame picked with `frame`) --------------------
  A.frame = null;
  function scopeWin() {
    if (!A.frame) return window;
    var f = A.frame;
    if (!f.isConnected) { A.frame = null; throw fail('not_found', 'The selected frame has gone. Use `frame main` or pick it again.'); }
    try { if (f.contentWindow && f.contentWindow.document) return f.contentWindow; } catch (e) { }
    throw fail('not_supported', 'That frame is from another site; commands cannot reach into it. Run a script inside it with `avamux browser addinitscript` (it can postMessage to the main page) and read what it sends with `avamux browser messages`.');
  }
  function scopeDoc() { return scopeWin().document; }
  A.selectFrame = function (target) {
    if (target === 'main' || target === 'top' || !target) { A.frame = null; return { frame: 'main' }; }
    var el = resolve(target);
    if (!/^i?frame$/i.test(el.tagName)) throw fail('invalid_params', target + ' is not a frame.');
    A.frame = el; scopeDoc();
    return { frame: target, url: el.contentWindow.location.href };
  };

  // ---- roles and names ----------------------------------------------------------------------------------------
  var INPUT_ROLES = { button: 'button', submit: 'button', reset: 'button', image: 'button', file: 'button', color: 'button', checkbox: 'checkbox', radio: 'radio', range: 'slider', number: 'spinbutton', search: 'searchbox', hidden: null };
  var INTERACTIVE = { link: 1, button: 1, textbox: 1, searchbox: 1, checkbox: 1, radio: 1, combobox: 1, listbox: 1, option: 1, menuitem: 1, menuitemcheckbox: 1, menuitemradio: 1, tab: 1, switch: 1, slider: 1, spinbutton: 1, treeitem: 1, gridcell: 0 };
  var FROM_CONTENT = { button: 1, link: 1, heading: 1, tab: 1, option: 1, menuitem: 1, menuitemcheckbox: 1, menuitemradio: 1, cell: 1, columnheader: 1, rowheader: 1, checkbox: 1, radio: 1, switch: 1, treeitem: 1, tooltip: 1, legend: 1, caption: 1 };
  var STRUCTURAL = { list: 1, listitem: 1, group: 1, region: 1, table: 1, row: 1, rowgroup: 1, cell: 1, paragraph: 1, article: 1, blockquote: 1, code: 1, separator: 1, figure: 1 };
  function role(el) {
    var r = el.getAttribute && el.getAttribute('role');
    if (r) { r = r.trim().split(/\s+/)[0].toLowerCase(); return r === 'none' || r === 'presentation' ? null : r; }
    var t = el.tagName.toLowerCase();
    switch (t) {
      case 'a': case 'area': return el.hasAttribute('href') ? 'link' : null;
      case 'button': case 'summary': return 'button';
      case 'input': { var ty = (el.getAttribute('type') || 'text').toLowerCase(); return ty in INPUT_ROLES ? INPUT_ROLES[ty] : (el.getAttribute('list') ? 'combobox' : 'textbox'); }
      case 'textarea': return 'textbox';
      case 'select': return el.multiple || el.size > 1 ? 'listbox' : 'combobox';
      case 'option': return 'option';
      case 'img': return el.getAttribute('alt') === '' ? null : 'img';
      case 'svg': return el.getAttribute('aria-label') || el.querySelector('title') ? 'img' : null;
      case 'h1': case 'h2': case 'h3': case 'h4': case 'h5': case 'h6': return 'heading';
      case 'nav': return 'navigation';
      case 'main': return 'main';
      case 'header': return el.closest('article,aside,main,nav,section') ? null : 'banner';
      case 'footer': return el.closest('article,aside,main,nav,section') ? null : 'contentinfo';
      case 'aside': return 'complementary';
      case 'form': return 'form';
      case 'section': return el.hasAttribute('aria-label') || el.hasAttribute('aria-labelledby') ? 'region' : null;
      case 'article': return 'article';
      case 'ul': case 'ol': case 'menu': return 'list';
      case 'li': return 'listitem';
      case 'table': return 'table';
      case 'tr': return 'row';
      case 'th': return el.getAttribute('scope') === 'row' ? 'rowheader' : 'columnheader';
      case 'td': return 'cell';
      case 'dialog': return 'dialog';
      case 'p': return 'paragraph';
      case 'details': case 'fieldset': return 'group';
      case 'iframe': case 'frame': return 'iframe';
      case 'progress': return 'progressbar';
      case 'meter': return 'meter';
      case 'hr': return 'separator';
      case 'blockquote': return 'blockquote';
      case 'pre': case 'code': return 'code';
      case 'figure': return 'figure';
      case 'output': return 'status';
      case 'video': return 'video';
      case 'audio': return 'audio';
      case 'canvas': return 'canvas';
    }
    if (el.isContentEditable && el.getAttribute('contenteditable') !== null) return 'textbox';
    return null;
  }
  function norm(s) { return String(s || '').replace(/\s+/g, ' ').trim(); }
  function textOf(el) { try { return norm(el.innerText !== undefined ? el.innerText : el.textContent); } catch (e) { return norm(el.textContent); } }
  function name(el, r) {
    var d = el.ownerDocument;
    var by = el.getAttribute('aria-labelledby');
    if (by) { var s = by.split(/\s+/).map(function (id) { var x = d.getElementById(id); return x ? textOf(x) : ''; }).join(' '); if (norm(s)) return norm(s); }
    var al = el.getAttribute('aria-label'); if (norm(al)) return norm(al);
    var t = el.tagName.toLowerCase();
    if (t === 'input' || t === 'textarea' || t === 'select') {
      var ty = (el.getAttribute('type') || '').toLowerCase();
      if (ty === 'button' || ty === 'submit' || ty === 'reset') return norm(el.value || (ty === 'submit' ? 'Submit' : ty === 'reset' ? 'Reset' : ''));
      if (ty === 'image') return norm(el.alt || el.value || 'Submit');
      if (el.labels && el.labels.length) { var l = Array.prototype.map.call(el.labels, textOf).join(' '); if (norm(l)) return norm(l); }
      if (el.placeholder) return norm(el.placeholder);
      if (el.title) return norm(el.title);
      return '';
    }
    if (t === 'img' || t === 'area') return norm(el.getAttribute('alt') || el.title);
    if (t === 'svg') { var st = el.querySelector('title'); return norm(st ? st.textContent : ''); }
    if (t === 'iframe') return norm(el.title || el.name);
    if (t === 'fieldset') { var lg = el.querySelector('legend'); if (lg) return textOf(lg); }
    if (t === 'table') { var cap = el.querySelector('caption'); if (cap) return textOf(cap); }
    if (FROM_CONTENT[r]) { var tx = textOf(el); if (tx) return tx; }
    if (el.title) return norm(el.title);
    return '';
  }
  function styleOf(el) { return (el.ownerDocument.defaultView || window).getComputedStyle(el); }
  function shown(el) {
    if (!el.isConnected) return false;
    if (el.hidden && el.tagName !== 'DIALOG') return false;
    var st = styleOf(el);
    if (st.display === 'none' || st.visibility === 'hidden' || st.visibility === 'collapse') return false;
    if (el.checkVisibility) { try { if (!el.checkVisibility({ checkVisibilityCSS: true })) return false; } catch (e) { } }
    var r = el.getBoundingClientRect();
    return r.width > 0 || r.height > 0 || st.display === 'contents';
  }
  function disabled(el) { return !!(el.disabled || el.getAttribute('aria-disabled') === 'true' || (el.closest && el.closest('fieldset[disabled]') && /^(INPUT|BUTTON|SELECT|TEXTAREA)$/.test(el.tagName))); }
  function interactive(el, r, cursor) {
    if (r && INTERACTIVE[r]) return true;
    if (el.tagName === 'VIDEO' || el.tagName === 'AUDIO') return el.controls;
    var ti = el.getAttribute('tabindex');
    if (ti !== null && +ti >= 0) return true;
    if (el.hasAttribute('onclick')) return true;
    if (el.isContentEditable && el.getAttribute('contenteditable') !== null) return true;
    if (cursor && styleOf(el).cursor === 'pointer' && el.parentElement && styleOf(el.parentElement).cursor !== 'pointer') return true;
    return false;
  }

  // ---- refs: e1, e2, … stable for an element as long as the page stays ----------------------------------------
  A.refs = new Map(); var elRef = new WeakMap(); var nextRef = 1;
  function refFor(el) {
    var r = elRef.get(el);
    if (r && A.refs.get(r) === el) return r;
    r = 'e' + nextRef++;
    elRef.set(el, r); A.refs.set(r, el);
    return r;
  }
  function pruneRefs() { A.refs.forEach(function (el, r) { if (!el.isConnected) A.refs.delete(r); }); }

  // ---- finding elements -----------------------------------------------------------------------------------------
  function resolve(target, opts) {
    var t = String(target == null ? '' : target).trim();
    if (!t) throw fail('invalid_params', 'No element given. Use a ref from `snapshot` (e5) or a CSS selector.');
    var m = /^@?(e\d+)$/.exec(t) || /^ref=(e\d+)$/.exec(t);
    if (m) {
      var el = A.refs.get(m[1]);
      if (!el || !el.isConnected) throw fail('not_found', 'Ref ' + m[1] + ' is unknown or stale (the page changed). Take a new `snapshot`.');
      return el;
    }
    var d = scopeDoc();
    if (/^text=/.test(t)) { var f = findAll('text', t.slice(5), {}); if (!f.length) throw fail('not_found', 'No element has the text ' + JSON.stringify(t.slice(5)) + '.'); return f[0]; }
    if (/^(xpath=|\/\/|\(\/\/)/.test(t)) {
      var xp = t.replace(/^xpath=/, '');
      var res = d.evaluate(xp, d, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
      if (!res) throw fail('not_found', 'No element matches the XPath ' + xp + '.');
      return res;
    }
    var found;
    try { found = d.querySelector(t); } catch (e) { throw fail('invalid_params', JSON.stringify(t) + ' is not a valid CSS selector (or ref like e5).'); }
    if (!found) throw fail('not_found', 'No element matches ' + t + '.');
    return found;
  }
  A.resolve = resolve;
  function describe(el) {
    var r = role(el) || el.tagName.toLowerCase();
    var n = name(el, r);
    return r + (n ? ' ' + JSON.stringify(clip(n, 80)) : '') + ' [ref=' + refFor(el) + ']';
  }

  function allElements(d) {
    var out = [];
    (function walk(root) {
      var w = d.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
      var n = w.currentNode.nodeType === 1 ? w.currentNode : w.nextNode();
      while (n) {
        out.push(n);
        if (n.shadowRoot) walk(n.shadowRoot);
        if (out.length > 50000) return;
        n = w.nextNode();
      }
    })(d.documentElement || d);
    return out;
  }
  function findAll(by, value, o) {
    var d = scopeDoc(), exact = !!o.exact, want = norm(value), wantL = want.toLowerCase();
    function match(s) { s = norm(s); return exact ? s === want : s.toLowerCase().indexOf(wantL) >= 0; }
    var all = allElements(d).filter(function (el) { return shown(el); });
    var res;
    switch (by) {
      case 'role':
        res = all.filter(function (el) { return role(el) === wantL && (!o.name || (function (n) { n = norm(n); return exact ? n === norm(o.name) : n.toLowerCase().indexOf(norm(o.name).toLowerCase()) >= 0; })(name(el, wantL))); });
        break;
      case 'text':
        // The innermost elements whose own text matches.
        res = all.filter(function (el) {
          if (/^(SCRIPT|STYLE|NOSCRIPT|HTML|BODY|HEAD)$/.test(el.tagName)) return false;
          if (!match(textOf(el))) return false;
          for (var c = el.firstElementChild; c; c = c.nextElementSibling) if (shown(c) && match(textOf(c))) return false;
          return true;
        });
        break;
      case 'label':
        res = all.filter(function (el) { return /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) || el.isContentEditable || el.getAttribute('aria-label') ? (function () { var r = role(el); return match(name(el, r)); })() : false; });
        break;
      case 'placeholder': res = all.filter(function (el) { return el.getAttribute('placeholder') !== null && match(el.getAttribute('placeholder')); }); break;
      case 'alt': res = all.filter(function (el) { return el.getAttribute('alt') !== null && match(el.getAttribute('alt')); }); break;
      case 'title': res = all.filter(function (el) { return el.getAttribute('title') !== null && match(el.getAttribute('title')); }); break;
      case 'testid': res = all.filter(function (el) { var v = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test'); return v !== null && (exact ? v === value : v.indexOf(value) >= 0); }); break;
      default: throw fail('invalid_params', 'Find by role, text, label, placeholder, alt, title or testid.');
    }
    return res;
  }
  A.find = function (by, value, o) {
    o = o || {};
    var res = findAll(by, value, o);
    if (o.nth != null) { var i = +o.nth; if (i < 0) i = res.length + i; res = res[i] ? [res[i]] : []; }
    else if (o.first) res = res.slice(0, 1);
    else if (o.last) res = res.slice(-1);
    return { count: res.length, matches: res.slice(0, 50).map(function (el) { return describe(el); }), refs: res.slice(0, 50).map(refFor) };
  };

  // ---- snapshot ---------------------------------------------------------------------------------------------------
  A.snapshot = function (o) {
    o = o || {};
    pruneRefs();
    var d = scopeDoc();
    var root = o.selector ? resolve(o.selector) : (d.body || d.documentElement);
    var lines = [], max = o.maxLines || 1500, more = 0, visited = 0;
    var maxDepth = o.maxDepth == null ? 1e9 : +o.maxDepth;
    function add(depth, s) { if (lines.length < max) lines.push(new Array(depth + 1).join('  ') + '- ' + s); else more++; }
    function attrs(el, r) {
      var a = [];
      if (r === 'heading') { var lv = el.getAttribute('aria-level') || (/^H(\d)$/.exec(el.tagName) || [])[1]; if (lv) a.push('level=' + lv); }
      if (r === 'checkbox' || r === 'radio' || r === 'switch' || r === 'menuitemcheckbox' || r === 'menuitemradio') {
        var ch = el.getAttribute('aria-checked'); if (ch === null && 'checked' in el) ch = el.checked ? 'true' : 'false';
        if (el.indeterminate) ch = 'mixed';
        if (ch === 'true') a.push('checked'); else if (ch === 'mixed') a.push('checked=mixed');
      }
      var ex = el.getAttribute('aria-expanded'); if (ex !== null) a.push('expanded=' + ex);
      if (el.tagName === 'DETAILS') a.push('expanded=' + el.open);
      var sel = el.getAttribute('aria-selected'); if (sel === 'true' || (el.tagName === 'OPTION' && el.selected)) a.push('selected');
      var pr = el.getAttribute('aria-pressed'); if (pr !== null) a.push('pressed=' + pr);
      if (disabled(el)) a.push('disabled');
      if (el.required || el.getAttribute('aria-required') === 'true') a.push('required');
      if (el === el.ownerDocument.activeElement && el !== el.ownerDocument.body) a.push('focused');
      return a;
    }
    function value(el, r) {
      var t = el.tagName;
      if (t === 'INPUT' || t === 'TEXTAREA') {
        if (/^(checkbox|radio|button|submit|reset|image|file)$/i.test(el.type)) { return el.type === 'file' && el.files && el.files.length ? Array.prototype.map.call(el.files, function (f) { return f.name; }).join(', ') : null; }
        if (el.type === 'password') return el.value ? '••••••' : '';
        return el.value;
      }
      if (t === 'SELECT') return Array.prototype.filter.call(el.options, function (x) { return x.selected; }).map(function (x) { return norm(x.text); }).join(', ');
      if (r === 'textbox' && el.isContentEditable) return clip(textOf(el), 200);
      if (r === 'slider' || r === 'spinbutton' || r === 'progressbar' || r === 'meter') return el.getAttribute('aria-valuenow') || el.value;
      return null;
    }
    function line(el, r, n) {
      var s = r + (n ? ' ' + JSON.stringify(clip(n, 120)) : '');
      var a = attrs(el, r); if (a.length) s += ' [' + a.join('] [') + ']';
      s += ' [ref=' + refFor(el) + ']';
      var v = value(el, r); if (v != null && v !== '' ) s += ': ' + clip(v, 200);
      else if (v === '' && (r === 'textbox' || r === 'searchbox')) s += ': ""';
      return s;
    }
    function walk(el, depth) {
      if (++visited > 40000 || depth > maxDepth) return;
      if (el.nodeType !== 1 || /^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE|HEAD|META|LINK)$/.test(el.tagName)) return;
      if (el.getAttribute('aria-hidden') === 'true') return;
      if (!shown(el)) return;
      var r = role(el), isInt = interactive(el, r, o.cursor);
      if (o.interactive) {
        if (isInt) { add(0, line(el, r || 'clickable', r ? name(el, r) : clip(textOf(el), 80))); }
        if (!isInt || !FROM_CONTENT[r]) childrenOf(el, depth);
        frame(el, depth);
        return;
      }
      var include = !!r && !(o.compact && STRUCTURAL[r] && !name(el, r) && !isInt);
      if (!r && isInt) { r = 'clickable'; include = true; }
      var n = include ? name(el, r) : '';
      if (include) {
        add(depth, line(el, r, n));
        if (r === 'link' && !o.compact && el.href) { if (lines.length < max) lines.push(new Array(depth + 2).join('  ') + '- /url: ' + clip(el.getAttribute('href'), 200)); }
        if (FROM_CONTENT[r] && n && !hasInteractive(el)) { frame(el, depth + 1); return; }
        // A field shows its value on its own line already.
        if (r === 'textbox' || r === 'searchbox' || r === 'combobox' || r === 'listbox' || r === 'slider' || r === 'spinbutton') return;
      }
      childrenOf(el, include ? depth + 1 : depth);
      frame(el, include ? depth + 1 : depth);
    }
    function hasInteractive(el) { return !!el.querySelector('a[href],button,input,select,textarea,[role=button],[role=link],[tabindex]'); }
    function frame(el, depth) {
      if (el.tagName !== 'IFRAME' && el.tagName !== 'FRAME') return;
      var cd = null; try { cd = el.contentDocument; } catch (e) { }
      if (cd && cd.body) walk(cd.body, depth); else if (!o.interactive) add(depth, '(frame from another site: ' + clip(el.src, 120) + ')');
    }
    function childrenOf(el, depth) {
      var text = '';
      var kids = el.shadowRoot ? [el.shadowRoot].concat(Array.prototype.slice.call(el.childNodes)) : el.childNodes;
      // A label's words are already the name of the field it labels.
      var quiet = el.tagName === 'LABEL' && el.control;
      function flushText() { var t = norm(text); text = ''; if (t && !quiet && !o.interactive && !o.compact) add(depth, 'text: ' + clip(t, 300)); }
      for (var i = 0; i < kids.length; i++) {
        var k = kids[i];
        if (k.nodeType === 3) { text += ' ' + k.nodeValue; continue; }
        if (k.nodeType === 11) { flushText(); for (var j = 0; j < k.childNodes.length; j++) if (k.childNodes[j].nodeType === 1) walk(k.childNodes[j], depth); continue; }
        if (k.nodeType !== 1) continue;
        // Inline formatting stays part of the surrounding text.
        if (!role(k) && !interactive(k, null, o.cursor) && /^(B|I|EM|STRONG|SPAN|SMALL|MARK|ABBR|SUB|SUP|U|S|TIME|BR|WBR|Q|CITE|KBD|VAR|DFN|BDI|BDO|FONT|LABEL)$/.test(k.tagName) && !k.querySelector('a,button,input,select,textarea,img,[role],[tabindex]')) {
          if (shown(k) && !(k.tagName === 'LABEL' && k.control)) text += k.tagName === 'BR' ? '\n' : ' ' + (k.innerText || k.textContent);
          continue;
        }
        flushText();
        walk(k, depth);
      }
      flushText();
    }
    walk(root, 0);
    var head = 'Page: ' + (d.title || '(no title)') + '\nURL: ' + d.location.href + (A.frame ? '\nFrame: ' + A.frame.src : '');
    var body = lines.length ? lines.join('\n') : (o.interactive ? '(no interactive elements)' : '(empty page)');
    if (more) body += '\n… ' + more + ' more lines. Narrow it with --selector, --interactive or --max-depth.';
    return { text: head + '\n' + body, refs: A.refs.size, lines: lines.length + more, url: d.location.href, title: d.title };
  };

  // ---- actions -------------------------------------------------------------------------------------------------------
  function winOf(el) { return el.ownerDocument.defaultView || window; }
  function center(el) {
    var r = el.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2, r: r };
  }
  function ready(el, o) {
    o = o || {};
    if (!el.isConnected) throw fail('not_found', 'The element left the page.');
    try { el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' }); } catch (e) { el.scrollIntoView(); }
    if (!o.force && !shown(el)) throw fail('invalid_state', describe(el) + ' is not visible.');
    if (!o.force && o.enabled !== false && disabled(el)) throw fail('invalid_state', describe(el) + ' is disabled.');
    return center(el);
  }
  function mouse(el, type, c, o) {
    var W = winOf(el);
    var init = { bubbles: true, cancelable: true, composed: true, view: W, clientX: c.x, clientY: c.y, screenX: c.x, screenY: c.y, button: o.button || 0, buttons: o.buttons || 0, detail: o.detail || 1,
      ctrlKey: !!o.ctrl, shiftKey: !!o.shift, altKey: !!o.alt, metaKey: !!o.meta, pointerId: 1, pointerType: 'mouse', isPrimary: true };
    var Ctor = /^pointer/.test(type) && W.PointerEvent ? W.PointerEvent : W.MouseEvent;
    return el.dispatchEvent(new Ctor(type, init));
  }
  function hoverSeq(el, c) {
    mouse(el, 'pointerover', c, {}); mouse(el, 'pointerenter', c, {}); mouse(el, 'mouseover', c, {}); mouse(el, 'mouseenter', c, {});
    mouse(el, 'pointermove', c, {}); mouse(el, 'mousemove', c, {});
  }
  function pointTarget(el, c) {
    // What the user would actually hit at the element's centre (a child, or the element itself).
    var hit = el.ownerDocument.elementFromPoint(c.x, c.y);
    return hit && (hit === el || el.contains(hit)) ? hit : el;
  }
  A.click = function (target, o) {
    o = o || {};
    var el = resolve(target), c = ready(el, o), t = pointTarget(el, c);
    var button = o.button === 'right' ? 2 : o.button === 'middle' ? 1 : 0, mask = button === 2 ? 2 : button === 1 ? 4 : 1;
    var count = o.count || 1;
    hoverSeq(t, c);
    for (var i = 1; i <= count; i++) {
      var opt = { button: button, buttons: mask, detail: i, ctrl: o.ctrl, shift: o.shift, alt: o.alt, meta: o.meta };
      var down = mouse(t, 'pointerdown', c, opt) && mouse(t, 'mousedown', c, opt);
      // A real press focuses what it lands on (unless the page prevented it, or focus is already inside it).
      var act = el.ownerDocument.activeElement;
      if (down && i === 1 && el.focus && act !== el && !el.contains(act)) { try { el.focus({ preventScroll: true }); } catch (e) { } }
      mouse(t, 'pointerup', c, { button: button, detail: i }); mouse(t, 'mouseup', c, { button: button, detail: i });
      if (button === 0) mouse(t, 'click', c, { button: 0, detail: i, ctrl: o.ctrl, shift: o.shift, alt: o.alt, meta: o.meta });
      else if (button === 1) mouse(t, 'auxclick', c, { button: 1, detail: i });
      else mouse(t, 'contextmenu', c, { button: 2, buttons: 2, detail: i });
    }
    if (count === 2 && button === 0) mouse(t, 'dblclick', c, { button: 0, detail: 2 });
    return { clicked: describe(el) };
  };
  A.hover = function (target) { var el = resolve(target), c = ready(el, { enabled: false }); hoverSeq(pointTarget(el, c), c); return { hovered: describe(el) }; };
  A.focus = function (target) { var el = resolve(target); ready(el, { enabled: false }); el.focus(); return { focused: describe(el) }; };
  A.blur = function (target) { var el = target ? resolve(target) : scopeDoc().activeElement; if (el && el.blur) el.blur(); return {}; };

  function setNativeValue(el, v) {
    var W = winOf(el);
    var proto = el.tagName === 'TEXTAREA' ? W.HTMLTextAreaElement.prototype : el.tagName === 'SELECT' ? W.HTMLSelectElement.prototype : W.HTMLInputElement.prototype;
    var desc = Object.getOwnPropertyDescriptor(proto, 'value');
    if (desc && desc.set) desc.set.call(el, v); else el.value = v;
  }
  function fire(el, type, extra) {
    var W = winOf(el);
    var e = type === 'input' || type === 'beforeinput' ? new W.InputEvent(type, Object.assign({ bubbles: true, cancelable: type === 'beforeinput', composed: true }, extra || {})) : new W.Event(type, { bubbles: true, cancelable: false });
    return el.dispatchEvent(e);
  }
  function editable(el) { return el.isContentEditable || /^(INPUT|TEXTAREA)$/.test(el.tagName) && !/^(checkbox|radio|button|submit|reset|image|file|range|color)$/i.test(el.type); }
  A.fill = function (target, text) {
    var el = resolve(target); ready(el, {});
    text = text == null ? '' : String(text);
    el.focus();
    if (el.tagName === 'SELECT') return A.select(target, [text]);
    if (el.tagName === 'INPUT' && /^(range|color|date|time|datetime-local|month|week|number)$/i.test(el.type) || el.tagName === 'INPUT' && el.type === 'file') {
      setNativeValue(el, text); fire(el, 'input'); fire(el, 'change'); return { filled: describe(el), value: el.value };
    }
    if (!editable(el)) throw fail('invalid_state', describe(el) + ' cannot be typed into.');
    if (el.readOnly) throw fail('invalid_state', describe(el) + ' is read-only.');
    if (el.isContentEditable) {
      var d = el.ownerDocument, sel = winOf(el).getSelection(), range = d.createRange();
      range.selectNodeContents(el); sel.removeAllRanges(); sel.addRange(range);
      if (!text) d.execCommand('delete', false); else if (!d.execCommand('insertText', false, text)) { el.textContent = text; fire(el, 'input'); }
      return { filled: describe(el) };
    }
    setNativeValue(el, text);
    fire(el, 'input', { inputType: 'insertReplacementText', data: text });
    fire(el, 'change');
    return { filled: describe(el), value: el.type === 'password' ? '••••••' : el.value };
  };

  // Keys: Playwright/W3C names (Enter, Tab, ArrowLeft, Control+a, Shift+Tab, Space, a, A, F5 …).
  var CODES = { Enter: 'Enter', Tab: 'Tab', Escape: 'Escape', Backspace: 'Backspace', Delete: 'Delete', ArrowLeft: 'ArrowLeft', ArrowRight: 'ArrowRight', ArrowUp: 'ArrowUp', ArrowDown: 'ArrowDown', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', ' ': 'Space', Shift: 'ShiftLeft', Control: 'ControlLeft', Alt: 'AltLeft', Meta: 'MetaLeft', Insert: 'Insert' };
  var KEYCODES = { Enter: 13, Tab: 9, Escape: 27, Backspace: 8, Delete: 46, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, Home: 36, End: 35, PageUp: 33, PageDown: 34, ' ': 32, Shift: 16, Control: 17, Alt: 18, Meta: 91, Insert: 45 };
  var ALIASES = { Space: ' ', Spacebar: ' ', space: ' ', Esc: 'Escape', Return: 'Enter', Left: 'ArrowLeft', Right: 'ArrowRight', Up: 'ArrowUp', Down: 'ArrowDown', Del: 'Delete', Ctrl: 'Control', Cmd: 'Meta', Command: 'Meta', Option: 'Alt', ControlOrMeta: 'Control' };
  A.mods = {};
  function parseKey(k) {
    var parts = String(k).split('+'), key = parts.pop();
    if (key === '' && parts.length) { parts.pop(); key = '+'; }
    key = ALIASES[key] || key;
    var mods = {};
    parts.forEach(function (p) { p = ALIASES[p] || p; if (p === 'Control') mods.ctrl = true; else if (p === 'Shift') mods.shift = true; else if (p === 'Alt') mods.alt = true; else if (p === 'Meta') mods.meta = true; else throw fail('invalid_params', 'Unknown modifier ' + p + '.'); });
    return { key: key, mods: mods };
  }
  function keyEvent(el, type, key, mods) {
    var W = winOf(el);
    var code = CODES[key] || (key.length === 1 ? (/[a-z]/i.test(key) ? 'Key' + key.toUpperCase() : /[0-9]/.test(key) ? 'Digit' + key : '') : key);
    var kc = KEYCODES[key] || (key.length === 1 ? key.toUpperCase().charCodeAt(0) : /^F\d+$/.test(key) ? 111 + +key.slice(1) : 0);
    var m = Object.assign({}, A.mods, mods || {});
    var e = new W.KeyboardEvent(type, { key: key, code: code, bubbles: true, cancelable: true, composed: true, ctrlKey: !!m.ctrl, shiftKey: !!m.shift, altKey: !!m.alt, metaKey: !!m.meta, keyCode: kc, which: kc, charCode: type === 'keypress' ? key.charCodeAt(0) : 0, view: W });
    return el.dispatchEvent(e);
  }
  function activeIn(d) { var a = d.activeElement; while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement; return a || d.body; }
  function focusables(d) {
    return allElements(d).filter(function (el) {
      if (!shown(el) || disabled(el)) return false;
      var ti = el.getAttribute('tabindex');
      if (ti !== null) return +ti >= 0;
      return /^(INPUT|SELECT|TEXTAREA|BUTTON)$/.test(el.tagName) && el.type !== 'hidden' || el.tagName === 'A' && el.hasAttribute('href') || el.isContentEditable && el.getAttribute('contenteditable') !== null || el.tagName === 'SUMMARY';
    });
  }
  function insertText(el, text) {
    var d = el.ownerDocument;
    if (!fire(el, 'beforeinput', { inputType: 'insertText', data: text })) return;
    if (d.execCommand && d.queryCommandSupported && d.execCommand('insertText', false, text)) return;
    if ('selectionStart' in el && el.setRangeText) {
      try { el.setRangeText(text, el.selectionStart, el.selectionEnd, 'end'); } catch (e) { setNativeValue(el, el.value + text); }
      fire(el, 'input', { inputType: 'insertText', data: text });
    } else if (el.isContentEditable) { el.textContent += text; fire(el, 'input', { inputType: 'insertText', data: text }); }
  }
  // What the browser itself would do for a real key press: synthetic events do not trigger it.
  function defaultAction(el, key, m) {
    var d = el.ownerDocument;
    if (m.ctrl || m.meta) {
      if (key.toLowerCase() === 'a' && editable(el)) { if (el.select) el.select(); else d.execCommand('selectAll'); }
      return;
    }
    if (key === 'Enter') {
      if (el.tagName === 'TEXTAREA' || el.isContentEditable) { insertText(el, '\n'); return; }
      if (el.tagName === 'INPUT' && el.form) { var sub = el.form.querySelector('[type=submit],button:not([type]),button[type=submit]'); if (sub && !disabled(sub)) sub.click(); else if (el.form.requestSubmit) el.form.requestSubmit(); else el.form.submit(); return; }
      if (el.tagName === 'A' || el.tagName === 'BUTTON' || el.tagName === 'SUMMARY' || role(el) === 'button' || role(el) === 'link') el.click();
      return;
    }
    if (key === ' ') {
      if (editable(el)) { insertText(el, ' '); return; }
      if (/^(BUTTON|SUMMARY)$/.test(el.tagName) || el.tagName === 'INPUT' && /^(checkbox|radio|button|submit|reset)$/i.test(el.type) || role(el) === 'button' || role(el) === 'checkbox') { el.click(); return; }
      winOf(el).scrollBy(0, winOf(el).innerHeight * 0.8 * (m.shift ? -1 : 1));
      return;
    }
    if (key === 'Tab') {
      var list = focusables(d), i = list.indexOf(el), next = list[(i + (m.shift ? -1 : 1) + list.length) % list.length];
      if (next) next.focus();
      return;
    }
    if (key === 'Backspace' || key === 'Delete') {
      if (!editable(el)) return;
      if (!fire(el, 'beforeinput', { inputType: key === 'Backspace' ? 'deleteContentBackward' : 'deleteContentForward' })) return;
      if (d.execCommand(key === 'Backspace' ? 'delete' : 'forwardDelete', false)) return;
      if ('selectionStart' in el) {
        var s = el.selectionStart, e2 = el.selectionEnd, v = el.value;
        if (s === e2) { if (key === 'Backspace' && s > 0) s--; else if (key === 'Delete' && e2 < v.length) e2++; }
        setNativeValue(el, v.slice(0, s) + v.slice(e2)); el.setSelectionRange(s, s); fire(el, 'input', { inputType: 'deleteContent' });
      }
      return;
    }
    if (/^Arrow|^Home$|^End$/.test(key) && 'selectionStart' in el && editable(el)) {
      var p = el.selectionStart, len = el.value.length;
      p = key === 'ArrowLeft' ? Math.max(0, p - 1) : key === 'ArrowRight' ? Math.min(len, p + 1) : key === 'Home' || key === 'ArrowUp' ? 0 : len;
      if (m.shift) el.setSelectionRange(Math.min(p, el.selectionEnd), Math.max(p, el.selectionStart)); else el.setSelectionRange(p, p);
      return;
    }
    var W = winOf(el), h = W.innerHeight;
    var dy = { ArrowDown: 40, ArrowUp: -40, PageDown: h * 0.85, PageUp: -h * 0.85 }[key];
    if (dy) { W.scrollBy(0, dy); return; }
    if (key === 'Home') W.scrollTo(W.scrollX, 0); else if (key === 'End') W.scrollTo(W.scrollX, d.documentElement.scrollHeight);
    else if (key === 'ArrowLeft') W.scrollBy(-40, 0); else if (key === 'ArrowRight') W.scrollBy(40, 0);
  }
  A.press = function (keys, target) {
    var d = scopeDoc();
    if (target) { var t = resolve(target); ready(t, {}); t.focus(); }
    var pressed = [];
    String(keys).split(/\s+/).filter(Boolean).forEach(function (k) {
      var p = parseKey(k), el = activeIn(d);
      var downMods = Object.assign({}, A.mods, p.mods);
      var proceed = keyEvent(el, 'keydown', p.key, p.mods);
      if (proceed && p.key.length === 1 && !downMods.ctrl && !downMods.meta && !downMods.alt) {
        if (keyEvent(el, 'keypress', p.key, p.mods) && editable(el)) insertText(el, downMods.shift ? p.key.toUpperCase() : p.key);
      } else if (proceed) defaultAction(el, p.key, downMods);
      keyEvent(el, 'keyup', p.key, p.mods);
      pressed.push(k);
    });
    return { pressed: pressed.join(' ') };
  };
  A.keydown = function (k) {
    var p = parseKey(k), el = activeIn(scopeDoc());
    if (p.key === 'Shift' || p.key === 'Control' || p.key === 'Alt' || p.key === 'Meta') A.mods[{ Shift: 'shift', Control: 'ctrl', Alt: 'alt', Meta: 'meta' }[p.key]] = true;
    keyEvent(el, 'keydown', p.key, p.mods);
    return { down: k, held: Object.keys(A.mods) };
  };
  A.keyup = function (k) {
    var p = parseKey(k), el = activeIn(scopeDoc());
    keyEvent(el, 'keyup', p.key, p.mods);
    if (p.key === 'Shift' || p.key === 'Control' || p.key === 'Alt' || p.key === 'Meta') delete A.mods[{ Shift: 'shift', Control: 'ctrl', Alt: 'alt', Meta: 'meta' }[p.key]];
    return { up: k, held: Object.keys(A.mods) };
  };
  A.type = function (target, text, o) {
    o = o || {};
    var el = target ? resolve(target) : activeIn(scopeDoc());
    if (target) { ready(el, {}); el.focus(); if (o.clear && editable(el)) A.fill(target, ''); }
    String(text).split('').forEach(function (ch) {
      var key = ch === '\n' ? 'Enter' : ch;
      var e = activeIn(el.ownerDocument);
      if (keyEvent(e, 'keydown', key, {})) {
        if (key === 'Enter') defaultAction(e, 'Enter', {});
        else if (keyEvent(e, 'keypress', key, {}) && editable(e)) insertText(e, ch);
      }
      keyEvent(e, 'keyup', key, {});
    });
    return { typed: text.length + ' characters', into: describe(el) };
  };
  A.select = function (target, values) {
    var el = resolve(target); ready(el, {});
    values = [].concat(values || []).map(String);
    if (el.tagName !== 'SELECT') {
      // A custom dropdown: open it, then click the option with that text.
      A.click(target, {});
      var opt = findAll('text', values[0], { exact: false }).filter(function (x) { return /option|menuitem|listitem/.test(role(x) || '') || x.closest('[role=listbox],[role=menu]'); })[0];
      if (!opt) throw fail('not_found', describe(el) + ' is not a <select>, and no option ' + JSON.stringify(values[0]) + ' appeared after clicking it.');
      A.click(refFor(opt), {});
      return { selected: [norm(textOf(opt))] };
    }
    var chosen = [];
    Array.prototype.forEach.call(el.options, function (op) {
      var hit = values.some(function (v) { return op.value === v || norm(op.text) === norm(v) || norm(op.label) === norm(v); });
      if (!el.multiple && chosen.length && hit) return;
      op.selected = hit; if (hit) chosen.push(norm(op.text));
    });
    if (!chosen.length) throw fail('not_found', 'No option ' + values.map(function (v) { return JSON.stringify(v); }).join(', ') + ' in ' + describe(el) + '. Options: ' + Array.prototype.map.call(el.options, function (o) { return JSON.stringify(norm(o.text)); }).slice(0, 30).join(', '));
    fire(el, 'input'); fire(el, 'change');
    return { selected: chosen };
  };
  A.check = function (target, on) {
    var el = resolve(target);
    var r = role(el);
    var checked = function () { var a = el.getAttribute('aria-checked'); return a !== null ? a === 'true' : !!el.checked; };
    if (checked() !== on) A.click(target, {});
    if (checked() !== on) {
      var inner = el.querySelector && el.querySelector('input[type=checkbox],input[type=radio]');
      if (inner) { inner.click(); }
    }
    if (checked() !== on && r !== 'radio') throw fail('invalid_state', describe(el) + ' did not change.');
    return { checked: checked() };
  };
  A.scroll = function (o) {
    o = o || {};
    var dx = +o.dx || 0, dy = +o.dy || 0;
    var dir = (o.direction || '').toLowerCase(), amt = o.amount != null ? +o.amount : null;
    var el = o.selector ? resolve(o.selector) : null, W = el ? winOf(el) : scopeWin();
    var h = el ? el.clientHeight : W.innerHeight, w = el ? el.clientWidth : W.innerWidth;
    if (dir === 'down') dy = amt == null ? h * 0.8 : amt; else if (dir === 'up') dy = -(amt == null ? h * 0.8 : amt);
    else if (dir === 'right') dx = amt == null ? w * 0.8 : amt; else if (dir === 'left') dx = -(amt == null ? w * 0.8 : amt);
    else if (dir === 'top') { if (el) el.scrollTop = 0; else W.scrollTo(W.scrollX, 0); }
    else if (dir === 'bottom') { if (el) el.scrollTop = el.scrollHeight; else W.scrollTo(W.scrollX, W.document.documentElement.scrollHeight); }
    else if (dir && !dx && !dy) throw fail('invalid_params', 'Scroll up, down, left, right, top or bottom (optionally with an amount in pixels), or give --dx/--dy.');
    if (!dx && !dy && !dir) dy = h * 0.8;
    if (dx || dy) { if (el) el.scrollBy(dx, dy); else W.scrollBy(dx, dy); }
    var s = el || W.document.scrollingElement || W.document.documentElement;
    return { scrollX: Math.round(el ? el.scrollLeft : W.scrollX), scrollY: Math.round(el ? el.scrollTop : W.scrollY), height: s.scrollHeight };
  };
  A.scrollIntoView = function (target) { var el = resolve(target); el.scrollIntoView({ block: 'center', inline: 'center' }); return { visible: shown(el), element: describe(el) }; };
  A.drag = function (source, target) {
    var a = resolve(source), b = resolve(target), ca = ready(a, {}), W = winOf(a);
    var dt; try { dt = new W.DataTransfer(); } catch (e) { dt = null; }
    var dragInit = function (c) { return { bubbles: true, cancelable: true, composed: true, clientX: c.x, clientY: c.y, dataTransfer: dt }; };
    hoverSeq(a, ca);
    mouse(a, 'pointerdown', ca, { buttons: 1 }); mouse(a, 'mousedown', ca, { buttons: 1 });
    if (W.DragEvent) a.dispatchEvent(new W.DragEvent('dragstart', dragInit(ca)));
    var cb = center(b);
    mouse(b, 'pointermove', cb, { buttons: 1 }); mouse(b, 'mousemove', cb, { buttons: 1 });
    if (W.DragEvent) { b.dispatchEvent(new W.DragEvent('dragenter', dragInit(cb))); b.dispatchEvent(new W.DragEvent('dragover', dragInit(cb))); b.dispatchEvent(new W.DragEvent('drop', dragInit(cb))); a.dispatchEvent(new W.DragEvent('dragend', dragInit(cb))); }
    mouse(b, 'pointerup', cb, {}); mouse(b, 'mouseup', cb, {});
    return { dragged: describe(a), onto: describe(b) };
  };
  A.upload = function (target, files) {
    var el = resolve(target);
    if (!(el.tagName === 'INPUT' && el.type === 'file')) {
      var inner = el.querySelector && el.querySelector('input[type=file]');
      if (!inner) throw fail('invalid_params', describe(el) + ' is not a file input.');
      el = inner;
    }
    var W = winOf(el), dt = new W.DataTransfer();
    files.forEach(function (f) {
      var bin = atob(f.data), bytes = new Uint8Array(bin.length);
      for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      dt.items.add(new W.File([bytes], f.name, { type: f.type || '' }));
    });
    el.files = dt.files;
    fire(el, 'input'); fire(el, 'change');
    return { uploaded: files.map(function (f) { return f.name; }), into: describe(el) };
  };

  // ---- reading ----------------------------------------------------------------------------------------------------------
  A.get = function (what, target, o) {
    o = o || {};
    var d = scopeDoc();
    switch (what) {
      case 'url': return d.location.href;
      case 'title': return d.title;
      case 'count': { if (!target) throw fail('invalid_params', 'count needs a selector.'); if (/^@?e\d+$/.test(target)) return A.refs.has(target.replace('@', '')) ? 1 : 0; return d.querySelectorAll(target).length; }
    }
    var el = target ? resolve(target) : d.body;
    switch (what) {
      case 'text': return el.innerText !== undefined ? el.innerText : el.textContent;
      case 'html': return o.outer ? el.outerHTML : el.innerHTML;
      case 'value': return el.tagName === 'SELECT' && el.multiple ? Array.prototype.filter.call(el.options, function (x) { return x.selected; }).map(function (x) { return x.value; }) : ('value' in el ? el.value : el.getAttribute('value'));
      case 'attr': if (!o.attr) throw fail('invalid_params', 'Say which attribute with --attr.'); return el.getAttribute(o.attr);
      case 'box': { var r = el.getBoundingClientRect(); return { x: Math.round(r.left), y: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height), scrollX: Math.round(winOf(el).scrollX), scrollY: Math.round(winOf(el).scrollY) }; }
      case 'styles': {
        var st = styleOf(el);
        if (o.property) return st.getPropertyValue(o.property);
        var keys = ['display', 'position', 'width', 'height', 'margin', 'padding', 'color', 'background-color', 'font-family', 'font-size', 'font-weight', 'line-height', 'border', 'border-radius', 'opacity', 'visibility', 'z-index', 'overflow', 'cursor', 'flex-direction', 'justify-content', 'align-items', 'gap'];
        var out = {}; keys.forEach(function (k) { out[k] = st.getPropertyValue(k); }); return out;
      }
      case 'ref': return refFor(el);
      case 'selector': return selectorFor(el);
    }
    throw fail('invalid_params', 'Get text, html, value, attr, count, box, styles, url or title.');
  };
  A.is = function (what, target) {
    var el = resolve(target);
    switch (what) {
      case 'visible': return shown(el);
      case 'hidden': return !shown(el);
      case 'enabled': return !disabled(el);
      case 'disabled': return disabled(el);
      case 'checked': { var a = el.getAttribute('aria-checked'); return a !== null ? a === 'true' : !!el.checked; }
      case 'editable': return editable(el) && !el.readOnly && !disabled(el);
      case 'focused': return el === activeIn(el.ownerDocument);
    }
    throw fail('invalid_params', 'Check visible, hidden, enabled, disabled, checked, editable or focused.');
  };

  // ---- waiting: one quick check; the app repeats it until it holds or time runs out --------------------------------------
  A.check_ = function (o) {
    var d;
    try { d = scopeDoc(); } catch (e) { return { ok: false, why: e.message }; }
    if (o.load) {
      var rs = document.readyState;
      if (o.load === 'domcontentloaded' && rs === 'loading') return { ok: false, why: 'document is still loading' };
      if ((o.load === 'load' || o.load === 'complete') && rs !== 'complete') return { ok: false, why: 'document is ' + rs };
      if (o.load === 'networkidle' && (rs !== 'complete' || inflight > 0 || _now() - lastNet < 500)) return { ok: false, why: inflight + ' requests in flight' };
    }
    if (o.urlContains && location.href.indexOf(o.urlContains) < 0) return { ok: false, why: 'URL is ' + location.href };
    if (o.url) {
      var re = new RegExp('^' + o.url.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*\*/g, '\u0000').replace(/\*/g, '[^/]*').replace(/\u0000/g, '.*').replace(/\?/g, '.') + '$');
      if (!re.test(location.href)) return { ok: false, why: 'URL is ' + location.href };
    }
    if (o.text != null) {
      var body = d.body ? (d.body.innerText || d.body.textContent || '') : '';
      if (body.indexOf(o.text) < 0) return { ok: false, why: 'text not on the page yet' };
    }
    if (o.selector) {
      var el = null;
      try { el = resolve(o.selector); } catch (e) { if (e.code !== 'not_found') throw e; }
      var state = o.state || 'visible';
      if (state === 'attached' && !el) return { ok: false, why: 'no match yet' };
      if (state === 'detached' && el) return { ok: false, why: 'still on the page' };
      if (state === 'visible' && !(el && shown(el))) return { ok: false, why: el ? 'present but not visible' : 'no match yet' };
      if (state === 'hidden' && el && shown(el)) return { ok: false, why: 'still visible' };
      if (state === 'enabled' && !(el && !disabled(el))) return { ok: false, why: el ? 'disabled' : 'no match yet' };
    }
    return { ok: true, url: location.href };
  };
  A.idle = function () { return { inflight: inflight, quietMs: _now() - lastNet, readyState: document.readyState }; };

  // ---- async results: a promise is parked here and the app collects it --------------------------------------------------
  var pending = {}, pendingId = 0;
  A.park = function (p) {
    var id = ++pendingId;
    pending[id] = { done: false };
    Promise.resolve(p).then(function (v) { pending[id] = { done: true, ok: true, v: v }; }, function (e) { pending[id] = { done: true, ok: false, e: e && e.message ? e.message : String(e) }; });
    return id;
  };
  A.collect = function (id) { var p = pending[id]; if (!p) return { done: true, ok: false, e: 'The page navigated before the result was ready.' }; if (p.done) delete pending[id]; return p; };

  // ---- storage and cookies --------------------------------------------------------------------------------------------
  A.storage = function (kind, action, key, value) {
    var s = kind === 'session' ? sessionStorage : localStorage;
    if (action === 'get' || !action) {
      if (key) return s.getItem(key);
      var o = {}; for (var i = 0; i < s.length; i++) { var k = s.key(i); o[k] = s.getItem(k); } return o;
    }
    if (action === 'set') { if (!key) throw fail('invalid_params', 'storage set needs a key and a value.'); s.setItem(key, value == null ? '' : String(value)); return { set: key }; }
    if (action === 'remove' || action === 'delete') { s.removeItem(key); return { removed: key }; }
    if (action === 'clear') { var n = s.length; s.clear(); return { cleared: n }; }
    throw fail('invalid_params', 'Use get, set, remove or clear.');
  };
  A.cookies = function () {
    return document.cookie ? document.cookie.split(/;\s*/).map(function (c) { var i = c.indexOf('='); return { name: decodeURIComponent(i < 0 ? c : c.slice(0, i)), value: i < 0 ? '' : c.slice(i + 1) }; }) : [];
  };
  A.setCookie = function (n, v, o) {
    o = o || {};
    var s = encodeURIComponent(n) + '=' + (v == null ? '' : v) + '; path=' + (o.path || '/');
    if (o.domain) s += '; domain=' + o.domain;
    if (o.expires) s += '; expires=' + new Date(o.expires).toUTCString();
    if (o.maxAge != null) s += '; max-age=' + o.maxAge;
    if (o.secure) s += '; secure';
    if (o.sameSite) s += '; samesite=' + o.sameSite;
    document.cookie = s;
    return { set: n };
  };
  A.clearCookies = function (o) {
    o = o || {};
    var names = A.cookies().map(function (c) { return c.name; }).filter(function (n) { return !o.name || n === o.name; });
    var host = location.hostname, parts = host.split('.'), domains = [''];
    for (var i = 0; i < parts.length - 1; i++) domains.push('; domain=' + parts.slice(i).join('.'), '; domain=.' + parts.slice(i).join('.'));
    var paths = ['/'], segs = location.pathname.split('/');
    for (var j = 2; j <= segs.length; j++) paths.push(segs.slice(0, j).join('/') || '/');
    if (o.path) paths = [o.path];
    names.forEach(function (n) { domains.forEach(function (dm) { paths.forEach(function (p) { document.cookie = encodeURIComponent(n) + '=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=' + p + dm; }); }); });
    return { cleared: names.length - A.cookies().filter(function (c) { return names.indexOf(c.name) >= 0; }).length };
  };
  A.state = function () { return { url: location.href, origin: location.origin, cookies: A.cookies(), local: A.storage('local', 'get'), session: A.storage('session', 'get') }; };
  A.loadState = function (st) {
    (st.cookies || []).forEach(function (c) { A.setCookie(c.name, c.value, {}); });
    Object.keys(st.local || {}).forEach(function (k) { localStorage.setItem(k, st.local[k]); });
    Object.keys(st.session || {}).forEach(function (k) { sessionStorage.setItem(k, st.session[k]); });
    return { cookies: (st.cookies || []).length, local: Object.keys(st.local || {}).length, session: Object.keys(st.session || {}).length };
  };

  // ---- a unique CSS selector for an element ----------------------------------------------------------------------------
  function cssEscape(s) { return window.CSS && CSS.escape ? CSS.escape(s) : String(s).replace(/[^a-zA-Z0-9_-]/g, '\\$&'); }
  function selectorFor(el) {
    var d = el.ownerDocument;
    function unique(sel) { try { return d.querySelectorAll(sel).length === 1; } catch (e) { return false; } }
    if (el.id && unique('#' + cssEscape(el.id))) return '#' + cssEscape(el.id);
    var tid = el.getAttribute('data-testid'); if (tid && unique('[data-testid="' + tid + '"]')) return '[data-testid="' + tid + '"]';
    var parts = [];
    for (var n = el; n && n.nodeType === 1 && n !== d.documentElement; n = n.parentElement) {
      var part = n.tagName.toLowerCase();
      if (n.id && unique('#' + cssEscape(n.id))) { parts.unshift('#' + cssEscape(n.id)); break; }
      var nm = n.getAttribute('name');
      if (nm && /^(INPUT|SELECT|TEXTAREA|BUTTON|FORM)$/.test(n.tagName)) part += '[name="' + nm + '"]';
      else {
        var same = n.parentElement ? Array.prototype.filter.call(n.parentElement.children, function (c) { return c.tagName === n.tagName; }) : [];
        if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(n) + 1) + ')';
      }
      parts.unshift(part);
      if (unique(parts.join(' > '))) break;
    }
    return parts.join(' > ');
  }
  A.selectorFor = selectorFor;

  // ---- highlight -------------------------------------------------------------------------------------------------------
  function overlayBox(el, color, label) {
    var r = el.getBoundingClientRect(), d = el.ownerDocument;
    var box = d.createElement('div');
    box.setAttribute('data-avx-overlay', '');
    box.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;box-sizing:border-box;border:2px solid ' + color + ';background:' + color.replace(')', ',0.12)').replace('rgb(', 'rgba(') + ';border-radius:3px;' +
      'left:' + r.left + 'px;top:' + r.top + 'px;width:' + r.width + 'px;height:' + r.height + 'px;transition:opacity .3s';
    if (label) {
      var tag = d.createElement('div');
      tag.textContent = label;
      tag.style.cssText = 'position:absolute;left:-2px;top:-20px;background:' + color + ';color:#fff;font:600 11px/18px system-ui,sans-serif;padding:0 6px;border-radius:3px;white-space:nowrap';
      if (r.top < 22) tag.style.top = '100%';
      box.appendChild(tag);
    }
    (d.body || d.documentElement).appendChild(box);
    return box;
  }
  A.highlight = function (target, ms) {
    var el = resolve(target);
    el.scrollIntoView({ block: 'center', inline: 'center' });
    var box = overlayBox(el, 'rgb(236,72,153)', describe(el));
    _setTimeout(function () { box.style.opacity = '0'; _setTimeout(function () { box.remove(); }, 400); }, ms || 2000);
    return { highlighted: describe(el) };
  };

  // ---- design mode: point at an element, click it, and its details go to the clipboard ------------------------------
  var picking = false, pickBox = null, pickBanner = null, pickEl = null;
  function pickInfo(el) {
    var st = styleOf(el), r = el.getBoundingClientRect();
    var styles = {};
    ['display', 'position', 'width', 'height', 'margin', 'padding', 'color', 'background-color', 'font-family', 'font-size', 'font-weight', 'line-height', 'border', 'border-radius', 'box-shadow', 'opacity', 'z-index', 'flex', 'grid-template-columns', 'gap'].forEach(function (k) { var v = st.getPropertyValue(k); if (v && v !== 'none' && v !== 'normal' && v !== 'auto' && v !== '0px') styles[k] = v; });
    var html = el.outerHTML;
    return { url: location.href, selector: selectorFor(el), ref: refFor(el), element: describe(el), tag: el.tagName.toLowerCase(), id: el.id || undefined,
      classes: el.className && typeof el.className === 'string' ? el.className.trim().split(/\s+/).filter(Boolean) : [], text: clip(textOf(el), 400),
      html: clip(html, 4000), box: { x: Math.round(r.left), y: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height) }, styles: styles };
  }
  function onPickMove(e) {
    var el = e.target;
    if (!el || el === pickBanner || (pickBanner && pickBanner.contains(el)) || el.hasAttribute && el.hasAttribute('data-avx-overlay')) return;
    pickEl = el;
    if (pickBox) pickBox.remove();
    pickBox = overlayBox(el, 'rgb(59,130,246)', (role(el) || el.tagName.toLowerCase()) + (el.id ? '#' + el.id : '') + '  ' + Math.round(el.getBoundingClientRect().width) + '×' + Math.round(el.getBoundingClientRect().height));
  }
  function onPickClick(e) {
    if (!picking) return;
    if (pickBanner && pickBanner.contains(e.target)) return;
    e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation();
    var el = pickEl || e.target;
    post({ k: 'pick', data: pickInfo(el) });
    if (pickBox) { pickBox.style.borderColor = 'rgb(34,197,94)'; }
  }
  function swallow(e) { if (picking && !(pickBanner && pickBanner.contains(e.target))) { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); } }
  A.designMode = function (on) {
    if (!isTop) return false;
    if (on === undefined) return picking;
    if (on && !picking) {
      picking = true;
      document.addEventListener('mousemove', onPickMove, true);
      document.addEventListener('click', onPickClick, true);
      ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick', 'auxclick', 'contextmenu'].forEach(function (t) { document.addEventListener(t, swallow, true); });
      pickBanner = document.createElement('div');
      pickBanner.setAttribute('data-avx-overlay', '');
      pickBanner.textContent = 'Design mode: click an element to copy its details for your agent. Esc to stop.';
      pickBanner.style.cssText = 'position:fixed;z-index:2147483647;left:50%;top:8px;transform:translateX(-50%);background:rgb(30,41,59);color:#fff;font:500 12px/1.4 system-ui,sans-serif;padding:6px 12px;border-radius:6px;box-shadow:0 4px 14px rgba(0,0,0,.35);pointer-events:none';
      (document.body || document.documentElement).appendChild(pickBanner);
    } else if (!on && picking) {
      picking = false;
      document.removeEventListener('mousemove', onPickMove, true);
      document.removeEventListener('click', onPickClick, true);
      ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick', 'auxclick', 'contextmenu'].forEach(function (t) { document.removeEventListener(t, swallow, true); });
      if (pickBox) pickBox.remove(); if (pickBanner) pickBanner.remove();
      pickBox = pickBanner = pickEl = null;
    }
    return picking;
  };

  // ---- find in page -------------------------------------------------------------------------------------------------------
  A.findInPage = function (text, backwards) {
    if (!text) { var s = window.getSelection(); if (s) s.removeAllRanges(); return { found: false }; }
    var found = window.find ? window.find(text, false, !!backwards, true, false, false, false) : false;
    var count = 0;
    try { var body = document.body.innerText.toLowerCase(), t = text.toLowerCase(), i = -1; while ((i = body.indexOf(t, i + 1)) >= 0 && count < 9999) count++; } catch (e) { }
    return { found: found, count: count };
  };

  // What a command result becomes on its way back: plain JSON (elements are described, cycles flattened).
  A.describe = describe;
  A.out = function (v) {
    if (v === undefined) return null;
    if (typeof Node !== 'undefined' && v instanceof Node) return v.nodeType === 1 ? describe(v) : String(v.nodeValue == null ? v.nodeName : v.nodeValue);
    if (typeof v === 'function') return 'function ' + (v.name || '') + '()';
    if (v === window) return '[Window ' + location.href + ']';
    try { var s = JSON.stringify(v); return s === undefined ? String(v) : JSON.parse(s); } catch (e) { return fmt(v); }
  };
  var collectRaw = A.collect;
  A.collect = function (id) { var p = collectRaw(id); if (p.done && p.ok) p = { done: true, ok: true, v: A.out(p.v) }; return p; };

  A.info = function () {
    return { url: location.href, title: document.title, readyState: document.readyState, keepAlive: keepAlive, designMode: picking, refs: A.refs.size,
      frame: A.frame ? A.frame.src : null, console: A.console.length, errors: A.errors.length, requests: A.net.length, inflight: inflight };
  };

  Object.defineProperty(window, '__avx', { value: A, configurable: true, enumerable: false, writable: true });
  if (isTop) post({ k: 'hello', url: location.href });
})();
