// avamux wiki preview: receives rendered HTML from the app, and handles links, pictures (zoom, pan, right-click
// menu) and scroll position reporting.
(function () {
  'use strict';
  const baseEl = document.getElementById('base');
  const content = document.getElementById('content');
  const menu = document.getElementById('menu');
  const lightbox = document.getElementById('lightbox');
  let current = null;      // wiki path of the file being shown
  let restoring = false;   // true while a programmatic scroll is settling
  let drag = null;         // the picture being panned
  let lightboxView = null;

  function post(o) { if (window.invokeCSharpAction) window.invokeCSharpAction(JSON.stringify(o)); }

  /** Wiki-relative path for a URL served from /wiki/raw/, or null for anything else. */
  function wikiPath(url) {
    try {
      const u = new URL(url, document.baseURI);
      if (u.origin !== location.origin || !u.pathname.startsWith('/wiki/raw/')) return null;
      return decodeURIComponent(u.pathname.slice('/wiki/raw/'.length));
    } catch { return null; }
  }

  window.setTheme = function (css) { document.getElementById('theme').textContent = ':root { ' + css + ' }'; return true; };

  window.setContent = function (html, folder, path, scroll) {
    const same = path === current;
    const y = same ? window.scrollY : (scroll || 0);
    current = path;
    baseEl.href = '/wiki/raw/' + (folder ? folder.split('/').map(encodeURIComponent).join('/') + '/' : '');
    closeLightbox();
    hideMenu();
    content.innerHTML = html;
    document.body.classList.toggle('wide', !!content.querySelector('.imgview, iframe.doc, video.media, pre.file, .tablewrap'));
    for (const host of content.querySelectorAll('.imgview')) mountViewer(host, host.dataset.src, false);
    addCopyButtons();
    restoring = true;
    window.scrollTo(0, y);
    if (!same && y > 0) {
      // Pictures change the page height as they load; keep returning to the remembered place until they have.
      for (const img of content.querySelectorAll('img')) img.addEventListener('load', () => { if (restoring) window.scrollTo(0, y); }, { once: true });
    }
    setTimeout(() => { restoring = false; }, same ? 150 : 700);
    return true;
  };

  // ---- code blocks ----

  const copyIcon = '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true"><path fill="currentColor" d="M19 21H8V7h11m0-2H8a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2m-3-4H4a2 2 0 0 0-2 2v14h2V3h12V1Z"/></svg>';
  const doneIcon = '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true"><path fill="currentColor" d="M21 7 9 19l-5.5-5.5 1.41-1.41L9 16.17 19.59 5.59 21 7Z"/></svg>';

  /** Wraps every code block so a copy button can sit in its corner without scrolling away with a long line. */
  function addCopyButtons() {
    for (const pre of content.querySelectorAll('pre')) {
      const wrap = document.createElement('div');
      wrap.className = 'codewrap';
      pre.parentNode.insertBefore(wrap, pre);
      wrap.appendChild(pre);
      const button = document.createElement('button');
      button.className = 'copycode';
      button.type = 'button';
      button.title = 'Copy to clipboard';
      button.setAttribute('aria-label', 'Copy code to clipboard');
      button.innerHTML = copyIcon;
      let timer = null;
      button.addEventListener('click', e => {
        e.stopPropagation();
        post({ t: 'copyText', text: pre.textContent.replace(/\n+$/, '') });
        button.innerHTML = doneIcon;
        button.classList.add('done');
        button.title = 'Copied';
        clearTimeout(timer);
        timer = setTimeout(() => { button.innerHTML = copyIcon; button.classList.remove('done'); button.title = 'Copy to clipboard'; }, 1500);
      });
      wrap.appendChild(button);
    }
  }

  window.scrollToLine = function (fraction) {
    window.scrollTo(0, Math.max(0, (document.documentElement.scrollHeight - innerHeight) * fraction));
    return true;
  };

  let scrollTimer = null;
  window.addEventListener('scroll', () => {
    if (restoring || !current) return;
    clearTimeout(scrollTimer);
    const path = current;
    scrollTimer = setTimeout(() => post({ t: 'scroll', path, y: Math.round(window.scrollY) }), 250);
  }, { passive: true });

  // ---- zoom and pan ----

  function mountViewer(host, src, closable) {
    host.innerHTML = '';
    const stage = document.createElement('div');
    stage.className = 'stage';
    const img = document.createElement('img');
    img.src = src;
    img.draggable = false;
    stage.appendChild(img);
    const bar = document.createElement('div');
    bar.className = 'zoombar';
    bar.innerHTML = '<button data-z="out" title="Zoom out (-)">−</button><span class="pct">100%</span><button data-z="in" title="Zoom in (+)">+</button>' +
      '<button data-z="fit" title="Fit to the view (0)">Fit</button><button data-z="one" title="Actual size (1)">1:1</button>' +
      (closable ? '<button data-z="close" title="Close (Esc)">✕</button>' : '');
    host.appendChild(stage);
    host.appendChild(bar);
    const pct = bar.querySelector('.pct');
    const v = { stage, img, scale: 1, x: 0, y: 0, fitScale: 1 };

    function apply() {
      img.style.transform = `translate(${v.x}px, ${v.y}px) scale(${v.scale})`;
      img.classList.toggle('pixel', v.scale >= 4);
      pct.textContent = Math.round(v.scale * 100) + '%';
    }
    function size() { return { w: img.naturalWidth || 512, h: img.naturalHeight || 512 }; }
    v.fit = function () {
      const r = stage.getBoundingClientRect();
      const { w, h } = size();
      v.fitScale = Math.min(1, (r.width - 24) / w, (r.height - 24) / h);
      v.scale = v.fitScale > 0 ? v.fitScale : 1;
      v.x = (r.width - w * v.scale) / 2;
      v.y = (r.height - h * v.scale) / 2;
      apply();
    };
    /** Multiplies the zoom by f, keeping the point (cx, cy) of the stage under the cursor. */
    v.zoomAt = function (f, cx, cy) {
      const next = Math.min(64, Math.max(0.02, v.scale * f));
      const k = next / v.scale;
      v.x = cx - (cx - v.x) * k;
      v.y = cy - (cy - v.y) * k;
      v.scale = next;
      apply();
    };
    v.zoom = function (f) { const r = stage.getBoundingClientRect(); v.zoomAt(f, r.width / 2, r.height / 2); };
    v.actual = function () { v.zoom(1 / v.scale); };
    v.pan = function (dx, dy) { v.x += dx; v.y += dy; apply(); };

    stage.addEventListener('wheel', ev => {
      ev.preventDefault();
      const r = stage.getBoundingClientRect();
      v.zoomAt(Math.exp(-ev.deltaY * 0.0016), ev.clientX - r.left, ev.clientY - r.top);
    }, { passive: false });
    stage.addEventListener('mousedown', ev => {
      if (ev.button !== 0) return;
      drag = { v, sx: ev.clientX - v.x, sy: ev.clientY - v.y, moved: false };
      stage.classList.add('grab');
      ev.preventDefault();
    });
    stage.addEventListener('dblclick', ev => {
      const r = stage.getBoundingClientRect();
      if (Math.abs(v.scale - v.fitScale) < 0.001 && v.fitScale < 1) v.zoomAt(1 / v.scale, ev.clientX - r.left, ev.clientY - r.top);
      else v.fit();
    });
    bar.addEventListener('mousedown', ev => ev.stopPropagation());
    bar.addEventListener('click', ev => {
      const b = ev.target.closest('button'); if (!b) return;
      const z = b.dataset.z;
      if (z === 'in') v.zoom(1.25); else if (z === 'out') v.zoom(0.8); else if (z === 'fit') v.fit(); else if (z === 'one') v.actual(); else if (z === 'close') closeLightbox();
    });
    if (img.complete && img.naturalWidth) v.fit(); else img.addEventListener('load', v.fit, { once: true });
    new ResizeObserver(() => { if (Math.abs(v.scale - v.fitScale) < 0.001) v.fit(); }).observe(stage);
    host._view = v;
    return v;
  }

  window.addEventListener('mousemove', ev => {
    if (!drag) return;
    drag.moved = true;
    drag.v.x = ev.clientX - drag.sx;
    drag.v.y = ev.clientY - drag.sy;
    drag.v.img.style.transform = `translate(${drag.v.x}px, ${drag.v.y}px) scale(${drag.v.scale})`;
  });
  window.addEventListener('mouseup', () => { if (drag) { drag.v.stage.classList.remove('grab'); drag = null; } });

  function openLightbox(src) {
    lightbox.classList.add('open');
    lightboxView = mountViewer(lightbox, src, true);
  }
  function closeLightbox() {
    if (!lightbox.classList.contains('open')) return;
    lightbox.classList.remove('open');
    lightbox.innerHTML = '';
    lightboxView = null;
  }
  function activeView() { return lightboxView || (content.querySelector('.imgview') || {})._view || null; }

  window.addEventListener('keydown', ev => {
    if (ev.key === 'Escape') { hideMenu(); closeLightbox(); return; }
    const v = activeView(); if (!v || ev.ctrlKey || ev.metaKey || ev.altKey) return;
    const step = 60;
    if (ev.key === '+' || ev.key === '=') v.zoom(1.25);
    else if (ev.key === '-') v.zoom(0.8);
    else if (ev.key === '0') v.fit();
    else if (ev.key === '1') v.actual();
    else if (ev.key === 'ArrowLeft') v.pan(step, 0);
    else if (ev.key === 'ArrowRight') v.pan(-step, 0);
    else if (ev.key === 'ArrowUp') v.pan(0, step);
    else if (ev.key === 'ArrowDown') v.pan(0, -step);
    else return;
    ev.preventDefault();
  });

  // ---- right-click menu for pictures ----

  function hideMenu() { menu.style.display = 'none'; }
  function showMenu(ev, items) {
    menu.innerHTML = '';
    for (const item of items) {
      if (!item) { menu.appendChild(document.createElement('hr')); continue; }
      const d = document.createElement('div');
      d.textContent = item[0];
      d.addEventListener('mousedown', e => { e.preventDefault(); e.stopPropagation(); hideMenu(); item[1](); });
      menu.appendChild(d);
    }
    menu.style.display = 'block';
    menu.style.left = Math.max(4, Math.min(ev.clientX, innerWidth - menu.offsetWidth - 6)) + 'px';
    menu.style.top = Math.max(4, Math.min(ev.clientY, innerHeight - menu.offsetHeight - 6)) + 'px';
  }
  document.addEventListener('contextmenu', ev => {
    const img = ev.target.closest('img');
    if (!img) { hideMenu(); return; }
    ev.preventDefault();
    const path = wikiPath(img.currentSrc || img.src);
    const inViewer = !!img.closest('.stage');
    const items = [];
    if (path) {
      items.push(['Show in Folder', () => post({ t: 'file', a: 'show', path })]);
      items.push(['Copy', () => post({ t: 'file', a: 'copy', path })]);
      items.push(['Copy Path', () => post({ t: 'file', a: 'copyPath', path })]);
      items.push(null);
      items.push(['Open in Default App', () => post({ t: 'file', a: 'open', path })]);
      if (path !== current) items.push(['Open in Wiki', () => post({ t: 'page', path })]);
    } else {
      items.push(['Copy Address', () => post({ t: 'copyText', text: img.src })]);
      items.push(['Open in Browser', () => post({ t: 'open', url: img.src })]);
    }
    if (!inViewer) { items.push(null); items.push(['Zoom…', () => openLightbox(img.currentSrc || img.src)]); }
    showMenu(ev, items);
  });
  document.addEventListener('mousedown', ev => { if (!menu.contains(ev.target)) hideMenu(); });
  window.addEventListener('blur', hideMenu);

  // ---- clicks ----

  document.addEventListener('click', ev => {
    const load = ev.target.closest('button[data-load]');
    if (load) {
      // A big file waits behind a button: swap in the real markup only now.
      const box = load.closest('.bigfile');
      const holder = document.createElement('div');
      holder.innerHTML = box.querySelector('template').innerHTML;
      for (const host of holder.querySelectorAll('.imgview')) mountViewer(host, host.dataset.src, false);
      box.replaceWith(...holder.childNodes);
      document.body.classList.add('wide');
      return;
    }
    const act = ev.target.closest('button[data-act]');
    if (act && current) { post({ t: 'file', a: act.dataset.act, path: current }); return; }
    const a = ev.target.closest('a');
    if (!a) {
      const img = ev.target.closest('#content img');
      if (img && !img.closest('.stage')) openLightbox(img.currentSrc || img.src);
      return;
    }
    if (a.dataset.tag) { ev.preventDefault(); post({ t: 'tag', tag: a.dataset.tag }); return; }
    const href = a.getAttribute('href'); if (!href) return;
    ev.preventDefault();
    if (href.startsWith('#')) { const t = document.getElementById(decodeURIComponent(href.slice(1))); if (t) t.scrollIntoView(); return; }
    const url = new URL(href, document.baseURI);
    if (url.origin !== location.origin) { post({ t: 'open', url: url.href }); return; }
    post({ t: 'page', path: decodeURIComponent(url.pathname.replace(/^\/wiki\/raw\//, '')) });
  });

  post({ t: 'ready' });
})();
